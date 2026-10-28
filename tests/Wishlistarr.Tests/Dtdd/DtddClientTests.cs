using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Integrations.Dtdd;
using Wishlistarr.Tests;
using Wishlistarr.Tests.Tmdb;

namespace Wishlistarr.Tests.Dtdd;

public sealed class DtddClientTests
{
    private const string Sentinel = "DO-NOT-LOG-THIS";

    [Fact]
    public async Task Missing_key_makes_no_request()
    {
        await using DtddHarness harness = await DtddHarness.CreateAsync();
        harness.Keys.Key = null;

        await Assert.ThrowsAsync<DtddNotConfiguredException>(() => harness.Client.GetWarningsAsync(MediaKind.Movie, 603));
        await Assert.ThrowsAsync<DtddNotConfiguredException>(() => harness.Client.TestApiKeyAsync());

        Assert.Empty(harness.Server.Requests);
    }

    [Fact]
    public async Task Requests_send_the_key_header_and_keep_it_out_of_the_query()
    {
        await using DtddHarness harness = await DtddHarness.CreateAsync();
        harness.Server.SetList(603, """[{"id":15,"itemTypeId":15,"itemTypeName":"Movie","tmdbId":603}]""");
        harness.Server.SetDetail(15, """{"id":15,"topicItemStats":[]}""");

        Assert.Empty(await harness.Client.GetWarningsAsync(MediaKind.Movie, 603));

        Assert.Equal(2, harness.Server.Requests.Count);
        Assert.All(harness.Server.Requests, request =>
        {
            Assert.Equal(FakeDtddServer.ValidApiKey, request.ApiKey);
            Assert.False(request.Query.ContainsKey("api_key"));
            Assert.False(request.Query.ContainsKey("key"));
            Assert.DoesNotContain("/ratings", request.Path, StringComparison.Ordinal);
            Assert.DoesNotContain("/topics", request.Path, StringComparison.Ordinal);
        });
        Assert.Equal("603", harness.Server.Requests[0].Query["tmdb"]);
    }

    [Fact]
    public async Task Topic_names_keep_only_a_yes_majority_and_sort_by_yes_then_name()
    {
        await using DtddHarness harness = await DtddHarness.CreateAsync();
        harness.Server.SetList(603, """
            [
              {"id":16,"itemTypeId":16,"itemTypeName":"TV","tmdbId":603},
              {"id":15,"itemTypeId":15,"itemTypeName":"Movie","tmdbId":603}
            ]
            """);
        harness.Server.SetDetail(16, """{"id":16,"topicItemStats":[]}""");
        harness.Server.SetDetail(15, """
            {"id":15,"topicItemStats":[
              {"topicId":1,"topicName":" a dog dies ","yesSum":4,"noSum":1,"numComments":9,"itemId":15},
              {"topicId":2,"topicName":"none","yesSum":1,"noSum":5,"numComments":2,"itemId":15},
              {"topicId":3,"topicName":" ","yesSum":8,"noSum":0,"numComments":1,"itemId":15},
              {"topicId":4,"topicName":"jump scares","yesSum":9,"noSum":2,"numComments":4,"itemId":15},
              {"topicId":5,"topicName":"A dog dies","yesSum":2,"noSum":0,"numComments":1,"itemId":15},
              {"topicId":6,"topicName":"alcohol","yesSum":0,"noSum":0,"numComments":1,"itemId":15},
              {"topicId":7,"topicName":"blood","yesSum":4,"noSum":4,"numComments":1,"itemId":15}
            ]}
            """);

        IReadOnlyList<string> names = await harness.Client.GetWarningsAsync(MediaKind.Movie, 603);

        Assert.Equal(["jump scares", "a dog dies"], names);
        Assert.Equal(0, harness.Server.Count("/api/v3/items/16"));
        Assert.Empty(await harness.Client.GetWarningsAsync(MediaKind.Show, 603));
        Assert.Equal(1, harness.Server.Count("/api/v3/items/16"));
    }

    [Fact]
    public async Task A_lookup_miss_is_empty_and_a_nested_item_type_is_not_a_match()
    {
        await using DtddHarness harness = await DtddHarness.CreateAsync();

        Assert.Empty(await harness.Client.GetWarningsAsync(MediaKind.Movie, 404));
        Assert.Empty(await harness.Client.GetWarningsAsync(MediaKind.Movie, 404));
        Assert.Equal(1, harness.Server.Count("/api/v3/items"));

        harness.Server.SetList(500, """[{"id":15,"itemType":{"id":15,"name":"Movie"},"tmdbId":500},{"id":16,"itemType":{"id":16,"name":"TV"},"tmdbId":500}]""");
        DtddException error = await Assert.ThrowsAsync<DtddException>(() => harness.Client.GetWarningsAsync(MediaKind.Movie, 500));
        await Assert.ThrowsAsync<DtddException>(() => harness.Client.GetWarningsAsync(MediaKind.Movie, 500));

        Assert.Contains("unrecognized", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, harness.Server.Requests.Count(request => request.Query.TryGetValue("tmdb", out string? id) && id == "500"));
    }

    [Fact]
    public async Task A_missing_detail_is_a_failure_and_is_not_cached()
    {
        await using DtddHarness harness = await DtddHarness.CreateAsync();
        harness.Server.SetList(603, """[{"id":9,"itemTypeId":15,"itemTypeName":"Movie","tmdbId":603}]""");

        DtddException error = await Assert.ThrowsAsync<DtddException>(() => harness.Client.GetWarningsAsync(MediaKind.Movie, 603));
        await Assert.ThrowsAsync<DtddException>(() => harness.Client.GetWarningsAsync(MediaKind.Movie, 603));

        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Contains("not_found", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, error.Message, StringComparison.Ordinal);
        Assert.Equal(2, harness.Server.Count("/api/v3/items/9"));
    }

    [Fact]
    public async Task Unauthorized_and_forbidden_include_the_error_code_only()
    {
        await using DtddHarness harness = await DtddHarness.CreateAsync();
        harness.Keys.Key = "wrong";

        DtddException invalid = await Assert.ThrowsAsync<DtddException>(() => harness.Client.GetWarningsAsync(MediaKind.Movie, 603));
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.Contains("invalid_api_key", invalid.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, invalid.Message, StringComparison.Ordinal);

        harness.Keys.Key = FakeDtddServer.ValidApiKey;
        harness.Server.Enqueue("/api/v3/items", 403, body: """{"error":"upgrade_required","message":"DO-NOT-LOG-THIS"}""");
        DtddException forbidden = await Assert.ThrowsAsync<DtddException>(() => harness.Client.GetWarningsAsync(MediaKind.Movie, 1));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Contains("upgrade_required", forbidden.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, forbidden.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_key_test_accepts_success_or_not_found_and_rejects_401()
    {
        await using DtddHarness harness = await DtddHarness.CreateAsync();

        Assert.True(await harness.Client.TestApiKeyAsync());
        Assert.Equal("0", Assert.Single(harness.Server.Requests).Query["tmdb"]);

        harness.Keys.Key = "wrong";
        Assert.False(await harness.Client.TestApiKeyAsync());
    }

    [Fact]
    public async Task Too_many_requests_is_retried_and_a_zero_delay_completes()
    {
        await using DtddHarness harness = await DtddHarness.CreateAsync();
        harness.Server.Enqueue("/api/v3/items", 429, "0", """{"error":"rate_limit_exceeded","message":"DO-NOT-LOG-THIS"}""");
        harness.Server.SetList(603, """[{"id":15,"itemTypeId":15,"itemTypeName":"Movie","tmdbId":603}]""");
        harness.Server.SetDetail(15, """{"id":15,"topicItemStats":[{"topicId":1,"topicName":"dogs","yesSum":2,"noSum":0,"numComments":1,"itemId":15}]}""");

        Assert.Equal(["dogs"], await harness.Client.GetWarningsAsync(MediaKind.Movie, 603));
        Assert.Empty(harness.Time.Delays);
        Assert.Equal(2, harness.Server.Count("/api/v3/items"));
    }

    [Fact]
    public async Task Retry_after_is_capped_and_the_total_wait_stays_short()
    {
        await using DtddHarness harness = await DtddHarness.CreateAsync();
        for (int index = 0; index < 6; index++)
        {
            harness.Server.Enqueue("/api/v3/items", 429, "3600", """{"error":"monthly_limit_exceeded","message":"DO-NOT-LOG-THIS"}""");
        }

        DtddException error = await Assert.ThrowsAsync<DtddException>(() => harness.Client.GetWarningsAsync(MediaKind.Movie, 603));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Contains("monthly_limit_exceeded", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, error.Message, StringComparison.Ordinal);
        Assert.All(harness.Time.Delays, delay => Assert.InRange(delay, TimeSpan.Zero, TimeSpan.FromSeconds(2)));
        Assert.True(harness.Time.Delays.Aggregate(TimeSpan.Zero, (sum, delay) => sum + delay) <= TimeSpan.FromSeconds(4));
        Assert.InRange(harness.Server.Count("/api/v3/items"), 2, 4);
    }

    [Fact]
    public async Task Cache_is_reused_until_seven_days_and_clears_on_demand()
    {
        ManualTimeProvider time = new();
        await using DtddHarness harness = await DtddHarness.CreateAsync(time);
        harness.Server.SetList(603, """[{"id":15,"itemTypeId":15,"itemTypeName":"Movie","tmdbId":603}]""");
        harness.Server.SetDetail(15, """{"id":15,"topicItemStats":[{"topicId":1,"topicName":"dogs","yesSum":3,"noSum":1,"numComments":1,"itemId":15}]}""");

        Assert.Equal(["dogs"], await harness.Client.GetWarningsAsync(MediaKind.Movie, 603));
        time.Advance(TimeSpan.FromDays(6));
        Assert.Equal(["dogs"], await harness.Client.GetWarningsAsync(MediaKind.Movie, 603));
        Assert.Equal(1, harness.Server.Count("/api/v3/items/15"));

        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(["dogs"], await harness.Client.GetWarningsAsync(MediaKind.Movie, 603));
        Assert.Equal(2, harness.Server.Count("/api/v3/items/15"));

        harness.Cache.Clear();
        Assert.Equal(["dogs"], await harness.Client.GetWarningsAsync(MediaKind.Movie, 603));
        Assert.Equal(3, harness.Server.Count("/api/v3/items/15"));
    }

    [Fact]
    public void Select_drops_missing_sums_and_keeps_the_higher_yes_spelling()
    {
        IReadOnlyList<string> names = DtddClient.Select(
        [
            new DtddTopicStat(1, "Dogs", null, null, 4, 1),
            new DtddTopicStat(2, "dogs", 5, 1, 9, 1),
            new DtddTopicStat(3, "cats", 1, 1, 0, 1),
        ]);

        Assert.Equal(["dogs"], names);
    }

    [Fact]
    public void Flat_search_items_bind_and_a_nested_item_type_does_not()
    {
        DtddListItem? flat = JsonSerializer.Deserialize<DtddListItem>("""{"id":15,"itemTypeId":15,"itemTypeName":"Movie","tmdbId":603}""");
        Assert.NotNull(flat);
        Assert.Equal(15, flat.Id);
        Assert.Equal(15, flat.ItemTypeId);
        Assert.Equal("Movie", flat.ItemTypeName);
        Assert.Equal(603, flat.TmdbId);

        DtddListItem? nested = JsonSerializer.Deserialize<DtddListItem>("""{"id":15,"itemType":{"id":15,"name":"Movie"},"tmdbId":603}""");
        Assert.NotNull(nested);
        Assert.Null(nested.ItemTypeId);
        Assert.Null(nested.ItemTypeName);
    }
}

public sealed class FixedDtddKeyProvider : IDtddApiKeyProvider
{
    public string? Key { get; set; } = FakeDtddServer.ValidApiKey;

    public Task<string?> GetApiKeyAsync(CancellationToken ct = default) => Task.FromResult(this.Key);
}

public sealed class DtddHarness : IAsyncDisposable
{
    private readonly ServiceProvider provider;

    private DtddHarness(FakeDtddServer server, FixedDtddKeyProvider keys, RecordingTimeProvider recorded, TimeProvider time, ServiceProvider provider)
    {
        this.Server = server;
        this.Keys = keys;
        this.Time = recorded;
        this.Clock = time;
        this.provider = provider;
    }

    public FakeDtddServer Server { get; }

    public FixedDtddKeyProvider Keys { get; }

    public RecordingTimeProvider Time { get; }

    public TimeProvider Clock { get; }

    public IDtddClient Client => this.provider.GetRequiredService<IDtddClient>();

    public DtddCache Cache => this.provider.GetRequiredService<DtddCache>();

    public static async Task<DtddHarness> CreateAsync(TimeProvider? time = null, Action<DtddOptions>? configure = null)
    {
        FakeDtddServer server = await FakeDtddServer.StartAsync();
        FixedDtddKeyProvider keys = new();
        RecordingTimeProvider recorded = new();
        TimeProvider clock = time ?? recorded;
        ServiceCollection services = new();
        services.AddDoesTheDogDie<FixedDtddKeyProvider>();
        services.AddSingleton<IDtddApiKeyProvider>(keys);
        services.AddSingleton(clock);
        services.Configure<DtddOptions>(options =>
        {
            options.ApiBaseAddress = server.ApiBaseAddress;
            configure?.Invoke(options);
        });
        return new DtddHarness(server, keys, recorded, clock, services.BuildServiceProvider());
    }

    public async ValueTask DisposeAsync()
    {
        await this.provider.DisposeAsync();
        await this.Server.DisposeAsync();
    }
}
