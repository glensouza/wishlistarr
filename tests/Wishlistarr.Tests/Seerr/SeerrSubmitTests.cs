using System.Text.Json.Nodes;
using Wishlistarr.Core.Fulfilment;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Tests.Seerr;

public sealed class SeerrSubmitTests
{
    private const int OtherUserId = 7;

    [Fact]
    public async Task A_show_without_seasons_requests_all_of_them()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();

        SeerrSubmission result = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, FakeSeerrServer.ShowTmdbId, null));
        await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, 1400, []));

        Assert.Equal(SeerrSubmitOutcome.Created, result.Outcome);
        Assert.False(result.Attached);
        Assert.Equal(SeerrRequestStatus.Approved, result.Status);
        Assert.All(harness.Server.Sent("POST", "/api/v1/request"), post => Assert.Equal("all", post.Body!["seasons"]!.GetValue<string>()));
        Assert.Equal(2, harness.Server.Sent("POST", "/api/v1/request").Count());
    }

    [Fact]
    public async Task Given_seasons_are_sent_once_each_in_order()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();

        await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, FakeSeerrServer.ShowTmdbId, [3, 1, 3, 2, 1]));

        JsonNode body = Assert.Single(harness.Server.Sent("POST", "/api/v1/request")).Body!;
        Assert.Equal([1, 2, 3], body["seasons"]!.AsArray().Select(item => item!.GetValue<int>()));
    }

    [Fact]
    public async Task A_movie_is_sent_without_seasons()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();

        await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Movie, FakeSeerrServer.MovieTmdbId, null));

        Assert.Null(Assert.Single(harness.Server.Sent("POST", "/api/v1/request")).Body!["seasons"]);
    }

    [Fact]
    public async Task The_request_is_made_as_the_configured_user_and_reads_are_not()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();

        SeerrSubmission result = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Movie, FakeSeerrServer.MovieTmdbId, null));
        await harness.Backend.GetRequestAsync(result.Id!.Value);

        FakeSeerrServer.RecordedRequest post = Assert.Single(harness.Server.Sent("POST", "/api/v1/request"));
        Assert.Equal("3", post.ApiUser);
        Assert.Null(post.Body!["userId"]);
        Assert.All(harness.Server.Requests.Where(request => request.Method == "GET"), request => Assert.Null(request.ApiUser));
    }

    [Theory]
    [InlineData(FakeSeerrServer.Request, SeerrRequestStatus.Pending)]
    [InlineData(FakeSeerrServer.Request | FakeSeerrServer.AutoApproveMovie, SeerrRequestStatus.Approved)]
    public async Task The_outcome_follows_the_configured_users_permissions(long permissions, SeerrRequestStatus expected)
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        harness.Server.Permissions = permissions;

        SeerrSubmission result = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Movie, FakeSeerrServer.MovieTmdbId, null));

        Assert.Equal(expected, result.Status);
        Assert.Equal(SeerrAvailability.Pending, result.Availability);
        Assert.False(result.Attached);
    }

    [Theory]
    [InlineData(MediaKind.Movie, "movie", FakeSeerrServer.MovieTmdbId)]
    [InlineData(MediaKind.Show, "tv", FakeSeerrServer.ShowTmdbId)]
    public async Task A_declined_request_is_not_attached(MediaKind kind, string mediaType, int tmdbId)
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        int declined = harness.Server.SeedRequest(mediaType, tmdbId, FakeSeerrServer.RequestDeclined, seasons: kind == MediaKind.Show ? [1] : null);

        SeerrSubmission result = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(kind, tmdbId, kind == MediaKind.Show ? [1] : null));

        Assert.False(result.Attached);
        Assert.NotEqual(declined, result.Id);
        Assert.Single(harness.Server.Sent("POST", "/api/v1/request"));
    }

    [Theory]
    [InlineData(MediaKind.Movie, "movie", FakeSeerrServer.MovieTmdbId)]
    [InlineData(MediaKind.Show, "tv", FakeSeerrServer.ShowTmdbId)]
    public async Task A_4k_request_is_not_attached(MediaKind kind, string mediaType, int tmdbId)
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        int uhd = harness.Server.SeedRequest(mediaType, tmdbId, is4k: true, seasons: kind == MediaKind.Show ? [1] : null);

        SeerrSubmission result = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(kind, tmdbId, kind == MediaKind.Show ? [1] : null));

        Assert.False(result.Attached);
        Assert.NotEqual(uhd, result.Id);
        Assert.Single(harness.Server.Sent("POST", "/api/v1/request"));
    }

    [Fact]
    public async Task A_show_request_for_other_seasons_creates_one_for_the_missing_seasons()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        int existing = harness.Server.SeedRequest("tv", FakeSeerrServer.ShowTmdbId, seasons: [1]);

        SeerrSubmission result = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, FakeSeerrServer.ShowTmdbId, [1, 2]));
        SeerrSubmission again = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, FakeSeerrServer.ShowTmdbId, [2]));

        Assert.False(result.Attached);
        Assert.NotEqual(existing, result.Id);
        Assert.True(again.Attached);
        Assert.Equal(result.Id, again.Id);
        Assert.Single(harness.Server.Sent("POST", "/api/v1/request"));
    }

    [Fact]
    public async Task A_show_request_covered_by_an_existing_request_is_attached()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        int existing = harness.Server.SeedRequest("tv", FakeSeerrServer.ShowTmdbId, FakeSeerrServer.RequestPending, seasons: [1, 2]);

        SeerrSubmission result = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, FakeSeerrServer.ShowTmdbId, [2]));

        Assert.Equal(SeerrSubmitOutcome.Attached, result.Outcome);
        Assert.True(result.Attached);
        Assert.Equal(existing, result.Id);
        Assert.Equal(SeerrRequestStatus.Pending, result.Status);
        Assert.Empty(harness.Server.Sent("POST", "/api/v1/request"));
    }

    [Fact]
    public async Task Seasons_already_in_the_library_count_as_covered()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        int existing = harness.Server.SeedRequest("tv", FakeSeerrServer.ShowTmdbId, seasons: [3]);
        harness.Server.SetSeasonStatus(FakeSeerrServer.ShowTmdbId, 1, FakeSeerrServer.MediaAvailable);
        harness.Server.SetSeasonStatus(FakeSeerrServer.ShowTmdbId, 2, FakeSeerrServer.MediaAvailable);
        harness.Server.SetMediaStatus("tv", FakeSeerrServer.ShowTmdbId, FakeSeerrServer.MediaPartiallyAvailable);

        SeerrSubmission all = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, FakeSeerrServer.ShowTmdbId, null));
        SeerrSubmission available = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, FakeSeerrServer.ShowTmdbId, [1]));

        Assert.Equal(existing, all.Id);
        Assert.True(all.Attached);
        Assert.Equal(SeerrAvailability.PartiallyAvailable, all.Availability);
        Assert.Equal(SeerrSubmitOutcome.AlreadyAvailable, available.Outcome);
        Assert.Equal(SeerrAvailability.PartiallyAvailable, available.Availability);
        Assert.Equal(SeerrAvailability.Available, available.SeasonsAvailability);
        Assert.Null(available.Id);
        Assert.False(available.Attached);
        Assert.Empty(harness.Server.Sent("POST", "/api/v1/request"));
    }

    [Fact]
    public async Task A_deleted_season_is_requested_again()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        harness.Server.SetSeasonStatus(FakeSeerrServer.ShowTmdbId, 1, FakeSeerrServer.MediaDeleted);

        SeerrSubmission result = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, FakeSeerrServer.ShowTmdbId, [1]));

        Assert.False(result.Attached);
        Assert.NotNull(result.Id);
    }

    [Fact]
    public async Task An_available_movie_without_a_request_is_not_requested_again()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        harness.Server.SetMediaStatus("movie", FakeSeerrServer.MovieTmdbId, FakeSeerrServer.MediaAvailable);

        SeerrSubmission result = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Movie, FakeSeerrServer.MovieTmdbId, null));

        Assert.Equal(SeerrSubmitOutcome.AlreadyAvailable, result.Outcome);
        Assert.Null(result.Id);
        Assert.Null(result.Status);
        Assert.Equal(SeerrAvailability.Available, result.Availability);
        Assert.Empty(harness.Server.Sent("POST", "/api/v1/request"));
    }

    [Fact]
    public async Task A_completed_request_is_attached_only_while_the_movie_is_available()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        int completed = harness.Server.SeedRequest("movie", FakeSeerrServer.MovieTmdbId, FakeSeerrServer.RequestCompleted);
        harness.Server.SetMediaStatus("movie", FakeSeerrServer.MovieTmdbId, FakeSeerrServer.MediaAvailable);

        SeerrSubmission available = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Movie, FakeSeerrServer.MovieTmdbId, null));
        harness.Server.SetMediaStatus("movie", FakeSeerrServer.MovieTmdbId, FakeSeerrServer.MediaDeleted);
        SeerrSubmission deleted = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Movie, FakeSeerrServer.MovieTmdbId, null));

        Assert.Equal(completed, available.Id);
        Assert.Equal(SeerrRequestStatus.Completed, available.Status);
        Assert.True(available.Attached);
        Assert.NotEqual(completed, deleted.Id);
        Assert.False(deleted.Attached);
    }

    [Fact]
    public async Task The_configured_users_own_request_is_preferred()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        harness.Server.SeedRequest("movie", FakeSeerrServer.MovieTmdbId, requestedBy: OtherUserId);
        int own = harness.Server.SeedRequest("movie", FakeSeerrServer.MovieTmdbId, FakeSeerrServer.RequestPending, requestedBy: FakeSeerrServer.DefaultUserId);

        SeerrSubmission result = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Movie, FakeSeerrServer.MovieTmdbId, null));

        Assert.Equal(own, result.Id);
        Assert.True(result.Attached);
    }

    [Fact]
    public async Task A_conflict_from_a_request_made_meanwhile_attaches_to_it()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        int raced = 0;
        harness.Server.BeforeNextCreate = () => raced = harness.Server.SeedRequest(
            "movie",
            FakeSeerrServer.MovieTmdbId,
            FakeSeerrServer.RequestPending,
            requestedBy: OtherUserId);

        SeerrSubmission result = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Movie, FakeSeerrServer.MovieTmdbId, null));

        Assert.Equal(raced, result.Id);
        Assert.Equal(SeerrSubmitOutcome.Attached, result.Outcome);
        Assert.Equal(SeerrRequestStatus.Pending, result.Status);
        Assert.Single(harness.Server.Sent("POST", "/api/v1/request"));
        Assert.Equal(2, harness.Server.Sent("GET", "/api/v1/movie/" + FakeSeerrServer.MovieTmdbId).Count());
    }

    [Fact]
    public async Task No_seasons_left_from_a_request_made_meanwhile_attaches_to_it()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        int raced = 0;
        harness.Server.BeforeNextCreate = () => raced = harness.Server.SeedRequest(
            "tv",
            FakeSeerrServer.ShowTmdbId,
            seasons: [1, 2, 3],
            requestedBy: OtherUserId);

        SeerrSubmission result = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, FakeSeerrServer.ShowTmdbId, null));

        Assert.Equal(raced, result.Id);
        Assert.True(result.Attached);
        Assert.Single(harness.Server.Sent("POST", "/api/v1/request"));
    }

    [Fact]
    public async Task A_blocklisted_season_in_seerr_counts_as_covered()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        harness.Server.SetSeasonStatus(FakeSeerrServer.ShowTmdbId, 1, FakeSeerrServer.MediaBlocklisted);

        SeerrSubmission result = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, FakeSeerrServer.ShowTmdbId, [1]));

        Assert.Equal(SeerrSubmitOutcome.AlreadyCovered, result.Outcome);
        Assert.Equal(SeerrAvailability.Pending, result.Availability);
        Assert.Equal(SeerrAvailability.Blocklisted, result.SeasonsAvailability);
        Assert.Null(result.Id);
        Assert.Empty(harness.Server.Sent("POST", "/api/v1/request"));
    }

    [Theory]
    [InlineData(FakeSeerrServer.MediaPending, FakeSeerrServer.MediaPending, SeerrAvailability.Pending)]
    [InlineData(FakeSeerrServer.MediaPending, FakeSeerrServer.MediaProcessing, SeerrAvailability.Processing)]
    [InlineData(FakeSeerrServer.MediaAvailable, FakeSeerrServer.MediaProcessing, SeerrAvailability.PartiallyAvailable)]
    public async Task Library_seasons_still_on_their_way_are_covered_but_not_available(int first, int second, SeerrAvailability expected)
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        harness.Server.SetSeasonStatus(FakeSeerrServer.ShowTmdbId, 1, first);
        harness.Server.SetSeasonStatus(FakeSeerrServer.ShowTmdbId, 2, second);
        harness.Server.SetMediaStatus("tv", FakeSeerrServer.ShowTmdbId, FakeSeerrServer.MediaPartiallyAvailable);

        SeerrSubmission result = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, FakeSeerrServer.ShowTmdbId, [1, 2]));

        Assert.Equal(SeerrSubmitOutcome.AlreadyCovered, result.Outcome);
        Assert.Equal(SeerrAvailability.PartiallyAvailable, result.Availability);
        Assert.Equal(expected, result.SeasonsAvailability);
        Assert.Null(result.Id);
        Assert.Empty(harness.Server.Sent("POST", "/api/v1/request"));
    }

    [Fact]
    public async Task A_deleted_season_in_overseerr_is_requested_again()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        harness.Server.Overseerr = true;
        harness.Server.SetSeasonStatus(FakeSeerrServer.ShowTmdbId, 1, FakeSeerrServer.OverseerrMediaDeleted);
        harness.Server.SetSeasonStatus(FakeSeerrServer.ShowTmdbId, 2, FakeSeerrServer.MediaAvailable);

        SeerrSubmission result = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, FakeSeerrServer.ShowTmdbId, [1, 2]));

        Assert.NotNull(result.Id);
        Assert.False(result.Attached);
        Assert.Single(harness.Server.Sent("POST", "/api/v1/request"));
    }

    [Theory]
    [InlineData(false, FakeSeerrServer.MediaBlocklisted, SeerrAvailability.Blocklisted)]
    [InlineData(false, FakeSeerrServer.MediaDeleted, SeerrAvailability.Deleted)]
    [InlineData(true, FakeSeerrServer.OverseerrMediaDeleted, SeerrAvailability.Deleted)]
    [InlineData(true, 7, SeerrAvailability.Unknown)]
    [InlineData(true, FakeSeerrServer.MediaAvailable, SeerrAvailability.Available)]
    public async Task Media_status_numbers_follow_the_servers_flavour(bool overseerr, int status, SeerrAvailability expected)
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        harness.Server.Overseerr = overseerr;
        int id = harness.Server.SeedRequest("movie", FakeSeerrServer.MovieTmdbId);
        harness.Server.SetMediaStatus("movie", FakeSeerrServer.MovieTmdbId, status);

        SeerrTrackedRequest tracked = await harness.Backend.GetRequestAsync(id);

        Assert.Equal(expected, tracked.Availability);
    }

    [Theory]
    [InlineData(new[] { 0 })]
    [InlineData(new[] { 0, 1 })]
    public async Task Specials_are_refused_when_seerr_has_them_turned_off(int[] seasons)
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        harness.Server.SeedRequest("tv", FakeSeerrServer.ShowTmdbId, seasons: [1]);

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, FakeSeerrServer.ShowTmdbId, seasons)));

        Assert.Equal("Seerr has special episodes turned off, so season 0 cannot be requested.", error.Message);
        Assert.Empty(harness.Server.Sent("POST", "/api/v1/request"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Specials_are_requested_when_the_server_allows_them(bool overseerr)
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        harness.Server.Overseerr = overseerr;
        harness.Server.EnableSpecialEpisodes = true;

        SeerrSubmission result = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, FakeSeerrServer.ShowTmdbId, [0]));

        Assert.NotNull(result.Id);
        Assert.Equal([0], Assert.Single(harness.Server.Sent("POST", "/api/v1/request")).Body!["seasons"]!.AsArray().Select(item => item!.GetValue<int>()));
    }

    [Fact]
    public async Task Overseerr_without_the_setting_allows_specials()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        harness.Server.Overseerr = true;

        SeerrSubmission result = await harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, FakeSeerrServer.ShowTmdbId, [0, 1]));

        Assert.NotNull(result.Id);
    }

    [Theory]
    [InlineData(new[] { 9 }, "That show has no season 9.")]
    [InlineData(new[] { 1, 4, 9 }, "That show has no seasons 4, 9.")]
    public async Task Seasons_the_show_does_not_have_are_refused(int[] seasons, string message)
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, FakeSeerrServer.ShowTmdbId, seasons)));

        Assert.Equal(message, error.Message);
        Assert.Empty(harness.Server.Sent("POST", "/api/v1/request"));
    }
}
