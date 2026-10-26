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

public sealed record KidHomeRow(string Id, int? GenreId, string? GenreName, IReadOnlyList<KidMediaItem> Items);

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
    public const int MaxParallelDiscover = 4;
    public const int MaxCandidatesPerRow = 8;
    public const string PopularitySort = "popularity.desc";

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
        IReadOnlyList<HomeSlot> slots = Expand(profile.HomeRows, policy.BlockedGenreIds);
        TmdbTally tally = new();
        Gathered gathered = await this.GatherAsync(slots, MovieCertificationCeiling(profile.MovieAgeLimit), tally, ct);
        if (tally.FailedClosed)
        {
            return HomeLoad.Unavailable();
        }

        List<RowDraft> drafts = [];
        List<KidCandidate> pending = [];
        HashSet<TitleKey> seen = [];
        for (int index = 0; index < slots.Count; index++)
        {
            if (gathered.Rows[index] is not { Count: > 0 } candidates)
            {
                continue;
            }

            foreach (KidCandidate candidate in candidates)
            {
                if (seen.Add(candidate.Key))
                {
                    pending.Add(candidate);
                }
            }

            drafts.Add(new RowDraft(slots[index], [.. candidates.Select(candidate => candidate.Key)]));
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
                int? genreId = draft.Slot.GenreId;
                string? genreName = genreId is int id ? gathered.GenreNames.GetValueOrDefault(id) : null;
                rows.Add(new KidHomeRow(draft.Slot.Id, genreId, genreName, items));
            }
        }

        return HomeLoad.Ready(new KidHomeResponse(rows));
    }

    private static List<KidCandidate> Interleave(List<KidCandidate> movies, List<KidCandidate> shows, int take)
    {
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
        int discoverRows = 0;
        foreach (string row in rows)
        {
            if (row == HomeRowTypes.Library)
            {
                Add(new HomeSlot(row, null));
            }
            else if (row == HomeRowTypes.Popular)
            {
                AddDiscover(new HomeSlot(row, null));
            }
            else if (row == HomeRowTypes.Genre)
            {
                foreach (int id in HomeRowTypes.DefaultGenreIds)
                {
                    AddGenre(id);
                }
            }
            else if (HomeRowTypes.TryParseGenre(row, out int genreId))
            {
                AddGenre(genreId);
            }
        }

        return slots;

        void AddGenre(int id)
        {
            if (!blocked.Contains(id))
            {
                AddDiscover(new HomeSlot(HomeRowTypes.GenreRow(id), id));
            }
        }

        void AddDiscover(HomeSlot slot)
        {
            if (discoverRows < MaxDiscoverRequests / 2 && Add(slot))
            {
                discoverRows++;
            }
        }

        bool Add(HomeSlot slot)
        {
            if (!seen.Add(slot.Id))
            {
                return false;
            }

            slots.Add(slot);
            return true;
        }
    }

    private async Task<Gathered> GatherAsync(
        IReadOnlyList<HomeSlot> slots,
        string? movieCeiling,
        TmdbTally tally,
        CancellationToken ct)
    {
        IReadOnlyList<KidCandidate>?[] rows = new IReadOnlyList<KidCandidate>?[slots.Count];
        using CancellationTokenSource deadline = new(KidContentPipeline.LookupBudget, time);
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        using SemaphoreSlim discoverLimit = new(MaxParallelDiscover);
        List<Task> tasks = [];
        Task<IReadOnlyDictionary<int, TmdbListedGenre>> genres = Task.FromResult<IReadOnlyDictionary<int, TmdbListedGenre>>(
            new Dictionary<int, TmdbListedGenre>());
        if (slots.Any(slot => slot.GenreId is not null))
        {
            genres = this.LoadGenresAsync(budget.Token);
            tasks.Add(genres);
        }

        for (int index = 0; index < slots.Count; index++)
        {
            tasks.Add(GatherRowAsync(index, slots[index]));
        }

        Task all = Task.WhenAll(tasks);
        try
        {
            await all.WaitAsync(budget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            this.LogGatherBudget(KidContentPipeline.LookupBudget.TotalSeconds);
        }
        finally
        {
            await budget.CancelAsync();
            await all.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        IReadOnlyList<KidCandidate>?[] finished = new IReadOnlyList<KidCandidate>?[rows.Length];
        for (int index = 0; index < rows.Length; index++)
        {
            finished[index] = Volatile.Read(ref rows[index]);
        }

        return new Gathered(
            finished,
            genres.IsCompletedSuccessfully
                ? genres.Result.ToDictionary(pair => pair.Key, pair => pair.Value.Name)
                : []);

        async Task GatherRowAsync(int index, HomeSlot slot)
        {
            IReadOnlyList<KidCandidate> gathered = slot.Id == HomeRowTypes.Library
                ? await this.GatherLibraryAsync(tally, budget.Token)
                : await this.GatherDiscoverAsync(slot.GenreId, genres, movieCeiling, tally, discoverLimit, budget.Token);
            Volatile.Write(ref rows[index], gathered);
        }
    }

    private async Task<IReadOnlyList<KidCandidate>> GatherDiscoverAsync(
        int? genreId,
        Task<IReadOnlyDictionary<int, TmdbListedGenre>> genres,
        string? movieCeiling,
        TmdbTally tally,
        SemaphoreSlim limit,
        CancellationToken ct)
    {
        bool movies = true;
        bool shows = true;
        if (genreId is int id && (await genres.WaitAsync(ct)).TryGetValue(id, out TmdbListedGenre? listed))
        {
            movies = listed.Movie;
            shows = listed.Tv;
        }

        Task<List<KidCandidate>> movieResults = movies ? DiscoverLimitedAsync(TmdbMediaType.Movie) : Task.FromResult<List<KidCandidate>>([]);
        Task<List<KidCandidate>> showResults = shows ? DiscoverLimitedAsync(TmdbMediaType.Tv) : Task.FromResult<List<KidCandidate>>([]);
        await Task.WhenAll(movieResults, showResults);
        return Interleave(movieResults.Result, showResults.Result, MaxCandidatesPerRow);

        async Task<List<KidCandidate>> DiscoverLimitedAsync(TmdbMediaType type)
        {
            await limit.WaitAsync(ct);
            try
            {
                return await this.DiscoverAsync(type, genreId, movieCeiling, tally, ct);
            }
            finally
            {
                limit.Release();
            }
        }
    }

    private async Task<List<KidCandidate>> DiscoverAsync(
        TmdbMediaType type,
        int? genreId,
        string? movieCeiling,
        TmdbTally tally,
        CancellationToken ct)
    {
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
            tally.Note(true);
            MediaKind kind = movie ? MediaKind.Movie : MediaKind.Show;
            return [.. page.Results.Select(result => KidContentPipeline.ToCandidate(result, kind)).OfType<KidCandidate>()];
        }
        catch (TmdbNotConfiguredException)
        {
            throw;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is TmdbException or HttpRequestException or TaskCanceledException)
        {
            tally.Note(false);
            this.LogDiscoverFailed(ex, type, genreId);
            return [];
        }
    }

    private async Task<IReadOnlyDictionary<int, TmdbListedGenre>> LoadGenresAsync(CancellationToken ct)
    {
        try
        {
            return (await tmdb.GetGenresAsync(ct: ct)).ToDictionary(genre => genre.Id);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is TmdbException or HttpRequestException or TaskCanceledException)
        {
            this.LogGenresFailed(ex);
            return new Dictionary<int, TmdbListedGenre>();
        }
    }

    private async Task<IReadOnlyList<KidCandidate>> GatherLibraryAsync(TmdbTally tally, CancellationToken ct)
    {
        IReadOnlyList<LibraryCatalogEntry>? recent = null;
        foreach (ILibraryIndex index in indexes)
        {
            if (await index.ListRecentAsync(MaxCandidatesPerRow, ct) is IReadOnlyList<LibraryCatalogEntry> listed)
            {
                recent = listed;
                break;
            }
        }

        if (recent is null || recent.Count == 0)
        {
            return [];
        }

        IReadOnlyList<KidCandidate> resolved = await this.LoadDetailsAsync(recent, ct);
        tally.Note(resolved.Count > 0);
        return resolved;
    }

    private async Task<IReadOnlyList<KidCandidate>> LoadDetailsAsync(
        IReadOnlyList<LibraryCatalogEntry> entries,
        CancellationToken ct)
    {
        ConcurrentDictionary<int, KidCandidate> found = [];
        await Parallel.ForEachAsync(
            entries.Select((entry, index) => (entry, index)),
            new ParallelOptions { MaxDegreeOfParallelism = KidContentPipeline.MaxParallelLookups, CancellationToken = ct },
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
                    throw;
                }
                catch (Exception ex) when (!token.IsCancellationRequested && ex is TmdbException or HttpRequestException or TaskCanceledException)
                {
                    this.LogDetailFailed(ex, item.entry.Kind, item.entry.TmdbId);
                }
            });

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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Home discover failed for {Type} genre {GenreId}")]
    private partial void LogDiscoverFailed(Exception exception, TmdbMediaType type, int? genreId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Library detail lookup failed for {Kind} {TmdbId}")]
    private partial void LogDetailFailed(Exception exception, MediaKind kind, int tmdbId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Genre names could not be loaded")]
    private partial void LogGenresFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Home rows did not finish gathering within {Seconds} s; unfinished rows were left out")]
    private partial void LogGatherBudget(double seconds);

    private readonly record struct HomeSlot(string Id, int? GenreId);

    private sealed record RowDraft(HomeSlot Slot, IReadOnlyList<TitleKey> Keys);

    private sealed record Gathered(IReadOnlyList<IReadOnlyList<KidCandidate>?> Rows, IReadOnlyDictionary<int, string> GenreNames);

    private sealed class TmdbTally
    {
        private int succeeded;
        private int failed;

        public bool FailedClosed => Volatile.Read(ref this.failed) > 0 && Volatile.Read(ref this.succeeded) == 0;

        public void Note(bool success) => Interlocked.Increment(ref success ? ref this.succeeded : ref this.failed);
    }
}
