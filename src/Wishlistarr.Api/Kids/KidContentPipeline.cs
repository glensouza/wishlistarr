using System.Collections.Concurrent;
using System.Globalization;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.Library;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Kids;

public enum Availability
{
    InLibrary,
    NotInLibrary,
}

public sealed record KidMediaItem(
    MediaKind Kind,
    int TmdbId,
    string Title,
    string? Year,
    string? Overview,
    string? PosterUrl,
    Availability? Availability,
    bool NeedsParent = false);

public sealed record KidCandidate(
    TitleKey Key,
    string Title,
    string? Year,
    string? Overview,
    string? PosterUrl,
    IReadOnlyList<int>? GenreIds,
    string? OriginalLanguage);

public sealed partial class KidContentPipeline(
    ITmdbClient tmdb,
    IEnumerable<ILibraryIndex> indexes,
    TimeProvider time,
    ILogger<KidContentPipeline> logger)
{
    public const int MaxParallelLookups = 8;
    public const string PosterSize = "w342";
    public const string PosterRoute = "/api/v1/kid/images/tmdb";
    public const int MaxOverviewLength = 400;

    public static readonly TimeSpan LookupBudget = TimeSpan.FromSeconds(3);

    public static KidCandidate? ToCandidate(TmdbSearchResult result)
    {
        MediaKind? kind = result.MediaType switch
        {
            "movie" => MediaKind.Movie,
            "tv" => MediaKind.Show,
            _ => null,
        };
        return kind is MediaKind known ? ToCandidate(result, known) : null;
    }

    public static KidCandidate? ToCandidate(TmdbSearchResult result, MediaKind kind)
    {
        string title = (kind == MediaKind.Movie ? result.Title : result.Name)?.Trim() ?? string.Empty;
        if (result.Id <= 0 || title.Length == 0)
        {
            return null;
        }

        string? date = kind == MediaKind.Movie ? result.ReleaseDate : result.FirstAirDate;
        string? overview = result.Overview?.Trim();
        return new KidCandidate(
            new TitleKey(kind, result.Id),
            title,
            date is { Length: >= 4 } && date[..4].All(char.IsAsciiDigit) ? date[..4] : null,
            string.IsNullOrEmpty(overview) ? null : overview[..Math.Min(overview.Length, MaxOverviewLength)],
            PosterUrl(result.PosterPath),
            result.GenreIds,
            result.OriginalLanguage);
    }

    public static KidCandidate? ToCandidate(TmdbDetails details, MediaKind kind)
    {
        string title = (kind == MediaKind.Movie ? details.Title : details.Name)?.Trim() ?? string.Empty;
        if (details.Id <= 0 || title.Length == 0)
        {
            return null;
        }

        string? date = kind == MediaKind.Movie ? details.ReleaseDate : details.FirstAirDate;
        string? overview = details.Overview?.Trim();
        return new KidCandidate(
            new TitleKey(kind, details.Id),
            title,
            date is { Length: >= 4 } && date[..4].All(char.IsAsciiDigit) ? date[..4] : null,
            string.IsNullOrEmpty(overview) ? null : overview[..Math.Min(overview.Length, MaxOverviewLength)],
            PosterUrl(details.PosterPath),
            details.Genres?.Select(genre => genre.Id).ToList(),
            details.OriginalLanguage);
    }

    public static string? PosterUrl(string? path)
    {
        string file = (path ?? string.Empty).TrimStart('/');
        return TmdbImageProxy.IsAllowed(PosterSize, file) ? $"{PosterRoute}/{PosterSize}/{file}" : null;
    }

    public async Task<IReadOnlyList<KidMediaItem>> FilterAsync(
        IReadOnlyList<KidCandidate> candidates,
        ContentPolicy policy,
        bool allowedOnly,
        CancellationToken ct = default)
    {
        bool keywords = policy.BlockedKeywordIds.Count > 0;
        ConcurrentDictionary<TitleKey, KnownTitle> known = await this.LookUpAsync(candidates, keywords, ct);

        List<KidMediaItem> results = [];
        foreach (KidCandidate candidate in candidates)
        {
            if (!known.TryGetValue(candidate.Key, out KnownTitle? title))
            {
                continue;
            }

            ContentDecision decision = ContentPolicyEngine.Default.Evaluate(
                new ContentTitle(candidate.Key, title.Certifications, candidate.GenreIds, title.KeywordIds, candidate.OriginalLanguage),
                policy);

            if (allowedOnly)
            {
                if (decision.Outcome == ContentOutcome.Allowed)
                {
                    results.Add(await this.ToItemAsync(candidate, needsParent: false, ct));
                }
            }
            else
            {
                if (decision.Outcome != ContentOutcome.Blocked)
                {
                    results.Add(await this.ToItemAsync(candidate, decision.Outcome == ContentOutcome.NeedsApproval, ct));
                }
            }
        }

        return results;
    }

    private async Task<ConcurrentDictionary<TitleKey, KnownTitle>> LookUpAsync(
        IReadOnlyList<KidCandidate> candidates,
        bool keywords,
        CancellationToken ct)
    {
        ConcurrentDictionary<TitleKey, KnownTitle> known = [];
        using CancellationTokenSource deadline = new(LookupBudget, time);
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            await Parallel.ForEachAsync(
                candidates,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelLookups, CancellationToken = budget.Token },
                async (candidate, token) =>
                {
                    try
                    {
                        TmdbMediaType type = candidate.Key.Kind == MediaKind.Movie ? TmdbMediaType.Movie : TmdbMediaType.Tv;
                        TmdbCertification? certification = await tmdb.GetCertificationAsync(type, candidate.Key.TmdbId, [RatingScale.UsRegion], token);
                        IReadOnlyList<int>? keywordIds = null;
                        if (keywords)
                        {
                            keywordIds = [.. (await tmdb.GetKeywordsAsync(type, candidate.Key.TmdbId, token)).Select(item => item.Id)];
                        }

                        known[candidate.Key] = new KnownTitle(
                            certification is null ? [] : [new RegionCertification(certification.Region, certification.Rating)],
                            keywordIds);
                    }
                    catch (Exception ex) when (ex is TmdbException or HttpRequestException || (ex is TaskCanceledException && !token.IsCancellationRequested))
                    {
                        this.LogLookupFailed(ex, candidate.Key.Kind, candidate.Key.TmdbId);
                    }
                });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            this.LogBudgetExceeded(LookupBudget.TotalSeconds);
        }

        return known;
    }

    private async Task<KidMediaItem> ToItemAsync(KidCandidate candidate, bool needsParent, CancellationToken ct) =>
        new(
            candidate.Key.Kind,
            candidate.Key.TmdbId,
            candidate.Title,
            candidate.Year,
            needsParent ? null : candidate.Overview,
            needsParent ? null : candidate.PosterUrl,
            await this.AvailabilityAsync(candidate.Key, ct),
            needsParent);

    public async Task<Availability?> AvailabilityAsync(TitleKey title, CancellationToken ct)
    {
        ExternalId id = new(ExternalIdSource.Tmdb, title.TmdbId.ToString(CultureInfo.InvariantCulture));
        foreach (ILibraryIndex index in indexes)
        {
            if (await index.FindAsync(title.Kind, id, ct) is IReadOnlyList<MediaItem> items)
            {
                return items.Any(item => item.Kind == title.Kind) ? Availability.InLibrary : Availability.NotInLibrary;
            }
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Certification or keyword lookup failed for {Kind} {TmdbId}")]
    private partial void LogLookupFailed(Exception exception, MediaKind kind, int tmdbId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Certification and keyword lookups did not finish within {Seconds} s; unresolved titles were left out")]
    private partial void LogBudgetExceeded(double seconds);

    private sealed record KnownTitle(IReadOnlyList<RegionCertification> Certifications, IReadOnlyList<int>? KeywordIds);
}
