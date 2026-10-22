using Wishlistarr.Core.Fulfilment;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Tests.Seerr;

public sealed class SeerrSeasonsAvailabilityTests
{
    [Fact]
    public async Task Movies_have_no_seasons_availability()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();

        SeerrSubmission created = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Movie, FakeSeerrServer.MovieTmdbId, null));
        SeerrSubmission attached = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Movie, FakeSeerrServer.MovieTmdbId, null));
        SeerrTrackedRequest tracked = await harness.Backend.GetRequestAsync(created.Id!.Value);

        Assert.Null(created.SeasonsAvailability);
        Assert.Null(attached.SeasonsAvailability);
        Assert.Null(tracked.SeasonsAvailability);
    }

    [Theory]
    [InlineData(FakeSeerrServer.Request, SeerrAvailability.Pending)]
    [InlineData(FakeSeerrServer.Request | FakeSeerrServer.AutoApproveTv, SeerrAvailability.Processing)]
    public async Task A_new_show_request_rolls_up_from_its_own_seasons(long permissions, SeerrAvailability expected)
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        harness.Server.Permissions = permissions;

        SeerrSubmission result = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, FakeSeerrServer.ShowTmdbId, [1, 2]));

        Assert.Equal(SeerrSubmitOutcome.Created, result.Outcome);
        Assert.Equal(SeerrAvailability.Pending, result.Availability);
        Assert.Equal(expected, result.SeasonsAvailability);
    }

    [Fact]
    public async Task An_attached_show_request_rolls_up_library_and_request_seasons()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        harness.Server.SeedRequest("tv", FakeSeerrServer.ShowTmdbId, seasons: [1, 2]);
        harness.Server.SetSeasonStatus(FakeSeerrServer.ShowTmdbId, 1, FakeSeerrServer.MediaAvailable);
        harness.Server.SetMediaStatus("tv", FakeSeerrServer.ShowTmdbId, FakeSeerrServer.MediaPartiallyAvailable);

        SeerrSubmission first = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, FakeSeerrServer.ShowTmdbId, [1]));
        SeerrSubmission both = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, FakeSeerrServer.ShowTmdbId, [1, 2]));

        Assert.Equal(SeerrSubmitOutcome.Attached, first.Outcome);
        Assert.Equal(SeerrAvailability.PartiallyAvailable, first.Availability);
        Assert.Equal(SeerrAvailability.Available, first.SeasonsAvailability);
        Assert.Equal(SeerrAvailability.PartiallyAvailable, both.SeasonsAvailability);
    }

    [Fact]
    public async Task Tracking_a_show_reports_the_requests_seasons_as_they_arrive()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        int id = harness.Server.SeedRequest("tv", FakeSeerrServer.ShowTmdbId, seasons: [1, 2]);
        harness.Server.SetMediaStatus("tv", FakeSeerrServer.ShowTmdbId, FakeSeerrServer.MediaPartiallyAvailable);
        harness.Server.SetSeasonStatus(FakeSeerrServer.ShowTmdbId, 3, FakeSeerrServer.MediaAvailable);

        SeerrTrackedRequest approved = await harness.Backend.GetRequestAsync(id);
        harness.Server.SetSeasonStatus(FakeSeerrServer.ShowTmdbId, 1, FakeSeerrServer.MediaAvailable);
        SeerrTrackedRequest partly = await harness.Backend.GetRequestAsync(id);
        harness.Server.SetSeasonStatus(FakeSeerrServer.ShowTmdbId, 2, FakeSeerrServer.MediaAvailable);
        SeerrTrackedRequest arrived = await harness.Backend.GetRequestAsync(id);

        Assert.Equal(SeerrAvailability.PartiallyAvailable, approved.Availability);
        Assert.Equal(SeerrAvailability.Processing, approved.SeasonsAvailability);
        Assert.Equal(SeerrAvailability.PartiallyAvailable, partly.SeasonsAvailability);
        Assert.Equal(SeerrAvailability.Available, arrived.SeasonsAvailability);
        Assert.Equal(SeerrAvailability.PartiallyAvailable, arrived.Availability);
    }

    [Fact]
    public async Task A_completed_show_request_counts_as_available_without_library_seasons()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        int id = harness.Server.SeedRequest("tv", FakeSeerrServer.ShowTmdbId, FakeSeerrServer.RequestCompleted, seasons: [1]);

        SeerrTrackedRequest tracked = await harness.Backend.GetRequestAsync(id);

        Assert.Equal(SeerrAvailability.Available, tracked.SeasonsAvailability);
    }
}
