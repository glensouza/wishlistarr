using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.Library;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Dtdd;
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
    ILogger<KidContentPipeline> logger,
    WishlistarrDbContext db,
    IDtddClient dtdd)
{
    public KidContentPipeline(
        ITmdbClient tmdb,
        IEnumerable<ILibraryIndex> indexes,
        TimeProvider time,
        ILogger<KidContentPipeline> logger,
        WishlistarrDbContext db)
        : this(tmdb, indexes, time, logger, db, MissingDtddClient.Instance)
    {
    }

    public const int MaxParallelLookups = 8;
    public const string PosterSize = "w342";
    public const string PosterRoute = "/api/v1/kid/images/tmdb";
    public const int MaxOverviewLength = 400;

    public static readonly TimeSpan LookupBudget = TimeSpan.FromSeconds(3);

    private sealed class MissingDtddClient : IDtddClient
    {
        public static MissingDtddClient Instance { get; } = new();

        public Task<IReadOnlyList<string>> GetWarningsAsync(MediaKind kind, int tmdbId, CancellationToken ct = default) =>
            throw new DtddNotConfiguredException();

        public Task<bool> TestApiKeyAsync(CancellationToken ct = default) =>
            throw new DtddNotConfiguredException();
    }

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
        int profileId,
        bool allowedOnly,
        CancellationToken ct = default)
    {
        HashSet<TitleKey> revoked = [.. (await db.MediaRequests.AsNoTracking()
            .Where(row => row.KidProfileId == profileId && row.State == RequestState.Revoked)
            .Select(row => new { row.Kind, row.TmdbId }).ToListAsync(ct))
            .Select(row => new TitleKey(row.Kind, row.TmdbId))];
        ConcurrentDictionary<TitleKey, ContentDecision> decisions = [];
        using CancellationTokenSource deadline = new(LookupBudget, time);
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            await Parallel.ForEachAsync(candidates,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelLookups, CancellationToken = budget.Token },
                async (candidate, token) =>
                {
                    if (await KidPolicyEvaluator.EvaluateAsync(tmdb, candidate.Key, policy, revoked.Contains(candidate.Key), token, dtdd: dtdd) is { } evaluation)
                        decisions[candidate.Key] = evaluation.Decision;
                });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            this.LogBudgetExceeded(LookupBudget.TotalSeconds);
        }
        List<KidMediaItem> results = [];
        foreach (KidCandidate candidate in candidates)
        {
            if (decisions.TryGetValue(candidate.Key, out ContentDecision? decision) && KidPolicyEvaluator.Visible(decision, allowedOnly))
                results.Add(await this.ToItemAsync(candidate, decision.Outcome == ContentOutcome.NeedsApproval, ct));
        }
        return results;
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Certification and keyword lookups did not finish within {Seconds} s; unresolved titles were left out")]
    private partial void LogBudgetExceeded(double seconds);

}
