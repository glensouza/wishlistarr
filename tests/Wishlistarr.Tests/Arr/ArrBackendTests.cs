using System.Text.Json.Nodes;
using Wishlistarr.Core.Fulfilment;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Tests.Arr;

public sealed class ArrBackendTests
{
    [Theory]
    [InlineData(ArrService.Sonarr)]
    [InlineData(ArrService.Radarr)]
    public async Task Test_succeeds_with_the_right_address_and_key(ArrService service)
    {
        await using ArrHarness harness = await ArrHarness.CreateAsync(service);

        ArrTestResult result = await harness.Backend.TestAsync();

        Assert.True(result.Valid);
        Assert.Equal("4.0.9.2244", result.Version);
        Assert.Equal(FakeArrServer.ApiKey, harness.Server.Requests[0].ApiKey);
    }

    [Fact]
    public async Task Test_explains_when_nothing_is_configured()
    {
        await using ArrHarness harness = await ArrHarness.CreateAsync(ArrService.Sonarr, configured: false);

        ArrTestResult result = await harness.Backend.TestAsync();

        Assert.False(result.Valid);
        Assert.Contains("not configured", result.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Server.Requests);
    }

    [Fact]
    public async Task Test_explains_a_rejected_key_without_repeating_it()
    {
        await using ArrHarness harness = await ArrHarness.CreateAsync(ArrService.Radarr, configured: false);
        await harness.SaveAsync("wrong-key-value", harness.Server.Address);

        ArrTestResult result = await harness.Backend.TestAsync();

        Assert.False(result.Valid);
        Assert.Equal("Radarr rejected the API key.", result.Message);
    }

    [Fact]
    public async Task Test_explains_when_the_address_runs_a_different_app()
    {
        await using ArrHarness harness = await ArrHarness.CreateAsync(ArrService.Sonarr);
        harness.Server.AppName = "Radarr";

        ArrTestResult result = await harness.Backend.TestAsync();

        Assert.False(result.Valid);
        Assert.Equal("That address is running Radarr, not Sonarr.", result.Message);
    }

    [Fact]
    public async Task Test_explains_a_wrong_url_base()
    {
        await using ArrHarness harness = await ArrHarness.CreateAsync(ArrService.Sonarr);
        harness.Server.FailWith = 404;

        ArrTestResult result = await harness.Backend.TestAsync();

        Assert.False(result.Valid);
        Assert.Contains("404", result.Message, StringComparison.Ordinal);
        Assert.Contains("URL base", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_explains_an_unreachable_server()
    {
        await using ArrHarness harness = await ArrHarness.CreateAsync(ArrService.Sonarr, configured: false);
        await harness.SaveAsync(FakeArrServer.ApiKey, new Uri("http://127.0.0.1:1/"));

        ArrTestResult result = await harness.Backend.TestAsync();

        Assert.False(result.Valid);
        Assert.Equal("Sonarr could not be reached. Check the address and that Sonarr is running.", result.Message);
    }

    [Fact]
    public async Task Profiles_root_folders_and_tags_are_listed_from_sonarr()
    {
        await using ArrHarness harness = await ArrHarness.CreateAsync(ArrService.Sonarr);

        IReadOnlyList<ArrQualityProfile> profiles = await harness.Backend.GetQualityProfilesAsync();
        IReadOnlyList<ArrRootFolder> folders = await harness.Backend.GetRootFoldersAsync();
        IReadOnlyList<ArrTag> tags = await harness.Backend.GetTagsAsync();

        Assert.Equal(["HD-1080p", "Any"], profiles.Select(profile => profile.Name));
        Assert.Equal(4, profiles[0].Id);
        Assert.Equal("/media/kids", Assert.Single(folders).Path);
        Assert.Equal(123456789012L, folders[0].FreeSpace);
        Assert.Equal("kids", Assert.Single(tags).Label);
    }

    [Fact]
    public async Task Lists_are_unavailable_until_configured()
    {
        await using ArrHarness harness = await ArrHarness.CreateAsync(ArrService.Radarr, configured: false);

        await Assert.ThrowsAsync<ArrNotConfiguredException>(() => harness.Backend.GetQualityProfilesAsync());
    }

    [Fact]
    public async Task Sonarr_adds_a_new_show_with_the_chosen_profile_tag_and_only_the_requested_seasons()
    {
        await using ArrHarness harness = await ArrHarness.CreateAsync(ArrService.Sonarr);

        FulfilmentResult result = await harness.Backend.FulfilAsync(new FulfilmentRequest(MediaKind.Show, FakeArrServer.ShowTmdbId, [2]));

        Assert.Equal(FulfilmentOutcome.Added, result.Outcome);
        JsonObject added = Assert.Single(harness.Server.Library);
        Assert.Equal(result.ArrId, added["id"]!.GetValue<int>());
        Assert.Equal(4, added["qualityProfileId"]!.GetValue<int>());
        Assert.Equal("/media/kids", added["rootFolderPath"]!.GetValue<string>());
        Assert.True(added["addOptions"]!["searchForMissingEpisodes"]!.GetValue<bool>());
        Assert.Equal(
            [false, false, true, false],
            added["seasons"]!.AsArray().Select(season => season!["monitored"]!.GetValue<bool>()));
        int tagId = harness.Server.Tags.Single(tag => tag["label"]!.GetValue<string>() == "wishlistarr")["id"]!.GetValue<int>();
        Assert.Equal([tagId], added["tags"]!.AsArray().Select(tag => tag!.GetValue<int>()));
    }

    [Fact]
    public async Task Sonarr_reuses_an_existing_show_and_monitors_the_requested_season()
    {
        await using ArrHarness harness = await ArrHarness.CreateAsync(ArrService.Sonarr);
        JsonObject seeded = harness.Server.SeedSeries(monitored: false, 1);

        FulfilmentResult result = await harness.Backend.FulfilAsync(new FulfilmentRequest(MediaKind.Show, FakeArrServer.ShowTmdbId, [3]));

        Assert.Equal(FulfilmentOutcome.Reused, result.Outcome);
        Assert.Equal(seeded["id"]!.GetValue<int>(), result.ArrId);
        Assert.Empty(harness.Server.Sent("POST", "/api/v3/series"));
        JsonObject stored = Assert.Single(harness.Server.Library);
        Assert.True(stored["monitored"]!.GetValue<bool>());
        Assert.Equal(
            [false, true, false, true],
            stored["seasons"]!.AsArray().Select(season => season!["monitored"]!.GetValue<bool>()));
        FakeArrServer.RecordedRequest search = Assert.Single(harness.Server.Sent("POST", "/api/v3/command"));
        Assert.Equal("SeasonSearch", search.Body!["name"]!.GetValue<string>());
        Assert.Equal(3, search.Body["seasonNumber"]!.GetValue<int>());
    }

    [Fact]
    public async Task Sonarr_never_adds_the_same_show_twice()
    {
        await using ArrHarness harness = await ArrHarness.CreateAsync(ArrService.Sonarr);
        FulfilmentRequest request = new(MediaKind.Show, FakeArrServer.ShowTmdbId, [1]);

        FulfilmentResult first = await harness.Backend.FulfilAsync(request);
        FulfilmentResult second = await harness.Backend.FulfilAsync(request);

        Assert.Equal(FulfilmentOutcome.Added, first.Outcome);
        Assert.Equal(FulfilmentOutcome.Reused, second.Outcome);
        Assert.Single(harness.Server.Library);
        Assert.Single(harness.Server.Sent("POST", "/api/v3/series"));
    }

    [Fact]
    public async Task Sonarr_rejects_a_request_without_seasons_or_for_the_wrong_kind()
    {
        await using ArrHarness harness = await ArrHarness.CreateAsync(ArrService.Sonarr);

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Backend.FulfilAsync(new FulfilmentRequest(MediaKind.Show, FakeArrServer.ShowTmdbId, [])));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Backend.FulfilAsync(new FulfilmentRequest(MediaKind.Movie, FakeArrServer.ShowTmdbId, [1])));
        Assert.Empty(harness.Server.Sent("POST", "/api/v3/series"));
    }

    [Fact]
    public async Task Sonarr_reports_a_show_it_cannot_find()
    {
        await using ArrHarness harness = await ArrHarness.CreateAsync(ArrService.Sonarr);

        ArrException error = await Assert.ThrowsAsync<ArrException>(() => harness.Backend.FulfilAsync(new FulfilmentRequest(MediaKind.Show, 424242, [1])));

        Assert.Equal("Sonarr could not find that show.", error.Message);
    }

    [Fact]
    public async Task Radarr_adds_a_new_movie_with_the_chosen_profile_tag_and_search_on_add()
    {
        await using ArrHarness harness = await ArrHarness.CreateAsync(ArrService.Radarr, configured: false);
        harness.Server.SeedTag(8, "extra");
        await harness.SaveAsync(FakeArrServer.ApiKey, harness.Server.Address, 6, "/media/kids", 8);

        FulfilmentResult result = await harness.Backend.FulfilAsync(new FulfilmentRequest(MediaKind.Movie, FakeArrServer.MovieTmdbId, []));

        Assert.Equal(FulfilmentOutcome.Added, result.Outcome);
        JsonObject added = Assert.Single(harness.Server.Library);
        Assert.Equal(6, added["qualityProfileId"]!.GetValue<int>());
        Assert.Equal("/media/kids", added["rootFolderPath"]!.GetValue<string>());
        Assert.True(added["monitored"]!.GetValue<bool>());
        Assert.True(added["addOptions"]!["searchForMovie"]!.GetValue<bool>());
        int wishlistarr = harness.Server.Tags.Single(tag => tag["label"]!.GetValue<string>() == "wishlistarr")["id"]!.GetValue<int>();
        Assert.Equal(new[] { 8, wishlistarr }.Order(), added["tags"]!.AsArray().Select(tag => tag!.GetValue<int>()).Order());
    }

    [Fact]
    public async Task Radarr_reuses_an_unmonitored_movie_by_monitoring_it_and_searching()
    {
        await using ArrHarness harness = await ArrHarness.CreateAsync(ArrService.Radarr);
        JsonObject seeded = harness.Server.SeedMovie(monitored: false, hasFile: false);

        FulfilmentResult result = await harness.Backend.FulfilAsync(new FulfilmentRequest(MediaKind.Movie, FakeArrServer.MovieTmdbId, []));

        Assert.Equal(FulfilmentOutcome.Reused, result.Outcome);
        Assert.Equal(seeded["id"]!.GetValue<int>(), result.ArrId);
        Assert.Empty(harness.Server.Sent("POST", "/api/v3/movie"));
        Assert.True(Assert.Single(harness.Server.Library)["monitored"]!.GetValue<bool>());
        FakeArrServer.RecordedRequest search = Assert.Single(harness.Server.Sent("POST", "/api/v3/command"));
        Assert.Equal("MoviesSearch", search.Body!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Radarr_does_not_search_again_for_a_movie_that_already_has_a_file()
    {
        await using ArrHarness harness = await ArrHarness.CreateAsync(ArrService.Radarr);
        harness.Server.SeedMovie(monitored: true, hasFile: true);

        FulfilmentResult result = await harness.Backend.FulfilAsync(new FulfilmentRequest(MediaKind.Movie, FakeArrServer.MovieTmdbId, []));

        Assert.Equal(FulfilmentOutcome.Reused, result.Outcome);
        Assert.Empty(harness.Server.Sent("POST", "/api/v3/command"));
        Assert.Single(harness.Server.Library);
    }

    [Fact]
    public async Task The_wishlistarr_tag_is_reused_when_it_already_exists_in_any_case()
    {
        await using ArrHarness harness = await ArrHarness.CreateAsync(ArrService.Radarr);
        harness.Server.SeedTag(12, "Wishlistarr");

        await harness.Backend.FulfilAsync(new FulfilmentRequest(MediaKind.Movie, FakeArrServer.MovieTmdbId, []));

        Assert.Empty(harness.Server.Sent("POST", "/api/v3/tag"));
        Assert.Equal([12], Assert.Single(harness.Server.Library)["tags"]!.AsArray().Select(tag => tag!.GetValue<int>()));
    }

    [Fact]
    public async Task Fulfilment_needs_a_profile_and_root_folder()
    {
        await using ArrHarness harness = await ArrHarness.CreateAsync(ArrService.Radarr, configured: false);
        await harness.SaveAsync(FakeArrServer.ApiKey, harness.Server.Address, null, null);

        await Assert.ThrowsAsync<ArrNotConfiguredException>(() => harness.Backend.FulfilAsync(new FulfilmentRequest(MediaKind.Movie, FakeArrServer.MovieTmdbId, [])));
        Assert.Empty(harness.Server.Requests);
    }

    [Fact]
    public async Task The_api_key_is_stored_as_a_secret()
    {
        await using ArrHarness harness = await ArrHarness.CreateAsync(ArrService.Sonarr);

        Assert.True(harness.Settings.Values["sonarr.api_key"].IsSecret);
        Assert.False(harness.Settings.Values["sonarr.url"].IsSecret);
    }
}
