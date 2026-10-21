using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Library;
using Wishlistarr.Api.Requests;
using Wishlistarr.Api.Settings;
using Wishlistarr.Api.Tmdb;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;
using Wishlistarr.Tests.Jobs;
using Wishlistarr.Tests.Library;

namespace Wishlistarr.Tests.Tmdb;

public sealed class RetentionHarness : IAsyncDisposable
{
    private readonly string directory;

    private RetentionHarness(ServiceProvider provider, string directory)
    {
        this.Provider = provider;
        this.directory = directory;
    }

    public ServiceProvider Provider { get; }

    public FakeTmdbFinder Tmdb => this.Provider.GetRequiredService<FakeTmdbFinder>();

    public FixedKeyProvider Key => this.Provider.GetRequiredService<FixedKeyProvider>();

    public TmdbPurgeRunner Runner => this.Provider.GetRequiredService<TmdbPurgeRunner>();

    public ManualTime Time => (ManualTime)this.Provider.GetRequiredService<TimeProvider>();

    public static async Task<RetentionHarness> CreateAsync(Action<IServiceCollection>? configure = null)
    {
        string directory = Directory.CreateTempSubdirectory("wishlistarr-retention").FullName;
        string database = Path.Combine(directory, "retention.db");
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddOptions<TmdbOptions>();
        services.AddSingleton<TmdbCache>();
        services.AddSingleton<TimeProvider>(new ManualTime(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)));
        services.AddDbContext<WishlistarrDbContext>(options => options.UseWishlistarrSqlite(database));
        services.AddScoped<SettingsStore>();
        services.AddSingleton<FixedKeyProvider>();
        services.AddSingleton<ITmdbApiKeyProvider>(provider => provider.GetRequiredService<FixedKeyProvider>());
        services.AddSingleton<FakeTmdbFinder>();
        services.AddSingleton<ITmdbClient>(provider => provider.GetRequiredService<FakeTmdbFinder>());
        configure?.Invoke(services);
        services.AddTmdbDataRetention();

        ServiceProvider provider = services.BuildServiceProvider();
        using (IServiceScope scope = provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().Database.MigrateAsync();
        }

        return new RetentionHarness(provider, directory);
    }

    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using IServiceScope scope = this.Provider.CreateScope();
        return await action(scope.ServiceProvider);
    }

    public Task MaintainAsync() => this.InScopeAsync(async services =>
    {
        await services.GetRequiredService<TmdbDataService>().MaintainAsync(CancellationToken.None);
        return 0;
    });

    public Task SetPurgePendingAsync(bool pending) => this.InScopeAsync(async services =>
    {
        TmdbDataService data = services.GetRequiredService<TmdbDataService>();
        await (pending ? data.MarkPurgePendingAsync(CancellationToken.None) : data.ClearPurgePendingAsync(CancellationToken.None));
        return 0;
    });

    public Task<bool> IsPurgePendingAsync() =>
        this.InScopeAsync(services => services.GetRequiredService<TmdbDataService>().IsPurgePendingAsync(CancellationToken.None));

    public Task SetRetentionAsync(string days) => this.InScopeAsync(async services =>
    {
        await services.GetRequiredService<SettingsStore>().SetAsync(TmdbRetention.SettingKey, days, false);
        return 0;
    });

    public async Task<int> AddLegacyItemAsync(string ratingKey, DateTimeOffset? fetchedAt, string tmdbId = "278", bool withTitle = true)
    {
        return await this.InScopeAsync(async services =>
        {
            WishlistarrDbContext db = services.GetRequiredService<WishlistarrDbContext>();
            LibraryTitle? title = withTitle ? new LibraryTitle { Kind = MediaKind.Movie, TmdbId = int.Parse(tmdbId, System.Globalization.CultureInfo.InvariantCulture) } : null;
            LibraryItem item = new()
            {
                ServerId = "server-a",
                RatingKey = ratingKey,
                Guid = $"plex://movie/{ratingKey}",
                Kind = MediaKind.Movie,
                LibraryId = "1",
                Title = $"Movie {ratingKey}",
                CanonicalTitle = title,
                ExternalIds =
                [
                    new LibraryExternalId { Source = LibraryIdSource.Imdb, Value = $"tt{ratingKey}", Origin = LibraryIdOrigin.Plex },
                    new LibraryExternalId { Source = LibraryIdSource.Tmdb, Value = tmdbId, Origin = LibraryIdOrigin.TmdbLookup, FetchedAt = fetchedAt },
                ],
            };
            db.LibraryItems.Add(item);
            await db.SaveChangesAsync();
            return item.Id;
        });
    }

    public async Task<int> AddRequestAsync(DateTimeOffset? fetchedAt, RequestState state = RequestState.Pending, int tmdbId = 603)
    {
        return await this.InScopeAsync(async services =>
        {
            WishlistarrDbContext db = services.GetRequiredService<WishlistarrDbContext>();
            KidProfile profile = new() { Name = $"Kid {tmdbId}{state}", Avatar = "fox", PlexLabel = $"kid-{tmdbId}-{state}", CreatedAt = this.Time.GetUtcNow() };
            db.KidProfiles.Add(profile);
            MediaRequest request = new()
            {
                KidProfile = profile,
                Kind = MediaKind.Movie,
                TmdbId = tmdbId,
                Title = "Stored title",
                TitleFetchedAt = fetchedAt,
                State = state,
                RequestCount = 1,
                CreatedAt = this.Time.GetUtcNow(),
                LastRequestedAt = this.Time.GetUtcNow(),
                StateChangedAt = this.Time.GetUtcNow(),
            };
            db.MediaRequests.Add(request);
            await db.SaveChangesAsync();
            return request.Id;
        });
    }

    public Task<List<LibraryExternalId>> GetLookupsAsync() => this.InScopeAsync(services =>
        services.GetRequiredService<WishlistarrDbContext>().LibraryExternalIds
            .AsNoTracking()
            .Where(id => id.Origin == LibraryIdOrigin.TmdbLookup)
            .OrderBy(id => id.Id)
            .ToListAsync());

    public Task<List<MediaRequest>> GetRequestsAsync() => this.InScopeAsync(services =>
        services.GetRequiredService<WishlistarrDbContext>().MediaRequests.AsNoTracking().OrderBy(request => request.Id).ToListAsync());

    public async ValueTask DisposeAsync()
    {
        await this.Provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(this.directory, true);
    }
}

public sealed class TmdbRetentionTests
{
    [Fact]
    public async Task Retention_defaults_to_90_days_and_never_exceeds_180()
    {
        await using RetentionHarness harness = await RetentionHarness.CreateAsync();

        TimeSpan unset = await harness.InScopeAsync(services => services.GetRequiredService<TmdbRetention>().GetAsync());
        await harness.SetRetentionAsync("30");
        TimeSpan configured = await harness.InScopeAsync(services => services.GetRequiredService<TmdbRetention>().GetAsync());
        await harness.SetRetentionAsync("400");
        TimeSpan capped = await harness.InScopeAsync(services => services.GetRequiredService<TmdbRetention>().GetAsync());
        await harness.SetRetentionAsync("nonsense");
        TimeSpan invalid = await harness.InScopeAsync(services => services.GetRequiredService<TmdbRetention>().GetAsync());

        Assert.Equal(TimeSpan.FromDays(90), unset);
        Assert.Equal(TimeSpan.FromDays(30), configured);
        Assert.Equal(TimeSpan.FromDays(180), capped);
        Assert.Equal(TimeSpan.FromDays(90), invalid);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("90", true)]
    [InlineData("180", true)]
    [InlineData("181", false)]
    [InlineData("0", false)]
    [InlineData("-5", false)]
    [InlineData("ninety", false)]
    [InlineData("", false)]
    public void Only_whole_days_from_one_to_180_are_valid(string value, bool valid) =>
        Assert.Equal(valid, TmdbRetention.TryParse(value, out _));

    [Fact]
    public async Task Library_ids_older_than_the_retention_are_deleted_and_newer_ones_kept()
    {
        await using RetentionHarness harness = await RetentionHarness.CreateAsync();
        DateTimeOffset now = harness.Time.GetUtcNow();
        await harness.AddLegacyItemAsync("1", now - TimeSpan.FromDays(91), "278");
        await harness.AddLegacyItemAsync("2", now - TimeSpan.FromDays(89), "279");
        await harness.AddLegacyItemAsync("3", null, "280");
        harness.Tmdb.Failure = new TmdbException("TMDB answered 500.");

        await harness.MaintainAsync();

        List<LibraryExternalId> kept = await harness.GetLookupsAsync();
        Assert.Equal("279", Assert.Single(kept).Value);
        await harness.InScopeAsync(async services =>
        {
            WishlistarrDbContext db = services.GetRequiredService<WishlistarrDbContext>();
            List<LibraryItem> items = await db.LibraryItems.AsNoTracking().OrderBy(item => item.RatingKey).ToListAsync();
            Assert.Equal([false, true, false], items.Select(item => item.TitleId is not null));
            Assert.Equal(1, await db.LibraryTitles.CountAsync());
            Assert.Equal(3, items.Count);
            return 0;
        });
    }

    [Fact]
    public async Task A_shorter_configured_retention_purges_sooner()
    {
        await using RetentionHarness harness = await RetentionHarness.CreateAsync();
        await harness.SetRetentionAsync("30");
        DateTimeOffset now = harness.Time.GetUtcNow();
        await harness.AddLegacyItemAsync("1", now - TimeSpan.FromDays(31));
        await harness.AddRequestAsync(now - TimeSpan.FromDays(31), RequestState.Labeled);
        harness.Tmdb.Failure = new TmdbException("TMDB answered 500.");

        await harness.MaintainAsync();

        Assert.Empty(await harness.GetLookupsAsync());
        Assert.Null(Assert.Single(await harness.GetRequestsAsync()).Title);
    }

    [Fact]
    public async Task Request_titles_older_than_the_retention_are_cleared_but_ids_and_status_stay()
    {
        await using RetentionHarness harness = await RetentionHarness.CreateAsync();
        DateTimeOffset now = harness.Time.GetUtcNow();
        int old = await harness.AddRequestAsync(now - TimeSpan.FromDays(120), RequestState.Labeled, 601);
        int recent = await harness.AddRequestAsync(now - TimeSpan.FromDays(10), RequestState.Labeled, 602);
        int unknown = await harness.AddRequestAsync(null, RequestState.Denied, 603);

        await harness.MaintainAsync();

        List<MediaRequest> requests = await harness.GetRequestsAsync();
        MediaRequest expired = requests.Single(request => request.Id == old);
        Assert.Equal((601, RequestState.Labeled, (string?)null, (DateTimeOffset?)null), (expired.TmdbId, expired.State, expired.Title, expired.TitleFetchedAt));
        Assert.Equal("Stored title", requests.Single(request => request.Id == recent).Title);
        Assert.Null(requests.Single(request => request.Id == unknown).Title);
    }

    [Fact]
    public async Task Records_still_in_use_are_refreshed_before_they_expire()
    {
        await using RetentionHarness harness = await RetentionHarness.CreateAsync();
        DateTimeOffset now = harness.Time.GetUtcNow();
        await harness.AddLegacyItemAsync("1", now - TimeSpan.FromDays(60), "278");
        await harness.AddLegacyItemAsync("2", now - TimeSpan.FromDays(60), "279");
        harness.Tmdb.Known[(TmdbMediaType.Movie, ExternalIdSource.Imdb, "tt1")] = 278;
        harness.Tmdb.Known[(TmdbMediaType.Movie, ExternalIdSource.Imdb, "tt2")] = 999;
        int open = await harness.AddRequestAsync(now - TimeSpan.FromDays(60), RequestState.Pending, 601);
        int closed = await harness.AddRequestAsync(now - TimeSpan.FromDays(60), RequestState.Labeled, 602);
        harness.Tmdb.Titles[(TmdbMediaType.Movie, 601)] = "Fresh title";
        harness.Tmdb.Titles[(TmdbMediaType.Movie, 602)] = "Closed title";

        await harness.MaintainAsync();

        List<LibraryExternalId> lookups = await harness.GetLookupsAsync();
        Assert.Equal([("278", (DateTimeOffset?)now), ("999", (DateTimeOffset?)now)], lookups.Select(id => (id.Value, id.FetchedAt)));
        await harness.InScopeAsync(async services =>
        {
            WishlistarrDbContext db = services.GetRequiredService<WishlistarrDbContext>();
            List<LibraryItem> items = await db.LibraryItems.AsNoTracking().Include(item => item.CanonicalTitle).OrderBy(item => item.RatingKey).ToListAsync();
            Assert.Equal([278, 999], items.Select(item => item.CanonicalTitle!.TmdbId));
            Assert.Equal([278, 999], await db.LibraryTitles.OrderBy(title => title.TmdbId).Select(title => title.TmdbId).ToListAsync());
            return 0;
        });
        List<MediaRequest> requests = await harness.GetRequestsAsync();
        Assert.Equal(("Fresh title", (DateTimeOffset?)now), (requests.Single(request => request.Id == open).Title, requests.Single(request => request.Id == open).TitleFetchedAt));
        Assert.Equal("Stored title", requests.Single(request => request.Id == closed).Title);
    }

    [Fact]
    public async Task Library_data_is_not_refreshed_or_purged_while_a_library_refresh_holds_the_gate()
    {
        await using RetentionHarness harness = await RetentionHarness.CreateAsync();
        await harness.AddLegacyItemAsync("1", harness.Time.GetUtcNow() - TimeSpan.FromDays(100));
        harness.Tmdb.Failure = new TmdbException("TMDB answered 500.");
        IDisposable held = harness.Provider.GetRequiredService<LibraryRefreshGate>().TryEnter() ?? throw new InvalidOperationException();

        Task maintenance = harness.MaintainAsync();
        await Task.Delay(300);
        bool waiting = !maintenance.IsCompleted;
        int whileHeld = (await harness.GetLookupsAsync()).Count;
        held.Dispose();
        await maintenance.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(waiting);
        Assert.Equal(1, whileHeld);
        Assert.Empty(await harness.GetLookupsAsync());
    }

    [Fact]
    public async Task Clearing_waits_for_a_running_library_refresh_instead_of_failing()
    {
        await using RetentionHarness harness = await RetentionHarness.CreateAsync();
        await harness.AddLegacyItemAsync("1", harness.Time.GetUtcNow());
        IDisposable held = harness.Provider.GetRequiredService<LibraryRefreshGate>().TryEnter() ?? throw new InvalidOperationException();

        Task<int> clearing = harness.InScopeAsync(services => services.GetRequiredService<TmdbDataService>().PurgeAllAsync(CancellationToken.None));
        await Task.Delay(300);
        bool waiting = !clearing.IsCompleted;
        held.Dispose();

        Assert.True(waiting);
        Assert.Equal(1, await clearing.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task A_refreshed_id_relinks_onto_a_title_another_item_already_uses()
    {
        await using RetentionHarness harness = await RetentionHarness.CreateAsync();
        DateTimeOffset now = harness.Time.GetUtcNow();
        int moved = await harness.AddLegacyItemAsync("1", now - TimeSpan.FromDays(60), "278");
        int resident = await harness.AddLegacyItemAsync("3", now - TimeSpan.FromDays(1), "999");
        harness.Tmdb.Known[(TmdbMediaType.Movie, ExternalIdSource.Imdb, "tt1")] = 999;

        await harness.MaintainAsync();

        await harness.InScopeAsync(async services =>
        {
            WishlistarrDbContext db = services.GetRequiredService<WishlistarrDbContext>();
            List<LibraryItem> items = await db.LibraryItems.AsNoTracking().OrderBy(item => item.RatingKey).ToListAsync();
            Assert.NotNull(items[0].TitleId);
            Assert.Equal(items[0].TitleId, items[1].TitleId);
            LibraryTitle title = await db.LibraryTitles.AsNoTracking().SingleAsync();
            Assert.Equal(999, title.TmdbId);
            Assert.Equal(moved, items[0].Id);
            Assert.Equal(resident, items[1].Id);
            return 0;
        });
    }

    [Fact]
    public async Task A_pending_purge_flag_makes_the_daily_run_purge_everything_and_clear_the_flag()
    {
        await using RetentionHarness harness = await RetentionHarness.CreateAsync();
        await harness.AddLegacyItemAsync("1", harness.Time.GetUtcNow());
        await harness.AddRequestAsync(harness.Time.GetUtcNow());
        await harness.SetPurgePendingAsync(true);

        await harness.MaintainAsync();

        Assert.Empty(await harness.GetLookupsAsync());
        Assert.Null(Assert.Single(await harness.GetRequestsAsync()).Title);
        Assert.False(await harness.IsPurgePendingAsync());
    }

    [Fact]
    public async Task Without_a_tmdb_key_the_daily_run_purges_everything()
    {
        await using RetentionHarness harness = await RetentionHarness.CreateAsync();
        await harness.AddLegacyItemAsync("1", harness.Time.GetUtcNow());
        await harness.AddRequestAsync(harness.Time.GetUtcNow());
        harness.Key.Key = null;

        await harness.MaintainAsync();

        Assert.Empty(await harness.GetLookupsAsync());
        Assert.Null(Assert.Single(await harness.GetRequestsAsync()).Title);
    }

    [Fact]
    public async Task With_a_key_and_no_pending_purge_data_inside_the_retention_is_kept()
    {
        await using RetentionHarness harness = await RetentionHarness.CreateAsync();
        await harness.AddLegacyItemAsync("1", harness.Time.GetUtcNow());
        harness.Tmdb.Failure = new TmdbException("TMDB answered 500.");

        await harness.MaintainAsync();

        Assert.Single(await harness.GetLookupsAsync());
    }

    [Fact]
    public async Task The_startup_check_resumes_a_pending_purge_and_leaves_clean_data_alone()
    {
        await using RetentionHarness harness = await RetentionHarness.CreateAsync();
        await harness.AddLegacyItemAsync("1", harness.Time.GetUtcNow());
        TmdbPurgeHostedService hosted = new(harness.Runner);

        await hosted.StartAsync(CancellationToken.None);
        await hosted.StopAsync(CancellationToken.None);
        Assert.Single(await harness.GetLookupsAsync());

        await harness.SetPurgePendingAsync(true);
        await hosted.StartAsync(CancellationToken.None);
        await hosted.StopAsync(CancellationToken.None);

        Assert.Empty(await harness.GetLookupsAsync());
        Assert.False(await harness.IsPurgePendingAsync());
    }

    [Fact]
    public async Task A_purge_that_faults_leaves_the_flag_set_and_reports_the_failure()
    {
        await using RetentionHarness harness = await RetentionHarness.CreateAsync(
            services => services.AddScoped<ITmdbDataStore>(_ => new FaultyStore(failRefresh: false, failPurge: true)));

        TmdbPurgeOutcome outcome = await harness.Runner.PurgeAllAsync();

        Assert.True(outcome.Failed);
        Assert.True(await harness.IsPurgePendingAsync());
    }

    [Fact]
    public async Task A_successful_purge_clears_the_flag()
    {
        await using RetentionHarness harness = await RetentionHarness.CreateAsync();
        await harness.AddLegacyItemAsync("1", harness.Time.GetUtcNow());

        TmdbPurgeOutcome outcome = await harness.Runner.PurgeAllAsync();

        Assert.Equal(new TmdbPurgeOutcome(true, 1), outcome);
        Assert.False(await harness.IsPurgePendingAsync());
        Assert.Empty(await harness.GetLookupsAsync());
    }

    [Fact]
    public async Task Shutting_down_while_a_purge_waits_leaves_it_pending_for_the_next_start()
    {
        await using RetentionHarness harness = await RetentionHarness.CreateAsync();
        await harness.AddLegacyItemAsync("1", harness.Time.GetUtcNow());
        harness.Runner.WaitLimit = TimeSpan.FromMilliseconds(100);
        IDisposable held = await harness.Provider.GetRequiredService<LibraryRefreshGate>().EnterAsync(CancellationToken.None);

        TmdbPurgeOutcome outcome = await harness.Runner.PurgeAllAsync();
        await harness.Runner.DrainAsync(TimeSpan.FromMilliseconds(100));
        held.Dispose();

        Assert.False(outcome.Completed);
        Assert.True(await harness.IsPurgePendingAsync());
        Assert.Single(await harness.GetLookupsAsync());
    }

    [Fact]
    public async Task A_tmdb_timeout_does_not_stop_expired_data_from_being_purged()
    {
        await using RetentionHarness harness = await RetentionHarness.CreateAsync();
        DateTimeOffset now = harness.Time.GetUtcNow();
        await harness.AddLegacyItemAsync("1", now - TimeSpan.FromDays(100));
        await harness.AddRequestAsync(now - TimeSpan.FromDays(100), RequestState.Pending);
        harness.Tmdb.Failure = new TaskCanceledException("The request timed out.");
        harness.Tmdb.DetailsFailure = new TaskCanceledException("The request timed out.");

        await harness.MaintainAsync();

        Assert.Empty(await harness.GetLookupsAsync());
        Assert.Null(Assert.Single(await harness.GetRequestsAsync()).Title);
    }

    [Fact]
    public async Task An_unexpected_refresh_failure_in_one_store_does_not_skip_purging_the_others()
    {
        await using RetentionHarness harness = await RetentionHarness.CreateAsync(
            services => services.AddScoped<ITmdbDataStore>(_ => new FaultyStore(failRefresh: true, failPurge: false)));
        DateTimeOffset now = harness.Time.GetUtcNow();
        await harness.AddLegacyItemAsync("1", now - TimeSpan.FromDays(100));
        await harness.AddRequestAsync(now - TimeSpan.FromDays(100), RequestState.Labeled);

        await harness.MaintainAsync();

        Assert.Empty(await harness.GetLookupsAsync());
        Assert.Null(Assert.Single(await harness.GetRequestsAsync()).Title);
    }

    [Fact]
    public async Task A_store_whose_purge_fails_does_not_stop_the_others_but_the_run_still_fails()
    {
        await using RetentionHarness harness = await RetentionHarness.CreateAsync(
            services => services.AddScoped<ITmdbDataStore>(_ => new FaultyStore(failRefresh: false, failPurge: true)));
        DateTimeOffset now = harness.Time.GetUtcNow();
        await harness.AddLegacyItemAsync("1", now - TimeSpan.FromDays(100));
        await harness.AddRequestAsync(now - TimeSpan.FromDays(100), RequestState.Labeled);

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(harness.MaintainAsync);

        Assert.IsType<InvalidOperationException>(Assert.Single(failure.InnerExceptions));
        Assert.Empty(await harness.GetLookupsAsync());
        Assert.Null(Assert.Single(await harness.GetRequestsAsync()).Title);
    }

    [Fact]
    public async Task A_purge_that_began_before_a_later_flag_write_does_not_clear_it_even_if_the_later_purge_fails()
    {
        ToggleStore toggle = new();
        await using RetentionHarness harness = await RetentionHarness.CreateAsync(services => services.AddScoped<ITmdbDataStore>(_ => toggle));
        await harness.AddLegacyItemAsync("1", harness.Time.GetUtcNow());
        harness.Runner.WaitLimit = TimeSpan.FromMilliseconds(50);
        IDisposable held = await harness.Provider.GetRequiredService<LibraryRefreshGate>().EnterAsync(CancellationToken.None);

        TmdbPurgeOutcome first = await harness.Runner.PurgeAllAsync();
        toggle.Fail = true;
        TmdbPurgeOutcome second = await harness.Runner.PurgeAllAsync();
        held.Dispose();
        await WaitUntilIdleAsync(harness);

        Assert.False(first.Completed);
        Assert.False(second.Completed);
        Assert.Empty(await harness.GetLookupsAsync());
        Assert.True(await harness.IsPurgePendingAsync());
    }

    [Fact]
    public async Task Overlapping_purges_that_all_succeed_clear_the_flag()
    {
        await using RetentionHarness harness = await RetentionHarness.CreateAsync();
        await harness.AddLegacyItemAsync("1", harness.Time.GetUtcNow());
        harness.Runner.WaitLimit = TimeSpan.FromMilliseconds(50);
        IDisposable held = await harness.Provider.GetRequiredService<LibraryRefreshGate>().EnterAsync(CancellationToken.None);

        await harness.Runner.PurgeAllAsync();
        await harness.Runner.PurgeAllAsync();
        held.Dispose();
        await WaitUntilIdleAsync(harness);

        Assert.Empty(await harness.GetLookupsAsync());
        Assert.False(await harness.IsPurgePendingAsync());
    }

    [Fact]
    public async Task A_runner_that_is_stopping_or_disposed_leaves_the_purge_pending_without_throwing()
    {
        await using RetentionHarness harness = await RetentionHarness.CreateAsync();
        await harness.AddLegacyItemAsync("1", harness.Time.GetUtcNow());
        harness.Runner.WaitLimit = TimeSpan.FromMilliseconds(50);
        IDisposable held = await harness.Provider.GetRequiredService<LibraryRefreshGate>().EnterAsync(CancellationToken.None);
        await harness.Runner.PurgeAllAsync();
        await harness.Runner.DrainAsync(TimeSpan.FromMilliseconds(50));
        held.Dispose();

        TmdbPurgeOutcome whileStopping = await harness.Runner.PurgeAllAsync();
        harness.Runner.Dispose();
        TmdbPurgeOutcome afterDispose = await harness.Runner.PurgeAllAsync();
        await harness.Runner.DrainAsync(TimeSpan.FromMilliseconds(50));

        Assert.Equal(new TmdbPurgeOutcome(false, null), whileStopping);
        Assert.Equal(new TmdbPurgeOutcome(false, null), afterDispose);
        Assert.True(await harness.IsPurgePendingAsync());
        Assert.Single(await harness.GetLookupsAsync());
    }

    private static async Task WaitUntilIdleAsync(RetentionHarness harness)
    {
        await harness.Runner.DrainAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100);
    }

    private sealed class ToggleStore : ITmdbDataStore
    {
        public bool Fail { get; set; }

        public Type EntityType => typeof(ToggleStore);

        public bool WaitsForLibraryRefresh => false;

        public Task RefreshAsync(DateTimeOffset staleBefore, CancellationToken ct) => Task.CompletedTask;

        public Task<int> PurgeExpiredAsync(DateTimeOffset expiredBefore, CancellationToken ct) => Task.FromResult(0);

        public Task<int> PurgeAllAsync(CancellationToken ct) =>
            this.Fail ? throw new InvalidOperationException("Purge failed.") : Task.FromResult(0);
    }

    private sealed class FaultyStore(bool failRefresh, bool failPurge) : ITmdbDataStore
    {
        public Type EntityType => typeof(FaultyStore);

        public bool WaitsForLibraryRefresh => false;

        public Task RefreshAsync(DateTimeOffset staleBefore, CancellationToken ct) =>
            failRefresh ? throw new InvalidOperationException("Refresh failed.") : Task.CompletedTask;

        public Task<int> PurgeExpiredAsync(DateTimeOffset expiredBefore, CancellationToken ct) =>
            failPurge ? throw new InvalidOperationException("Purge failed.") : Task.FromResult(0);

        public Task<int> PurgeAllAsync(CancellationToken ct) =>
            failPurge ? throw new InvalidOperationException("Purge failed.") : Task.FromResult(0);
    }

    [Fact]
    public async Task Clearing_removes_everything_tmdb_derived_even_inside_the_retention()
    {
        await using RetentionHarness harness = await RetentionHarness.CreateAsync();
        DateTimeOffset now = harness.Time.GetUtcNow();
        await harness.AddLegacyItemAsync("1", now);
        await harness.AddRequestAsync(now);

        int purged = await harness.InScopeAsync(services => services.GetRequiredService<TmdbDataService>().PurgeAllAsync(CancellationToken.None));

        Assert.Equal(2, purged);
        Assert.Empty(await harness.GetLookupsAsync());
        MediaRequest request = Assert.Single(await harness.GetRequestsAsync());
        Assert.Equal((603, (string?)null), (request.TmdbId, request.Title));
    }

    [Fact]
    public async Task A_purged_title_is_fetched_again_on_demand_and_stored_with_a_new_timestamp()
    {
        await using RetentionHarness harness = await RetentionHarness.CreateAsync();
        int id = await harness.AddRequestAsync(harness.Time.GetUtcNow() - TimeSpan.FromDays(200), RequestState.Labeled, 603);
        await harness.MaintainAsync();
        harness.Tmdb.Titles[(TmdbMediaType.Movie, 603)] = "The Matrix";

        string title = await harness.InScopeAsync(async services =>
        {
            WishlistarrDbContext db = services.GetRequiredService<WishlistarrDbContext>();
            return await services.GetRequiredService<RequestTitles>().GetAsync(await db.MediaRequests.SingleAsync(request => request.Id == id), CancellationToken.None);
        });

        Assert.Equal("The Matrix", title);
        MediaRequest stored = Assert.Single(await harness.GetRequestsAsync());
        Assert.Equal(("The Matrix", (DateTimeOffset?)harness.Time.GetUtcNow(), RequestState.Labeled), (stored.Title, stored.TitleFetchedAt, stored.State));
    }

    [Fact]
    public async Task A_fresh_stored_title_is_used_without_asking_tmdb_and_an_outage_yields_an_empty_title()
    {
        await using RetentionHarness harness = await RetentionHarness.CreateAsync();
        int fresh = await harness.AddRequestAsync(harness.Time.GetUtcNow(), RequestState.Labeled, 601);
        int purged = await harness.AddRequestAsync(null, RequestState.Labeled, 602);
        await harness.MaintainAsync();
        harness.Tmdb.DetailsFailure = new HttpRequestException("TMDB is unreachable.");

        (string freshTitle, string purgedTitle) = await harness.InScopeAsync(async services =>
        {
            WishlistarrDbContext db = services.GetRequiredService<WishlistarrDbContext>();
            RequestTitles titles = services.GetRequiredService<RequestTitles>();
            return (
                await titles.GetAsync(await db.MediaRequests.SingleAsync(request => request.Id == fresh), CancellationToken.None),
                await titles.GetAsync(await db.MediaRequests.SingleAsync(request => request.Id == purged), CancellationToken.None));
        });

        Assert.Equal("Stored title", freshTitle);
        Assert.Equal(string.Empty, purgedTitle);
        Assert.Equal(1, harness.Tmdb.DetailCalls);
    }
}
