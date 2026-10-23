using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.Kids;
using Wishlistarr.Core.Library;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Kids;

public sealed record KidHomeRow(string Id, int? GenreId, IReadOnlyList<KidMediaItem> Items);

public sealed record KidHomeResponse(IReadOnlyList<KidHomeRow> Rows);

public enum HomeLoadStatus
{
    Missing,
    Unavailable,
    Ready,
}

public sealed record HomeLoad(HomeLoadStatus Status, KidHomeResponse? Response)
{
    public static HomeLoad Missing() => new(HomeLoadStatus.Missing, null);

    public static HomeLoad Unavailable() => new(HomeLoadStatus.Unavailable, null);

    public static HomeLoad Ready(KidHomeResponse response) => new(HomeLoadStatus.Ready, response);
}

public sealed partial class KidHome(
    WishlistarrDbContext db,
    ITmdbClient tmdb,
    KidContentPipeline pipeline,
    IEnumerable<ILibraryIndex> indexes,
    TimeProvider time,
    ILogger<KidHome> logger)
{
    public const int MaxDiscoverRequests = 8;
    public const int MaxCandidatesPerRow = 8;
    public const int MaxLookupCandidates = 40;
    public const string PopularitySort = "popularity.desc";

    private readonly object gate = new();
    private int discoverCalls;
    private bool tmdbFailed;
    private bool tmdbResponded;

    public async Task<HomeLoad> LoadAsync(int profileId, CancellationToken ct = default)
    {
        KidProfile? profile = await db.KidProfiles.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == profileId && row.ArchivedAt == null, ct);
        if (profile is null)
        {
            return HomeLoad.Missing();
        }

        ContentPolicy policy = ContentPolicy.ForProfile(
            profile.MovieAgeLimit,
            profile.TvAgeLimit,
            profile.BlockedGenreIds,
            profile.BlockedKeywordIds,
            profile.AllowedOriginalLanguages);
        string? movieCeiling = MovieCertificationCeiling(profile.MovieAgeLimit);

        List<RowDraft> drafts = [];
        List<KidCandidate> pending = [];
        HashSet<TitleKey> seen = [];
        int remaining = MaxLookupCandidates;
        foreach (HomeSlot slot in Expand(profile.HomeRows, policy.BlockedGenreIds))
        {
            if (remaining <= 0)
            {
                break;
            }

            int take = Math.Min(MaxCandidatesPerRow, remaining);
            IReadOnlyList<KidCandidate> gathered = slot.Id == HomeRowTypes.Library
                ? await this.GatherLibraryAsync(take, ct)
                : await this.GatherDiscoverAsync(slot.GenreId, movieCeiling, take, ct);
            if (gathered.Count == 0)
            {
                continue;
            }

            List<TitleKey> keys = [];
            foreach (KidCandidate candidate in gathered)
            {
                keys.Add(candidate.Key);
                if (seen.Add(candidate.Key))
                {
                    pending.Add(candidate);
                    remaining--;
                }
            }

            drafts.Add(new RowDraft(slot.Id, slot.GenreId, keys));
        }

        if (this.FailedClosed)
        {
            return HomeLoad.Unavailable();
        }

        IReadOnlyList<KidMediaItem> allowed = pending.Count == 0
            ? []
            : await pipeline.FilterAsync(pending, policy, allowedOnly: true, ct);
        Dictionary<TitleKey, KidMediaItem> byKey = allowed.ToDictionary(item => new TitleKey(item.Kind, item.TmdbId));
        List<KidHomeRow> rows = [];
        foreach (RowDraft draft in drafts)
        {
            List<KidMediaItem> items = [];
            foreach (TitleKey key in draft.Keys)
            {
                if (byKey.TryGetValue(key, out KidMediaItem? item))
                {
                    items.Add(item);
                }
            }

            if (items.Count > 0)
            {
                rows.Add(new KidHomeRow(draft.Id, draft.GenreId, items));
            }
        }

        return HomeLoad.Ready(new KidHomeResponse(rows));
    }

    private bool FailedClosed
    {
        get
        {
            lock (this.gate)
            {
                return this.tmdbFailed && !this.tmdbResponded;
            }
        }
    }

    private bool TryReserveDiscover()
    {
        lock (this.gate)
        {
            if (this.discoverCalls >= MaxDiscoverRequests)
            {
                return false;
            }

            this.discoverCalls++;
            return true;
        }
    }

    private void Note(bool success)
    {
        lock (this.gate)
        {
            if (success)
            {
                this.tmdbResponded = true;
            }
            else
            {
                this.tmdbFailed = true;
            }
        }
    }

    private async Task<IReadOnlyList<KidCandidate>> GatherDiscoverAsync(
        int? genreId,
        string? movieCeiling,
        int take,
        CancellationToken ct)
    {
        List<KidCandidate> movies = await this.DiscoverAsync(TmdbMediaType.Movie, genreId, movieCeiling, ct);
        List<KidCandidate> shows = await this.DiscoverAsync(TmdbMediaType.Tv, genreId, movieCeiling, ct);
        List<KidCandidate> mixed = [];
        HashSet<TitleKey> seen = [];
        int movieIndex = 0;
        int showIndex = 0;
        while (mixed.Count < take && (movieIndex < movies.Count || showIndex < shows.Count))
        {
            if (movieIndex < movies.Count && mixed.Count < take && seen.Add(movies[movieIndex].Key))
            {
                mixed.Add(movies[movieIndex]);
            }

            movieIndex++;
            if (showIndex < shows.Count && mixed.Count < take && seen.Add(shows[showIndex].Key))
            {
                mixed.Add(shows[showIndex]);
            }

            showIndex++;
        }

        return mixed;
    }

    private async Task<List<KidCandidate>> DiscoverAsync(
        TmdbMediaType type,
        int? genreId,
        string? movieCeiling,
        CancellationToken ct)
    {
        if (!this.TryReserveDiscover())
        {
            return [];
        }

        bool movie = type == TmdbMediaType.Movie;
        try
        {
            TmdbPage<TmdbSearchResult> page = await tmdb.DiscoverAsync(
                type,
                new TmdbDiscoverQuery(
                    Genres: genreId?.ToString(CultureInfo.InvariantCulture),
                    CertificationCountry: movie && movieCeiling is not null ? RatingScale.UsRegion : null,
                    CertificationLessThanOrEqual: movie ? movieCeiling : null,
                    SortBy: PopularitySort),
                ct: ct);
            this.Note(true);
            MediaKind kind = movie ? MediaKind.Movie : MediaKind.Show;
            return [.. page.Results.Select(result => KidContentPipeline.ToCandidate(result, kind)).OfType<KidCandidate>()];
        }
        catch (TmdbNotConfiguredException)
        {
            throw;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is TmdbException or HttpRequestException or TaskCanceledException)
        {
            this.Note(false);
            this.LogDiscoverFailed(ex, type, genreId);
            return [];
        }
    }

    private async Task<IReadOnlyList<KidCandidate>> GatherLibraryAsync(int take, CancellationToken ct)
    {
        IReadOnlyList<LibraryCatalogEntry>? recent = null;
        foreach (ILibraryIndex index in indexes)
        {
            if (await index.ListRecentAsync(take, ct) is IReadOnlyList<LibraryCatalogEntry> listed)
            {
                recent = listed;
                break;
            }
        }

        if (recent is null || recent.Count == 0)
        {
            return [];
        }

        if (recent.Count > take)
        {
            recent = recent.Take(take).ToList();
        }

        IReadOnlyList<KidCandidate> resolved = await this.LoadDetailsAsync(recent, ct);
        this.Note(resolved.Count > 0);
        return resolved;
    }

    private async Task<IReadOnlyList<KidCandidate>> LoadDetailsAsync(
        IReadOnlyList<LibraryCatalogEntry> entries,
        CancellationToken ct)
    {
        ConcurrentDictionary<int, KidCandidate> found = [];
        object keyGate = new();
        bool missingKey = false;
        using CancellationTokenSource deadline = new(KidContentPipeline.LookupBudget, time);
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            await Parallel.ForEachAsync(
                entries.Select((entry, index) => (entry, index)),
                new ParallelOptions { MaxDegreeOfParallelism = KidContentPipeline.MaxParallelLookups, CancellationToken = budget.Token },
                async (item, token) =>
                {
                    try
                    {
                        TmdbMediaType type = item.entry.Kind == MediaKind.Movie ? TmdbMediaType.Movie : TmdbMediaType.Tv;
                        TmdbDetails details = await tmdb.GetDetailsAsync(type, item.entry.TmdbId, ct: token);
                        if (KidContentPipeline.ToCandidate(details, item.entry.Kind) is KidCandidate candidate)
                        {
                            found[item.index] = candidate;
                        }
                    }
                    catch (TmdbNotConfiguredException)
                    {
                        lock (keyGate)
                        {
                            missingKey = true;
                        }
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested && ex is TmdbException or HttpRequestException or TaskCanceledException)
                    {
                        this.LogDetailFailed(ex, item.entry.Kind, item.entry.TmdbId);
                    }
                });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            this.LogDetailBudget(KidContentPipeline.LookupBudget.TotalSeconds);
        }

        if (missingKey)
        {
            throw new TmdbNotConfiguredException();
        }

        List<KidCandidate> ordered = [];
        for (int index = 0; index < entries.Count; index++)
        {
            if (found.TryGetValue(index, out KidCandidate? candidate))
            {
                ordered.Add(candidate);
            }
        }

        return ordered;
    }

    private static string? MovieCertificationCeiling(int? ageLimit)
    {
        if (ageLimit is not int age)
        {
            return null;
        }

        return RatingScale.MaxLevelForAge(age) switch
        {
            RatingLevel.Preschool or RatingLevel.General or RatingLevel.Children => "G",
            RatingLevel.ParentalGuidance => "PG",
            RatingLevel.Teen => "PG-13",
            RatingLevel.Mature => "R",
            _ => "NC-17",
        };
    }

    private static IReadOnlyList<HomeSlot> Expand(IReadOnlyList<string> rows, IReadOnlySet<int> blocked)
    {
        List<HomeSlot> slots = [];
        HashSet<string> seen = [];
        foreach (string row in rows)
        {
            if (row is HomeRowTypes.Popular or HomeRowTypes.Library)
            {
                Add(new HomeSlot(row, null));
            }
            else if (row == HomeRowTypes.Genre)
            {
                foreach (int id in HomeRowTypes.DefaultGenreIds)
                {
                    AddGenre(id);
                }
            }
            else if (TryGenreId(row, out int genreId))
            {
                AddGenre(genreId);
            }
        }

        return slots;

        void AddGenre(int id)
        {
            if (!blocked.Contains(id))
            {
                Add(new HomeSlot($"genre:{id.ToString(CultureInfo.InvariantCulture)}", id));
            }
        }

        void Add(HomeSlot slot)
        {
            if (seen.Add(slot.Id))
            {
                slots.Add(slot);
            }
        }
    }

    private static bool TryGenreId(string row, out int genreId)
    {
        const string Prefix = "genre:";
        genreId = 0;
        return row.StartsWith(Prefix, StringComparison.Ordinal)
            && int.TryParse(row.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out genreId)
            && genreId > 0;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Home discover failed for {Type} genre {GenreId}")]
    private partial void LogDiscoverFailed(Exception exception, TmdbMediaType type, int? genreId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Library detail lookup failed for {Kind} {TmdbId}")]
    private partial void LogDetailFailed(Exception exception, MediaKind kind, int tmdbId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Library detail lookups did not finish within {Seconds} s; unresolved titles were left out")]
    private partial void LogDetailBudget(double seconds);

    private readonly record struct HomeSlot(string Id, int? GenreId);

    private sealed record RowDraft(string Id, int? GenreId, IReadOnlyList<TitleKey> Keys);
}
