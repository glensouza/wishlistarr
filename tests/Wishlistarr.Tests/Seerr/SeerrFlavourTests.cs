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
        SeerrTrackedRequest second = await harness.Backend.GetRequestAsync(id);

        Assert.Equal(SeerrAvailability.Blocklisted, first.Availability);
        Assert.Equal(SeerrRequestStatus.Approved, first.Status);
        Assert.Equal(SeerrAvailability.Deleted, second.Availability);
        Assert.Equal(2, harness.Server.Sent("GET", SettingsPath).Count());
    }

    [Fact]
    public async Task Submitting_still_works_when_the_public_settings_cannot_be_read()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        harness.Server.BlockPublicSettings = true;

        SeerrSubmission result = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, FakeSeerrServer.ShowTmdbId, [1]));

        Assert.Equal(SeerrSubmitOutcome.Created, result.Outcome);
    }
}
