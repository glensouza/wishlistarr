using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.Library;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Search;

public enum Availability
{
    InLibrary,
    NotInLibrary,
}

public sealed record KidSearchItem(
    MediaKind Kind,
    int TmdbId,
    string Title,
    string? Year,
    string? Overview,
    string? PosterUrl,
    Availability? Availability,
    bool NeedsParent = false);

public sealed record KidSearchResponse(string Query, int Page, bool HasMore, IReadOnlyList<KidSearchItem> Results);

public sealed partial class KidSearch(
    WishlistarrDbContext db,
    ITmdbClient tmdb,
    IEnumerable<ILibraryIndex> indexes,
    TimeProvider time,
    ILogger<KidSearch> logger)
{
    public const int MinQueryLength = 2;
    public const int MaxQueryLength = 100;
    public const int MaxPage = 10;
    public const int MaxParallelLookups = 8;
    public const string PosterSize = "w342";
    public const string ProfileSize = "w185";
    public const string BackdropSize = "w1280";
    public const string PosterRoute = "/api/v1/kid/images/tmdb";

    public static bool IsImageSize(string size) => size is PosterSize or ProfileSize or BackdropSize;

    public static readonly TimeSpan LookupBudget = TimeSpan.FromSeconds(3);

    private const int MaxOverviewLength = 400;

    public static bool TryNormalize(string? query, out string normalized)
    {
        normalized = string.Join(' ', (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length is >= MinQueryLength and <= MaxQueryLength && !normalized.Any(char.IsControl);
    }

    public async Task<KidSearchResponse?> SearchAsync(int kidProfileId, string query, int page, CancellationToken ct = default)
    {
        KidProfile? profile = await db.KidProfiles.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == kidProfileId && row.ArchivedAt == null, ct);
        if (profile is null)
        {
            return null;
        }

        TmdbPage<TmdbSearchResult> found = await tmdb.SearchAsync(query, page: page, ct: ct);
        List<Candidate> candidates = [.. found.Results.Select(ToCandidate).OfType<Candidate>().DistinctBy(item => item.Key)];

        ContentPolicy policy = ContentPolicy.ForProfile(
            profile.MovieAgeLimit,
            profile.TvAgeLimit,
            profile.BlockedGenreIds,
            profile.BlockedKeywordIds,
            profile.AllowedOriginalLanguages);
        bool keywords = policy.BlockedKeywordIds.Count > 0;
        ConcurrentDictionary<TitleKey, KnownTitle> known = await this.LookUpAsync(candidates, keywords, ct);

        List<KidSearchItem> results = [];
        foreach (Candidate candidate in candidates)
        {
            if (!known.TryGetValue(candidate.Key, out KnownTitle? title))
            {
                continue;
            }

            ContentDecision decision = ContentPolicyEngine.Default.Evaluate(
                new ContentTitle(candidate.Key, title.Certifications, candidate.GenreIds, title.KeywordIds, candidate.OriginalLanguage),
                policy);
            if (decision.Outcome != ContentOutcome.Blocked)
            {
                results.Add(await this.ToItemAsync(candidate, decision.Outcome == ContentOutcome.NeedsApproval, ct));
            }
        }

        return new KidSearchResponse(query, page, page < Math.Min(found.TotalPages, MaxPage), results);
    }

    private static Candidate? ToCandidate(TmdbSearchResult result)
    {
        MediaKind? kind = result.MediaType switch
        {
            "movie" => MediaKind.Movie,
            "tv" => MediaKind.Show,
            _ => null,
        };
        string title = (kind == MediaKind.Movie ? result.Title : result.Name)?.Trim() ?? string.Empty;
        if (kind is null || result.Id <= 0 || title.Length == 0)
        {
            return null;
        }

        string? date = kind == MediaKind.Movie ? result.ReleaseDate : result.FirstAirDate;
        string? overview = result.Overview?.Trim();
        return new Candidate(
            new TitleKey(kind.Value, result.Id),
            title,
            date is { Length: >= 4 } && date[..4].All(char.IsAsciiDigit) ? date[..4] : null,
            string.IsNullOrEmpty(overview) ? null : overview[..Math.Min(overview.Length, MaxOverviewLength)],
            PosterUrl(result.PosterPath),
            result.GenreIds,
            result.OriginalLanguage);
    }

    private static string? PosterUrl(string? path)
    {
        string file = (path ?? string.Empty).TrimStart('/');
        return TmdbImageProxy.IsAllowed(PosterSize, file) ? $"{PosterRoute}/{PosterSize}/{file}" : null;
    }

    private async Task<ConcurrentDictionary<TitleKey, KnownTitle>> LookUpAsync(
        IReadOnlyList<Candidate> candidates,
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

    private async Task<KidSearchItem> ToItemAsync(Candidate candidate, bool needsParent, CancellationToken ct) =>
        new(
            candidate.Key.Kind,
            candidate.Key.TmdbId,
            candidate.Title,
            candidate.Year,
            needsParent ? null : candidate.Overview,
            needsParent ? null : candidate.PosterUrl,
            await this.AvailabilityAsync(candidate.Key, ct),
            needsParent);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Certification or keyword lookup failed for {Kind} {TmdbId}")]
    private partial void LogLookupFailed(Exception exception, MediaKind kind, int tmdbId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Certification and keyword lookups did not finish within {Seconds} s; unresolved titles were left out")]
    private partial void LogBudgetExceeded(double seconds);

    private async Task<Availability?> AvailabilityAsync(TitleKey title, CancellationToken ct)
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

    private sealed record Candidate(
        TitleKey Key,
        string Title,
        string? Year,
        string? Overview,
        string? PosterUrl,
        IReadOnlyList<int>? GenreIds,
        string? OriginalLanguage);

    private sealed record KnownTitle(IReadOnlyList<RegionCertification> Certifications, IReadOnlyList<int>? KeywordIds);
}
