using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wishlistarr.Api.Kids;
using Wishlistarr.Api.Requests;
using Wishlistarr.Api.Settings;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;
using Wishlistarr.Tests.Tmdb;

namespace Wishlistarr.Tests.Requests;

public sealed class RequestQueueBudgetTests
{
    [Fact]
    public async Task A_hanging_tmdb_call_is_cut_off_per_call_and_the_page_still_loads_with_gaps()
    {
        using KidRequestApiFactory factory = new();
        int profileId = await SeedAsync(factory, 6);
        using IServiceScope scope = factory.Services.CreateScope();
        ManualTimeProvider time = new();
        SlowTmdb tmdb = new(time) { Hang = true };

        Task<ApprovalQueuePage?> pending = CreateQueue(scope, tmdb, time).GetAsync(profileId, 50, 0, CancellationToken.None);
        await WaitUntilAsync(() => tmdb.Running == RequestQueue.MaxParallelLookups);
        Assert.False(pending.IsCompleted);
        time.Advance(RequestQueue.CallLimit);
        await WaitUntilAsync(() => tmdb.DetailCalls == 6 && tmdb.Running == 2);
        Assert.False(pending.IsCompleted);
        time.Advance(RequestQueue.CallLimit);
        ApprovalQueuePage? page = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(RequestQueue.CallLimit * 2 < RequestQueue.PageBudget);
        Assert.NotNull(page);
        Assert.Equal(6, page.Items.Count);
        Assert.All(page.Items, item =>
        {
            Assert.StartsWith("Movie ", item.Title, StringComparison.Ordinal);
            Assert.Null(item.Overview);
            Assert.Equal([QueueGap.Synopsis, QueueGap.Rating, QueueGap.Providers], item.Gaps);
            Assert.Equal(QueueReasonCode.Unavailable, item.Reason.Code);
        });
        Assert.InRange(tmdb.Peak, 1, RequestQueue.MaxParallelLookups);
        Assert.Equal(0, tmdb.Running);
    }

    [Fact]
    public async Task When_the_page_budget_runs_out_the_queue_stops_calling_tmdb_and_reports_gaps()
    {
        using KidRequestApiFactory factory = new();
        int profileId = await SeedAsync(factory, 6);
        using IServiceScope scope = factory.Services.CreateScope();
        ManualTimeProvider time = new();
        SlowTmdb tmdb = new(time) { Hang = true };

        Task<ApprovalQueuePage?> pending = CreateQueue(scope, tmdb, time).GetAsync(profileId, 50, 0, CancellationToken.None);
        await WaitUntilAsync(() => tmdb.Running == RequestQueue.MaxParallelLookups);
        time.Advance(RequestQueue.PageBudget);
        ApprovalQueuePage? page = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(page);
        Assert.Equal(6, page.Total);
        Assert.Equal(6, page.Items.Count);
        Assert.All(page.Items, item =>
        {
            Assert.Equal([QueueGap.Synopsis, QueueGap.Rating, QueueGap.Providers], item.Gaps);
            Assert.Equal(QueueReasonCode.Unavailable, item.Reason.Code);
        });
        Assert.Equal(RequestQueue.MaxParallelLookups, tmdb.DetailCalls);
        Assert.Equal(0, tmdb.Running);
    }

    [Fact]
    public async Task When_the_budget_runs_out_after_the_details_call_the_synopsis_stays_and_only_the_missing_parts_are_gaps()
    {
        using KidRequestApiFactory factory = new();
        int profileId = await SeedAsync(factory, 3);
        using IServiceScope scope = factory.Services.CreateScope();
        ManualTimeProvider time = new();
        SlowTmdb tmdb = new(time) { HangCertification = true };

        Task<ApprovalQueuePage?> pending = CreateQueue(scope, tmdb, time).GetAsync(profileId, 50, 0, CancellationToken.None);
        await WaitUntilAsync(() => tmdb.Running == 3);
        time.Advance(RequestQueue.PageBudget);
        ApprovalQueuePage? page = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(page);
        Assert.Equal(3, page.Items.Count);
        Assert.All(page.Items, item =>
        {
            Assert.Equal("A short story.", item.Overview);
            Assert.Null(item.Rating);
            Assert.Equal([QueueGap.Rating, QueueGap.Providers], item.Gaps);
            Assert.Equal(QueueReasonCode.Unavailable, item.Reason.Code);
        });
        Assert.Equal(0, tmdb.Running);
    }

    [Fact]
    public async Task A_page_that_runs_out_of_budget_logs_one_warning_and_not_one_per_item()
    {
        using KidRequestApiFactory factory = new();
        int profileId = await SeedAsync(factory, 10);
        using IServiceScope scope = factory.Services.CreateScope();
        ManualTimeProvider time = new();
        SlowTmdb tmdb = new(time) { HangCertification = true };
        CapturingLogger logger = new();

        Task<ApprovalQueuePage?> pending = CreateQueue(scope, tmdb, time, logger: logger).GetAsync(profileId, 50, 0, CancellationToken.None);
        await WaitUntilAsync(() => tmdb.Running == RequestQueue.MaxParallelLookups);
        time.Advance(RequestQueue.PageBudget);
        await pending.WaitAsync(TimeSpan.FromSeconds(10));

        string warning = Assert.Single(logger.Warnings);
        Assert.Contains("stopped calling TMDB", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_real_tmdb_failure_on_an_item_is_still_logged()
    {
        using KidRequestApiFactory factory = new();
        int profileId = await SeedAsync(factory, 2);
        using IServiceScope scope = factory.Services.CreateScope();
        ManualTimeProvider time = new();
        SlowTmdb tmdb = new(time) { FailProviders = true };
        CapturingLogger logger = new();

        ApprovalQueuePage? page = await CreateQueue(scope, tmdb, time, logger: logger).GetAsync(profileId, 50, 0, CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal(2, logger.Warnings.Count(line => line.Contains("watch providers", StringComparison.Ordinal)));
        Assert.All(page.Items, item => Assert.Equal([QueueGap.Providers], item.Gaps));
    }

    [Fact]
    public async Task A_missing_tmdb_key_is_reported_on_the_page_and_no_tmdb_call_is_made()
    {
        using KidRequestApiFactory factory = new();
        int profileId = await SeedAsync(factory, 3);
        using IServiceScope scope = factory.Services.CreateScope();
        ManualTimeProvider time = new();
        SlowTmdb tmdb = new(time);
        CapturingLogger logger = new();

        ApprovalQueuePage? page = await CreateQueue(scope, tmdb, time, new FixedKeyProvider { Key = null }, logger)
            .GetAsync(profileId, 50, 0, CancellationToken.None);

        Assert.NotNull(page);
        Assert.False(page.TmdbConfigured);
        Assert.Equal(3, page.Items.Count);
        Assert.All(page.Items, item => Assert.Equal([QueueGap.Synopsis, QueueGap.Rating, QueueGap.Providers], item.Gaps));
        Assert.Equal(0, tmdb.DetailCalls);
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public async Task A_configured_key_is_reported_as_configured()
    {
        using KidRequestApiFactory factory = new();
        int profileId = await SeedAsync(factory, 1);
        using IServiceScope scope = factory.Services.CreateScope();
        ManualTimeProvider time = new();

        ApprovalQueuePage? page = await CreateQueue(scope, new SlowTmdb(time), time).GetAsync(profileId, 50, 0, CancellationToken.None);

        Assert.NotNull(page);
        Assert.True(page.TmdbConfigured);
    }

    [Fact]
    public async Task The_shared_evaluator_still_throws_when_cancelled_unless_the_caller_asks_to_keep_what_was_read()
    {
        ManualTimeProvider time = new();
        SlowTmdb tmdb = new(time) { HangCertification = true };
        TitleKey key = new(MediaKind.Movie, 700);
        using CancellationTokenSource cancel = new();

        Task<PolicyEvaluation?> strict = KidPolicyEvaluator.EvaluateAsync(tmdb, key, new ContentPolicy(), false, cancel.Token);
        await WaitUntilAsync(() => tmdb.Running == 1);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => strict);

        using CancellationTokenSource cancelKept = new();
        Task<PolicyEvaluation?> kept = KidPolicyEvaluator.EvaluateAsync(tmdb, key, new ContentPolicy(), false, cancelKept.Token, keepPartialOnCancel: true);
        await WaitUntilAsync(() => tmdb.Running == 1);
        await cancelKept.CancelAsync();
        PolicyEvaluation? evaluation = await kept;

        Assert.NotNull(evaluation);
        Assert.Equal(ContentOutcome.Blocked, evaluation.Decision.Outcome);
        Assert.Equal(ContentReasonCode.MetadataUnavailable, evaluation.Decision.Reason.Code);
        Assert.Equal("A short story.", evaluation.Details?.Overview);
        Assert.Equal(PolicyLookup.Certification, evaluation.FailedLookup);
    }

    [Fact]
    public async Task Slow_answers_within_the_budget_return_every_item_in_full()
    {
        using KidRequestApiFactory factory = new();
        int profileId = await SeedAsync(factory, 8);
        using IServiceScope scope = factory.Services.CreateScope();
        ManualTimeProvider time = new();
        TimeSpan delay = TimeSpan.FromSeconds(1);
        SlowTmdb tmdb = new(time) { Delay = delay };

        Task<ApprovalQueuePage?> pending = CreateQueue(scope, tmdb, time).GetAsync(profileId, 50, 0, CancellationToken.None);
        const int steps = 6;
        for (int step = 1; step <= steps; step++)
        {
            int expected = step * RequestQueue.MaxParallelLookups;
            await WaitUntilAsync(() => tmdb.Started == expected);
            time.Advance(delay);
        }

        ApprovalQueuePage? page = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(delay * steps < RequestQueue.PageBudget);
        Assert.True(delay * 3 * 8 > RequestQueue.PageBudget);
        Assert.NotNull(page);
        Assert.Equal(8, page.Items.Count);
        Assert.All(page.Items, item =>
        {
            Assert.Equal("A short story.", item.Overview);
            Assert.Equal("PG", item.Rating);
            Assert.Equal(["Stream"], item.Providers.Select(provider => provider.Name));
            Assert.Empty(item.Gaps);
            Assert.Equal(QueueReasonCode.AutoApproveOff, item.Reason.Code);
        });
        Assert.Equal(RequestQueue.MaxParallelLookups, tmdb.Peak);
    }

    [Fact]
    public async Task Rate_limit_waits_longer_than_the_call_limit_become_gaps()
    {
        using KidRequestApiFactory factory = new();
        int profileId = await SeedAsync(factory, 4);
        ManualTimeProvider time = new();
        ServiceCollection services = new();
        services.AddTmdb<FixedKeyProvider>();
        services.AddSingleton<ITmdbApiKeyProvider>(new FixedKeyProvider());
        services.AddSingleton<TimeProvider>(time);
        services.Configure<TmdbOptions>(options =>
        {
            options.ApiBaseAddress = factory.Tmdb.ApiBaseAddress;
            options.ImageBaseAddress = factory.Tmdb.ImageBaseAddress;
        });
        await using ServiceProvider provider = services.BuildServiceProvider();
        foreach (int id in KidRequestApiFactory.NotInLibrary)
        {
            factory.Tmdb.Enqueue($"/3/movie/{id}", StatusCodes.Status429TooManyRequests, retryAfter: "20");
        }

        using IServiceScope scope = factory.Services.CreateScope();
        Task<ApprovalQueuePage?> pending = CreateQueue(scope, provider.GetRequiredService<ITmdbClient>(), time)
            .GetAsync(profileId, 50, 0, CancellationToken.None);
        await WaitUntilAsync(() => KidRequestApiFactory.NotInLibrary.All(id => factory.Tmdb.Count($"/3/movie/{id}") == 1));
        Assert.False(pending.IsCompleted);
        time.Advance(RequestQueue.CallLimit);
        ApprovalQueuePage? page = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(page);
        Assert.Equal(4, page.Items.Count);
        Assert.All(page.Items, item =>
        {
            Assert.Equal([QueueGap.Synopsis, QueueGap.Rating, QueueGap.Providers], item.Gaps);
            Assert.Equal(QueueReasonCode.Unavailable, item.Reason.Code);
        });
        Assert.All(KidRequestApiFactory.NotInLibrary, id => Assert.Equal(1, factory.Tmdb.Count($"/3/movie/{id}")));
    }

    [Fact]
    public async Task A_caller_that_goes_away_cancels_every_tmdb_call()
    {
        using KidRequestApiFactory factory = new();
        int profileId = await SeedAsync(factory, 6);
        using IServiceScope scope = factory.Services.CreateScope();
        ManualTimeProvider time = new();
        SlowTmdb tmdb = new(time) { Hang = true };
        using CancellationTokenSource caller = new();

        Task<ApprovalQueuePage?> pending = CreateQueue(scope, tmdb, time).GetAsync(profileId, 50, 0, caller.Token);
        await WaitUntilAsync(() => tmdb.Running == RequestQueue.MaxParallelLookups);
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, tmdb.Running);
        Assert.Equal(RequestQueue.MaxParallelLookups, tmdb.DetailCalls);
    }

    private static RequestQueue CreateQueue(IServiceScope scope, ITmdbClient tmdb, TimeProvider time, ITmdbApiKeyProvider? keys = null, ILogger<RequestQueue>? logger = null) =>
        new(
            scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>(),
            tmdb,
            keys ?? new FixedKeyProvider(),
            scope.ServiceProvider.GetRequiredService<RequestTitles>(),
            scope.ServiceProvider.GetRequiredService<SettingsStore>(),
            time,
            logger ?? NullLogger<RequestQueue>.Instance);

    private static async Task<int> SeedAsync(KidRequestApiFactory factory, int count)
    {
        (_, int profileId) = await factory.SignInKidAsync();
        using IServiceScope scope = factory.Services.CreateScope();
        MediaRequests requests = scope.ServiceProvider.GetRequiredService<MediaRequests>();
        for (int index = 0; index < count; index++)
        {
            int tmdbId = 700 + index;
            RequestResult result = await requests.CreateAsync(
                profileId,
                new TitleKey(MediaKind.Movie, tmdbId),
                $"Movie {tmdbId}",
                RequestActor.Kid(profileId, "Ada"));
            Assert.Equal(RequestOutcome.Created, result.Outcome);
        }

        return profileId;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, guard.Token);
        }
    }

    private sealed class CapturingLogger : ILogger<RequestQueue>
    {
        private readonly List<string> warnings = [];

        public IReadOnlyList<string> Warnings
        {
            get
            {
                lock (this.warnings)
                {
                    return [.. this.warnings];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                lock (this.warnings)
                {
                    this.warnings.Add(formatter(state, exception));
                }
            }
        }
    }

    private sealed class SlowTmdb(TimeProvider time) : ITmdbClient
    {
        private int running;
        private int peak;
        private int started;
        private int detailCalls;

        public bool Hang { get; init; }

        public bool HangCertification { get; init; }

        public bool FailProviders { get; init; }

        public TimeSpan Delay { get; init; }

        public int Running => Volatile.Read(ref this.running);

        public int Peak => Volatile.Read(ref this.peak);

        public int Started => Volatile.Read(ref this.started);

        public int DetailCalls => Volatile.Read(ref this.detailCalls);

        public async Task<TmdbDetails> GetDetailsAsync(TmdbMediaType type, int id, string language = "en-US", CancellationToken ct = default)
        {
            Interlocked.Increment(ref this.detailCalls);
            await this.WaitAsync(ct);
            return new TmdbDetails(id, $"Movie {id}", null, "A short story.", null, null, null, null, null, null, null, null, [new TmdbGenre(28, "Action")], 7, "en");
        }

        public async Task<TmdbCertification?> GetCertificationAsync(TmdbMediaType type, int id, IReadOnlyList<string> regions, CancellationToken ct = default)
        {
            await this.WaitAsync(ct, this.HangCertification);
            return new TmdbCertification("US", "PG", []);
        }

        public async Task<TmdbWatchProviders> GetWatchProvidersAsync(TmdbMediaType type, int id, CancellationToken ct = default)
        {
            if (this.FailProviders)
            {
                throw new TmdbException("Providers are down.");
            }

            await this.WaitAsync(ct);
            return new TmdbWatchProviders(new Dictionary<string, TmdbRegionProviders>
            {
                ["US"] = new(null, [new TmdbProvider(8, "Stream", null)], null, null),
            });
        }

        public Task<IReadOnlyList<TmdbKeyword>> GetKeywordsAsync(TmdbMediaType type, int id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TmdbVideos> GetVideosAsync(TmdbMediaType type, int id, string language = "en-US", CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> TestApiKeyAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TmdbPage<TmdbSearchResult>> SearchAsync(string query, TmdbMediaType? type = null, int page = 1, string language = "en-US", CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TmdbCredits> GetCreditsAsync(TmdbMediaType type, int id, string language = "en-US", CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int?> GetTvdbIdAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();


        public Task<TmdbImages> GetImagesAsync(TmdbMediaType type, int id, string imageLanguages = "en,null", CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TmdbPage<TmdbSearchResult>> DiscoverAsync(TmdbMediaType type, TmdbDiscoverQuery query, string language = "en-US", CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TmdbPage<TmdbSearchResult>> GetTrendingAsync(TmdbMediaType type, string window = "week", int page = 1, string language = "en-US", CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int?> FindIdAsync(TmdbMediaType type, ExternalIdSource source, string externalId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<TmdbListedGenre>> GetGenresAsync(string language = "en-US", CancellationToken ct = default) => throw new NotSupportedException();

        private async Task WaitAsync(CancellationToken ct, bool hang = false)
        {
            int now = Interlocked.Increment(ref this.running);
            int seen;
            while (now > (seen = Volatile.Read(ref this.peak)) && Interlocked.CompareExchange(ref this.peak, now, seen) != seen)
            {
            }

            try
            {
                Task wait = this.Hang || hang ? Task.Delay(Timeout.Infinite, ct) : Task.Delay(this.Delay, time, ct);
                Interlocked.Increment(ref this.started);
                await wait;
            }
            finally
            {
                Interlocked.Decrement(ref this.running);
            }
        }
    }
}
