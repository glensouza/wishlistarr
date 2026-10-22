using Wishlistarr.Core.Fulfilment;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Tests.Seerr;

public sealed class SeerrBackendTests
{
    [Theory]
    [InlineData(SeerrPermissions.Admin, false, false)]
    [InlineData(SeerrPermissions.ManageRequests, false, false)]
    [InlineData(SeerrPermissions.AutoApprove, false, false)]
    [InlineData(SeerrPermissions.AutoApproveMovie, false, true)]
    [InlineData(SeerrPermissions.AutoApproveTv, true, false)]
    [InlineData(SeerrPermissions.AutoApproveMovie | SeerrPermissions.AutoApproveTv, false, false)]
    [InlineData(32768, true, true)]
    [InlineData(0, true, true)]
    public void Auto_approve_is_decided_per_kind(long permissions, bool movieWaits, bool showWaits)
    {
        Assert.Equal(!movieWaits, SeerrPermissions.AutoApproves(permissions, MediaKind.Movie));
        Assert.Equal(!showWaits, SeerrPermissions.AutoApproves(permissions, MediaKind.Show));
    }

    [Theory]
    [InlineData(SeerrPermissions.Admin, true, true)]
    [InlineData(SeerrPermissions.Request, true, true)]
    [InlineData(SeerrPermissions.RequestMovie, true, false)]
    [InlineData(SeerrPermissions.RequestTv, false, true)]
    [InlineData(SeerrPermissions.AutoApprove, false, false)]
    [InlineData(0, false, false)]
    public void Request_permission_is_decided_per_kind(long permissions, bool movie, bool show)
    {
        Assert.Equal(movie, SeerrPermissions.CanRequest(permissions, MediaKind.Movie));
        Assert.Equal(show, SeerrPermissions.CanRequest(permissions, MediaKind.Show));
    }

    [Fact]
    public async Task Test_reads_the_user_without_impersonating_it()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();

        await harness.Backend.TestAsync();

        Assert.All(harness.Server.Requests, request => Assert.Null(request.ApiUser));
        Assert.Single(harness.Server.Sent("GET", "/api/v1/user/" + FakeSeerrServer.DefaultUserId));
    }

    [Fact]
    public void A_connection_does_not_print_its_api_key()
    {
        SeerrConnection connection = new(new Uri("http://seerr.local:5055/"), "secret-seerr-key", 3);

        Assert.DoesNotContain("secret-seerr-key", connection.ToString(), StringComparison.Ordinal);
        Assert.Contains("seerr.local", connection.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_reports_commit_tag_when_the_version_is_absent()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        harness.Server.Version = null;
        harness.Server.CommitTag = "1.34.0-dev";

        SeerrTestResult result = await harness.Backend.TestAsync();

        Assert.Equal("1.34.0-dev", result.Version);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Test_prefers_version_over_commit_tag()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        harness.Server.Version = "2.0.0";
        harness.Server.CommitTag = "abcdef";

        SeerrTestResult result = await harness.Backend.TestAsync();

        Assert.Equal("2.0.0", result.Version);
    }

    [Fact]
    public async Task Test_fails_when_nothing_is_configured()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync(configured: false);

        SeerrNotConfiguredException error = await Assert.ThrowsAsync<SeerrNotConfiguredException>(() => harness.Backend.TestAsync());

        Assert.Contains("not configured", error.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Server.Requests);
    }

    [Fact]
    public async Task Test_fails_when_the_configured_user_does_not_exist()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        harness.Server.MissingUser = true;

        SeerrException error = await Assert.ThrowsAsync<SeerrException>(() => harness.Backend.TestAsync());

        Assert.Equal("Seerr has no user with that id.", error.Message);
        Assert.IsNotType<SeerrNotConfiguredException>(error);
    }

    [Fact]
    public async Task A_rejected_key_is_not_repeated()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync(configured: false);
        await harness.SaveAsync("wrong-key-value", harness.Server.Address);

        SeerrException error = await Assert.ThrowsAsync<SeerrException>(() => harness.Backend.TestAsync());

        Assert.Equal("Seerr rejected the API key.", error.Message);
        Assert.DoesNotContain("wrong-key-value", error.Message, StringComparison.Ordinal);
        Assert.Single(harness.Server.Sent("GET", "/api/v1/status"));
        Assert.Single(harness.Server.Sent("GET", "/api/v1/user/" + FakeSeerrServer.DefaultUserId));
    }

    [Fact]
    public async Task A_redirect_is_not_followed_to_another_host()
    {
        await using RecordingSink sink = await RecordingSink.StartAsync();
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        harness.Server.RedirectTo = new Uri(sink.Address, "collect");

        SeerrException error = await Assert.ThrowsAsync<SeerrException>(() => harness.Backend.TestAsync());

        Assert.Equal("Seerr redirected the request. Check the address.", error.Message);
        Assert.DoesNotContain(FakeSeerrServer.ApiKey, error.Message, StringComparison.Ordinal);
        Assert.Equal(0, sink.Hits);
        Assert.Equal(FakeSeerrServer.ApiKey, Assert.Single(harness.Server.Requests).ApiKey);
    }

    [Fact]
    public async Task Upstream_text_that_echoes_the_key_is_redacted()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();
        harness.Server.FailWith = 400;
        harness.Server.FailureBody = "{\"message\":\"" + FakeSeerrServer.ApiKey + "\"}";

        SeerrException error = await Assert.ThrowsAsync<SeerrException>(() => harness.Backend.TestAsync());

        Assert.Equal("Seerr refused the request: ****", error.Message);
        Assert.DoesNotContain(FakeSeerrServer.ApiKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_submit_arguments_do_not_call_seerr()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Movie, 0, null)));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Movie, 5, [1])));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Backend.SubmitAsync(new SeerrSubmitRequest(MediaKind.Show, 5, [-1])));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Backend.SubmitAsync(new SeerrSubmitRequest((MediaKind)9, 5, null)));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Backend.GetRequestAsync(0));
        Assert.Empty(harness.Server.Requests);
    }

    [Fact]
    public async Task The_api_key_is_stored_as_a_secret()
    {
        await using SeerrHarness harness = await SeerrHarness.CreateAsync();

        Assert.True(harness.Settings.Values["seerr.api_key"].IsSecret);
        Assert.False(harness.Settings.Values["seerr.url"].IsSecret);
        Assert.False(harness.Settings.Values["seerr.user_id"].IsSecret);
        Assert.Equal(FakeSeerrServer.DefaultUserId.ToString(System.Globalization.CultureInfo.InvariantCulture), harness.Settings.Values["seerr.user_id"].Value);
    }
}
