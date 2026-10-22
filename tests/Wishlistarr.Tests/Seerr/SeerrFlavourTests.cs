using Wishlistarr.Core.Fulfilment;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Integrations.Seerr;

namespace Wishlistarr.Tests.Seerr;

public sealed class SeerrFlavourTests
{
    private const string SettingsPath = "/api/v1/settings/public";

    [Fact]
    public async Task The_public_settings_are_read_once_while_they_are_fresh()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        int id = harness.Server.SeedRequest("movie", FakeSeerrServer.MovieTmdbId);

        await harness.Backend.GetRequestAsync(id);
        await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, FakeSeerrServer.ShowTmdbId, [1]));
        harness.Time.Advance(SeerrBackend.FlavourLifetime - TimeSpan.FromSeconds(1));
        await harness.Backend.GetRequestAsync(id);

        Assert.Single(harness.Server.Sent("GET", SettingsPath));
    }

    [Fact]
    public async Task The_public_settings_are_read_again_once_they_expire()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        int id = harness.Server.SeedRequest("movie", FakeSeerrServer.MovieTmdbId);

        await harness.Backend.GetRequestAsync(id);
        harness.Time.Advance(SeerrBackend.FlavourLifetime);
        await harness.Backend.GetRequestAsync(id);

        Assert.Equal(2, harness.Server.Sent("GET", SettingsPath).Count());
    }

    [Theory]
    [InlineData("save")]
    [InlineData("expire")]
    [InlineData("test")]
    public async Task A_server_swapped_behind_the_same_address_is_noticed(string trigger)
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        int id = harness.Server.SeedRequest("movie", FakeSeerrServer.MovieTmdbId);
        harness.Server.SetMediaStatus("movie", FakeSeerrServer.MovieTmdbId, FakeSeerrServer.MediaBlocklisted);

        SeerrTrackedRequest before = await harness.Backend.GetRequestAsync(id);
        harness.Server.Overseerr = true;
        SeerrTrackedRequest cached = await harness.Backend.GetRequestAsync(id);
        switch (trigger)
        {
            case "save":
                await harness.SaveAsync(FakeSeerrServer.ApiKey, harness.Server.Address);
                break;
            case "expire":
                harness.Time.Advance(SeerrBackend.FlavourLifetime);
                break;
            default:
                await harness.Backend.TestAsync();
                break;
        }

        SeerrTrackedRequest after = await harness.Backend.GetRequestAsync(id);

        Assert.Equal(SeerrAvailability.Blocklisted, before.Availability);
        Assert.Equal(SeerrAvailability.Blocklisted, cached.Availability);
        Assert.Equal(SeerrAvailability.Deleted, after.Availability);
    }

    [Fact]
    public async Task Tracking_uses_seerr_numbering_when_the_public_settings_cannot_be_read()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        harness.Server.BlockPublicSettings = true;
        int id = harness.Server.SeedRequest("movie", FakeSeerrServer.MovieTmdbId);
        harness.Server.SetMediaStatus("movie", FakeSeerrServer.MovieTmdbId, FakeSeerrServer.MediaBlocklisted);

        SeerrTrackedRequest first = await harness.Backend.GetRequestAsync(id);
        harness.Server.BlockPublicSettings = false;
        harness.Server.Overseerr = true;
        harness.Time.Advance(SeerrBackend.FallbackLifetime);
        SeerrTrackedRequest second = await harness.Backend.GetRequestAsync(id);

        Assert.Equal(SeerrAvailability.Blocklisted, first.Availability);
        Assert.Equal(SeerrRequestStatus.Approved, first.Status);
        Assert.Equal(SeerrAvailability.Deleted, second.Availability);
        Assert.Equal(2, harness.Server.Sent("GET", SettingsPath).Count());
    }

    [Fact]
    public async Task A_failed_settings_read_is_not_retried_until_the_fallback_expires()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        harness.Server.BlockPublicSettings = true;
        int id = harness.Server.SeedRequest("movie", FakeSeerrServer.MovieTmdbId);

        await harness.Backend.GetRequestAsync(id);
        await harness.Backend.GetRequestAsync(id);
        harness.Time.Advance(SeerrBackend.FallbackLifetime - TimeSpan.FromSeconds(1));
        await harness.Backend.GetRequestAsync(id);
        int beforeExpiry = harness.Server.Sent("GET", SettingsPath).Count();
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await harness.Backend.GetRequestAsync(id);

        Assert.Equal(1, beforeExpiry);
        Assert.Equal(2, harness.Server.Sent("GET", SettingsPath).Count());
    }

    [Theory]
    [InlineData(true, "1.33.2", false)]
    [InlineData(false, "2.7.3", false)]
    [InlineData(false, "3.4.1", false)]
    public async Task All_seasons_include_an_announced_season_where_the_server_requests_it(bool overseerr, string version, bool drops)
    {
        await using SeerrHarness harness = await AnnouncedShowAsync(overseerr, version, drops);

        SeerrSubmission result = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, FakeSeerrServer.ShowTmdbId, null));

        Assert.Equal(SeerrSubmitOutcome.Created, result.Outcome);
        Assert.Equal("all", Assert.Single(harness.Server.Sent("POST", "/api/v1/request")).Body!["seasons"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("3.5.0")]
    [InlineData("v3.5.0")]
    [InlineData("3.5")]
    [InlineData("3.6.1-beta.2")]
    [InlineData("4")]
    public async Task All_seasons_skip_an_announced_season_from_seerr_3_5(string version)
    {
        await using SeerrHarness harness = await AnnouncedShowAsync(false, version, true);

        SeerrSubmission result = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, FakeSeerrServer.ShowTmdbId, null));

        Assert.Equal(SeerrSubmitOutcome.Attached, result.Outcome);
        Assert.Empty(harness.Server.Sent("POST", "/api/v1/request"));
    }

    [Theory]
    [InlineData("2.2.0", true, true)]
    [InlineData("2.5.2", true, true)]
    [InlineData("2.5.2", false, false)]
    [InlineData("2.6.0", true, false)]
    [InlineData("2.1.0", true, false)]
    [InlineData("3.4.1", true, false)]
    public async Task All_seasons_include_specials_only_where_the_server_puts_them_in_all(string version, bool specials, bool included)
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        harness.Server.Version = version;
        harness.Server.EnableSpecialEpisodes = specials;
        harness.Server.AllIncludesSpecials = version.StartsWith("2.2", StringComparison.Ordinal) || version.StartsWith("2.5", StringComparison.Ordinal);
        harness.Server.SeedRequest("tv", FakeSeerrServer.ShowTmdbId, seasons: [1, 2, 3]);

        SeerrSubmission result = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, FakeSeerrServer.ShowTmdbId, null));

        Assert.Equal(included ? SeerrSubmitOutcome.Created : SeerrSubmitOutcome.Attached, result.Outcome);
        Assert.Equal(included ? 1 : 0, harness.Server.Sent("POST", "/api/v1/request").Count());
    }

    [Fact]
    public async Task A_develop_build_that_has_nothing_left_to_request_is_attached()
    {
        await using SeerrHarness harness = await AnnouncedShowAsync(false, "develop-0a1b2c3", true);

        SeerrSubmission result = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, FakeSeerrServer.ShowTmdbId, null));

        Assert.Equal(SeerrSubmitOutcome.Attached, result.Outcome);
        Assert.Single(harness.Server.Sent("POST", "/api/v1/request"));
    }

    [Theory]
    [InlineData(false, "3.5.0", true, SeerrServerKind.Seerr, true, false, true)]
    [InlineData(false, "2.5.2", true, SeerrServerKind.Seerr, true, true, false)]
    [InlineData(false, "3.4.1", false, SeerrServerKind.Seerr, false, false, false)]
    [InlineData(true, "1.33.2", false, SeerrServerKind.Overseerr, true, false, false)]
    public async Task The_test_button_reports_the_detected_flavour(
        bool overseerr,
        string version,
        bool specials,
        SeerrServerKind kind,
        bool requestable,
        bool allSpecials,
        bool skipsEmpty)
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        harness.Server.Overseerr = overseerr;
        harness.Server.Version = version;
        harness.Server.EnableSpecialEpisodes = specials;

        SeerrTestResult result = await harness.Backend.TestAsync();

        Assert.Equal(version, result.Version);
        Assert.Equal(new SeerrServerFlavour(kind, true, requestable, allSpecials, skipsEmpty), result.Flavour);
        Assert.DoesNotContain(SeerrBackend.SettingsWarning, result.Warnings);
    }

    [Fact]
    public async Task The_test_button_warns_when_the_public_settings_are_blocked()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        harness.Server.BlockPublicSettings = true;

        SeerrTestResult result = await harness.Backend.TestAsync();

        Assert.False(result.Flavour.SettingsRead);
        Assert.Equal(SeerrServerKind.Seerr, result.Flavour.Kind);
        Assert.Equal([SeerrBackend.SettingsWarning], result.Warnings);
    }

    [Fact]
    public async Task Submitting_still_works_when_the_public_settings_cannot_be_read()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        harness.Server.BlockPublicSettings = true;

        SeerrSubmission result = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, FakeSeerrServer.ShowTmdbId, [1]));

        Assert.Equal(SeerrSubmitOutcome.Created, result.Outcome);
    }

    private static async Task<SeerrHarness> AnnouncedShowAsync(bool overseerr, string version, bool drops)
    {
        SeerrHarness harness = await SeerrHarness.CreateAsync();
        harness.Server.Overseerr = overseerr;
        harness.Server.Version = version;
        harness.Server.DropsEmptySeasons = drops;
        harness.Server.AnnouncedSeason = true;
        harness.Server.SeedRequest("tv", FakeSeerrServer.ShowTmdbId, seasons: [1, 2, 3]);
        return harness;
    }
}
