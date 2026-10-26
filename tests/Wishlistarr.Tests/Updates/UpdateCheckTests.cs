using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Events;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Settings;
using Wishlistarr.Api.System;
using Wishlistarr.Api.Updates;

namespace Wishlistarr.Tests.Updates;

public sealed class UpdateCheckTests(UpdateApiFactory factory) : IClassFixture<UpdateApiFactory>
{
    private const string BodySecret = "notice-body-secret-9f3a";
    private const string StatusSecret = "notice-status-secret-9f3a";

    [Fact]
    public async Task Newer_release_is_the_highest_semantic_version()
    {
        await factory.ResetAsync();
        SemanticVersion current = Running();
        string older = OlderTag(current);
        string same = "v" + current;
        string low = Tag(current, 1);
        string chosen = Tag(current, 3);
        string last = Tag(current, 2);
        string prerelease = Tag(current, 4) + "-rc.1";
        string flagged = Tag(current, 5);
        string draft = $"v{current.Major + 1}.0.0";
        factory.GitHub.Json(
            "["
            + Release(prerelease, false, false, "https://evil.example/pre")
            + ","
            + Release(low, false, false, "https://evil.example/low")
            + ","
            + Release(draft, true, false, "https://evil.example/draft")
            + ","
            + Release(same, false, false, "https://evil.example/same")
            + ","
            + Release(older, false, false, "https://evil.example/old")
            + ","
            + """{"tag_name":"v9.0.0","prerelease":false,"html_url":"https://evil.example/broken","author":{"login":"octocat"}}"""
            + ","
            + Release(flagged, false, true, "https://evil.example/flag")
            + ","
            + Release(chosen, false, false, "https://evil.example/chosen")
            + ","
            + Release(last, false, false, "https://evil.example/last")
            + "]");

        await factory.RunAsync();

        JsonElement notice = await this.ParentNoticeAsync();
        Assert.True(notice.GetProperty("checkEnabled").GetBoolean());
        JsonElement available = notice.GetProperty("available");
        Assert.Equal(chosen[1..], available.GetProperty("version").GetString());
        string notes = available.GetProperty("releaseNotesUrl").GetString()!;
        Assert.Equal("https://github.com/glensouza/wishlistarr/releases/tag/" + Uri.EscapeDataString(chosen), notes);
        Assert.Equal(
            "https://github.com/glensouza/wishlistarr/blob/" + Uri.EscapeDataString(chosen) + "/docs/upgrading.md",
            available.GetProperty("upgradeNotesUrl").GetString());
        string raw = notice.GetRawText();
        Assert.DoesNotContain("evil.example", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("octocat", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("octocat", factory.Logs.Text(), StringComparison.Ordinal);
        Assert.Equal(UpdateChecker.ReleasesUrl, factory.GitHub.Requests[0].Uri);
    }

    [Fact]
    public async Task No_newer_release_clears_a_stored_notice_on_the_next_day()
    {
        await factory.ResetAsync();
        SemanticVersion current = Running();
        string chosen = Tag(current, 1);
        factory.GitHub.Json("[" + Release(chosen, false, false, "https://evil.example/chosen") + "]");
        await factory.RunAsync();
        Assert.Equal(chosen[1..], (await this.ParentNoticeAsync()).GetProperty("available").GetProperty("version").GetString());

        factory.Clock.Advance(TimeSpan.FromDays(1));
        factory.GitHub.Json("[" + Release(OlderTag(current), false, false, "https://evil.example/old") + "," + Release("v" + current, false, false, "https://evil.example/same") + "]");
        await factory.RunAsync();

        JsonElement notice = await this.ParentNoticeAsync();
        Assert.Equal(JsonValueKind.Null, notice.GetProperty("available").ValueKind);
        Assert.Null(await factory.SettingAsync(UpdateChecker.TagKey));
        Assert.Null(await factory.SettingAsync(UpdateChecker.ReleaseNotesUrlKey));
    }

    [Fact]
    public async Task A_failed_check_keeps_the_previous_notice_and_consumes_the_day()
    {
        await factory.ResetAsync();
        string chosen = Tag(Running(), 1);
        factory.GitHub.Json("[" + Release(chosen, false, false, "https://github.com/glensouza/wishlistarr/releases/tag/" + chosen) + "]");
        await factory.RunAsync();
        factory.Clock.Advance(TimeSpan.FromDays(1));
        factory.Logs.Clear();
        factory.GitHub.Status(HttpStatusCode.InternalServerError, StatusSecret);
        await factory.RunAsync();
        await factory.RunAsync();

        Assert.Equal(chosen[1..], (await this.ParentNoticeAsync()).GetProperty("available").GetProperty("version").GetString());
        Assert.Equal(2, factory.GitHub.Requests.Count);
        string logs = factory.Logs.Text();
        Assert.Contains("Update check failed with HTTP status 500.", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(StatusSecret, logs, StringComparison.Ordinal);

        factory.Clock.Advance(TimeSpan.FromDays(1));
        factory.Logs.Clear();
        factory.GitHub.Status(HttpStatusCode.OK, "{\"secret\":\"" + BodySecret + "\",\"tag_name\":\"v9.0.0\"}");
        int before = factory.GitHub.Requests.Count;
        await factory.RunAsync();

        Assert.Equal(before + 1, factory.GitHub.Requests.Count);
        Assert.Equal(chosen[1..], (await this.ParentNoticeAsync()).GetProperty("available").GetProperty("version").GetString());
        logs = factory.Logs.Text();
        Assert.Contains("Update check failed because the response could not be read.", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(BodySecret, logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_retired_api_version_logs_a_distinct_warning()
    {
        await factory.ResetAsync();
        string chosen = Tag(Running(), 1);
        factory.GitHub.Json("[" + Release(chosen, false, false, "https://evil.example/chosen") + "]");
        await factory.RunAsync();
        factory.Clock.Advance(TimeSpan.FromDays(1));
        factory.Logs.Clear();
        factory.GitHub.Status(HttpStatusCode.Gone, StatusSecret);
        await factory.RunAsync();

        string logs = factory.Logs.Text();
        Assert.Contains("no longer supports API version 2022-11-28", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("Update check failed with HTTP status 410.", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(StatusSecret, logs, StringComparison.Ordinal);
        Assert.Equal(chosen[1..], (await this.ParentNoticeAsync()).GetProperty("available").GetProperty("version").GetString());
    }

    [Fact]
    public void The_upgrade_notes_follow_a_valid_tag_and_fall_back_to_main()
    {
        Assert.Equal(
            "https://github.com/glensouza/wishlistarr/blob/v1.2.3/docs/upgrading.md",
            UpdateChecker.UpgradeNotesUrlFor("v1.2.3"));
        Assert.Equal(
            "https://github.com/glensouza/wishlistarr/blob/v1.2.3-rc.1/docs/upgrading.md",
            UpdateChecker.UpgradeNotesUrlFor("v1.2.3-rc.1"));
        foreach (string? bad in new[] { null, "", "main", "1.2.3", "v1.2/../x", "v1.2.3/../../x" })
        {
            Assert.Equal(UpdateChecker.UpgradeNotesUrl, UpdateChecker.UpgradeNotesUrlFor(bad));
        }
    }

    [Fact]
    public async Task Being_offline_is_logged_and_ignored()
    {
        await factory.ResetAsync();
        factory.GitHub.Error = () => new HttpRequestException("The network is offline.");
        await factory.RunAsync();
        await factory.RunAsync();

        Assert.Single(factory.GitHub.Requests);
        Assert.Contains("Update check failed.", factory.Logs.Text(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, (await this.ParentNoticeAsync()).GetProperty("available").ValueKind);
        HttpResponseMessage health = await factory.CreateClient().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task The_request_carries_only_the_app_name_and_version()
    {
        await factory.ResetAsync();
        await factory.SetAsync("updates.url", "https://evil.example/releases?per_page=100");
        factory.GitHub.Json("[]");
        await factory.RunAsync();

        CapturedRequest request = Assert.Single(factory.GitHub.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(UpdateChecker.ReleasesUrl, request.Uri);
        Assert.Equal("https://api.github.com/repos/glensouza/wishlistarr/releases?per_page=100", request.Uri);
        Assert.DoesNotContain("evil.example", request.Uri, StringComparison.Ordinal);
        Assert.Equal("Wishlistarr/" + AppVersion.Current, request.Header("User-Agent"));
        Assert.DoesNotContain("(", request.Header("User-Agent"), StringComparison.Ordinal);
        Assert.Equal(UpdateChecker.AcceptMediaType, request.Header("Accept"));
        Assert.Equal("2022-11-28", request.Header("X-GitHub-Api-Version"));
        Assert.Null(request.Header("Authorization"));
        Assert.Null(request.Header("Cookie"));

        HttpClient client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(UpdateChecker.HttpClientName);
        Assert.Equal(UpdateChecker.Timeout, client.Timeout);
        Assert.True(client.Timeout <= TimeSpan.FromSeconds(10));
        Assert.Equal(UpdateChecker.MaxResponseBytes, (int)client.MaxResponseContentBufferSize);
    }

    [Fact]
    public async Task The_check_runs_at_most_once_a_day()
    {
        await factory.ResetAsync();
        factory.GitHub.Json("[]");
        await factory.RunAsync();
        await factory.RunAsync();
        factory.Clock.Advance(TimeSpan.FromHours(23));
        await factory.RunAsync();
        Assert.Single(factory.GitHub.Requests);

        factory.Clock.Advance(TimeSpan.FromHours(1));
        await factory.RunAsync();
        Assert.Equal(2, factory.GitHub.Requests.Count);

        Assert.Equal(TimeSpan.FromDays(1), UpdateChecker.Interval);
        JobDefinition definition = factory.Services.GetServices<JobDefinition>().Single(item => item.Name == UpdateCheckJob.JobName);
        Assert.Equal(TimeSpan.FromDays(1), definition.DefaultInterval);
        Assert.Equal("Checks GitHub for a newer release.", definition.Description);
        JobSchedule schedule = new JobOptions().Resolve(definition);
        Assert.True(schedule.Enabled);
        Assert.Equal(TimeSpan.FromDays(1), schedule.Interval);
    }

    [Fact]
    public async Task A_future_check_time_and_a_disabled_setting_skip_the_call()
    {
        await factory.ResetAsync();
        await factory.SetAsync(UpdateChecker.CheckedAtKey, factory.Clock.GetUtcNow().AddDays(1).ToString("O"));
        factory.GitHub.Json("[]");
        await factory.RunAsync();
        Assert.Empty(factory.GitHub.Requests);

        await factory.ResetAsync();
        string tag = Tag(Running(), 1);
        await factory.SetAsync(UpdateChecker.CheckSettingKey, "false");
        await factory.SetAsync(UpdateChecker.TagKey, tag);
        await factory.SetAsync(UpdateChecker.ReleaseNotesUrlKey, UpdateChecker.ReleaseNotesUrlFor(tag));
        await factory.RunAsync();

        Assert.Empty(factory.GitHub.Requests);
        Assert.Null(await factory.SettingAsync(UpdateChecker.CheckedAtKey));
        JsonElement notice = await this.ParentNoticeAsync();
        Assert.False(notice.GetProperty("checkEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, notice.GetProperty("available").ValueKind);
        Assert.DoesNotContain("github.com/glensouza/wishlistarr/releases", notice.GetRawText(), StringComparison.Ordinal);

        await factory.SetAsync(UpdateChecker.CheckSettingKey, "sometimes");
        await factory.RunAsync();
        Assert.Empty(factory.GitHub.Requests);
        Assert.False((await this.ParentNoticeAsync()).GetProperty("checkEnabled").GetBoolean());
    }

    [Fact]
    public async Task Parents_can_turn_the_check_off_and_cannot_write_the_stored_notice()
    {
        await factory.ResetAsync();
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        HttpResponseMessage turnedOff = await client.PutAsJsonAsync("/api/v1/updates", new { checkEnabled = false });
        Assert.Equal(HttpStatusCode.NoContent, turnedOff.StatusCode);
        Assert.False((await this.ParentNoticeAsync()).GetProperty("checkEnabled").GetBoolean());

        HttpResponseMessage missing = await client.PutAsJsonAsync("/api/v1/updates", new { });
        HttpResponseMessage empty = await client.PutAsJsonAsync<UpdateCheckUpdate?>("/api/v1/updates", null);
        foreach (HttpResponseMessage response in new[] { missing, empty })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("The update check must be true or false.", problem.GetProperty("title").GetString());
        }

        HttpResponseMessage canonical = await client.PutAsJsonAsync(
            "/api/v1/settings/updates.check",
            new { value = " FALSE ", isSecret = true });
        Assert.Equal(HttpStatusCode.NoContent, canonical.StatusCode);
        JsonElement listed = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings");
        JsonElement row = listed.EnumerateArray().Single(item => item.GetProperty("key").GetString() == "updates.check");
        Assert.False(row.GetProperty("isSecret").GetBoolean());
        Assert.Equal("false", row.GetProperty("value").GetString());

        HttpResponseMessage rejected = await client.PutAsJsonAsync(
            "/api/v1/settings/updates.check",
            new { value = "sometimes", isSecret = true });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("false", await factory.SettingAsync(UpdateChecker.CheckSettingKey));

        HttpResponseMessage tag = await client.PutAsJsonAsync(
            "/api/v1/settings/" + UpdateChecker.TagKey,
            new { value = "v9.9.9", isSecret = false });
        HttpResponseMessage notes = await client.PutAsJsonAsync(
            "/api/v1/settings/" + UpdateChecker.ReleaseNotesUrlKey,
            new { value = "https://evil.example/notes", isSecret = false });
        Assert.Equal(HttpStatusCode.BadRequest, tag.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notes.StatusCode);
        Assert.Null(await factory.SettingAsync(UpdateChecker.TagKey));
        Assert.Null(await factory.SettingAsync(UpdateChecker.ReleaseNotesUrlKey));
        Assert.DoesNotContain("system.updates", (await client.GetStringAsync("/api/v1/settings")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stored_notice_is_shown_only_when_it_is_still_newer_and_canonical()
    {
        await factory.ResetAsync();
        string tag = Tag(Running(), 1);
        await factory.SetAsync(UpdateChecker.TagKey, tag);
        await factory.SetAsync(UpdateChecker.ReleaseNotesUrlKey, "https://evil.example/notes");

        JsonElement hidden = await this.ParentNoticeAsync();
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("available").ValueKind);
        Assert.DoesNotContain("evil.example", hidden.GetRawText(), StringComparison.Ordinal);
        Assert.Empty(factory.GitHub.Requests);

        await factory.SetAsync(UpdateChecker.ReleaseNotesUrlKey, UpdateChecker.ReleaseNotesUrlFor(tag));
        JsonElement shown = await this.ParentNoticeAsync();
        Assert.Equal(tag[1..], shown.GetProperty("available").GetProperty("version").GetString());
        Assert.Equal(UpdateChecker.ReleaseNotesUrlFor(tag), shown.GetProperty("available").GetProperty("releaseNotesUrl").GetString());
        Assert.Empty(factory.GitHub.Requests);
    }

    [Fact]
    public async Task Kids_and_the_status_endpoint_do_not_receive_the_notice()
    {
        await factory.ResetAsync();
        string tag = Tag(Running(), 1);
        await factory.SetAsync(UpdateChecker.TagKey, tag);
        await factory.SetAsync(UpdateChecker.ReleaseNotesUrlKey, UpdateChecker.ReleaseNotesUrlFor(tag));
        Assert.NotNull((await this.ParentNoticeAsync()).GetProperty("available").GetProperty("version").GetString());

        HttpClient kid = factory.CreateClient();
        kid.DefaultRequestHeaders.Add(AuthApiFactory.KidHeader, "kid");
        HttpResponseMessage kidGet = await kid.GetAsync("/api/v1/updates");
        HttpResponseMessage kidPut = await kid.PutAsJsonAsync("/api/v1/updates", new { checkEnabled = false });
        Assert.Equal(HttpStatusCode.Forbidden, kidGet.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, kidPut.StatusCode);
        Assert.DoesNotContain("releaseNotesUrl", await kidGet.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("glensouza/wishlistarr/releases", await kidPut.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        HttpClient anonymous = factory.CreateClient();
        string anonymousStatus = await anonymous.GetStringAsync("/api/v1/auth/status");
        string kidStatus = await kid.GetStringAsync("/api/v1/auth/status");
        Assert.DoesNotContain("releaseNotesUrl", anonymousStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("glensouza/wishlistarr/releases", anonymousStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("releaseNotesUrl", kidStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("glensouza/wishlistarr/releases", kidStatus, StringComparison.Ordinal);
        Assert.True((await this.ParentNoticeAsync()).GetProperty("checkEnabled").GetBoolean());
    }

    [Fact]
    public async Task Startup_does_not_call_github()
    {
        using ReleaseClientFactory host = new();
        HttpResponseMessage health = await host.CreateClient().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        JobDefinition definition = host.Services.GetRequiredService<JobRunner>().Find(UpdateCheckJob.JobName)!;
        JobSchedule schedule = host.Services.GetRequiredService<IOptions<JobOptions>>().Value.Resolve(definition);
        Assert.False(schedule.Enabled);
        Assert.Null(schedule.Interval);

        HttpMessageHandler handler = host.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(UpdateChecker.HttpClientName);
        SocketsHttpHandler sockets = PrimaryHandler(handler);
        Assert.False(sockets.AllowAutoRedirect);
        Assert.False(sockets.UseCookies);
        Assert.Equal(DecompressionMethods.None, sockets.AutomaticDecompression);
        Assert.Null(sockets.ActivityHeadersPropagator);
    }

    [Fact]
    public void Docs_say_the_update_check_is_the_only_call_the_app_makes_to_github()
    {
        string root = RepoRoot();
        string upgrading = File.ReadAllText(Path.Combine(root, "docs", "upgrading.md"));
        Assert.Contains("the only call Wishlistarr makes to GitHub by itself", upgrading, StringComparison.Ordinal);
        Assert.Contains(UpdateChecker.ReleasesUrl, upgrading, StringComparison.Ordinal);
        Assert.Equal("https://github.com/glensouza/wishlistarr/blob/main/docs/upgrading.md", UpdateChecker.UpgradeNotesUrl);
        string jobs = File.ReadAllText(Path.Combine(root, "docs", "jobs.md"));
        Assert.Contains("update-check", jobs, StringComparison.Ordinal);
        Assert.Contains("the only call Wishlistarr makes to GitHub by itself", jobs, StringComparison.Ordinal);
    }

    private async Task<JsonElement> ParentNoticeAsync()
    {
        HttpClient client = await factory.CreateAuthorizedClientAsync();
        return await client.GetFromJsonAsync<JsonElement>("/api/v1/updates");
    }

    private static SemanticVersion Running()
    {
        Assert.True(SemanticVersion.TryParse(AppVersion.Current, out SemanticVersion? version));
        Assert.NotNull(version);
        Assert.False(version.IsPrerelease);
        return version;
    }

    private static string Tag(SemanticVersion current, int patchDelta) =>
        $"v{current.Major}.{current.Minor}.{current.Patch + patchDelta}";

    private static string OlderTag(SemanticVersion current)
    {
        Assert.True(current.Major > 0);
        return $"v{current.Major - 1}.9.9";
    }

    private static string Release(string tag, bool draft, bool prerelease, string htmlUrl) =>
        $$"""
        {"url":"https://api.github.com/repos/glensouza/wishlistarr/releases/1","html_url":"{{htmlUrl}}","id":1,"tag_name":"{{tag}}","name":"{{tag}}","draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}},"author":{"login":"octocat","id":1},"published_at":"2026-10-01T00:00:00Z","body":"notes","assets":[]}
        """;

    private static SocketsHttpHandler PrimaryHandler(HttpMessageHandler handler)
    {
        while (handler is DelegatingHandler delegating)
        {
            Assert.NotNull(delegating.InnerHandler);
            handler = delegating.InnerHandler;
        }

        return Assert.IsType<SocketsHttpHandler>(handler);
    }

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wishlistarr.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}

public sealed class ReleaseClientFactory : ApiFactory
{
    protected override bool ReplaceGitHubHandler => false;
}

public class UpdateApiFactory : AuthApiFactory
{
    private readonly ResettableTime clock = new(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
    private readonly GitHubCapture gitHub = new();
    private readonly CollectingLog logs = new();

    public ResettableTime Clock => this.clock;

    public GitHubCapture GitHub => this.gitHub;

    public CollectingLog Logs => this.logs;

    public async Task ResetAsync()
    {
        this.clock.MoveTo(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
        this.gitHub.Reset();
        this.logs.Clear();
        await this.DeleteAsync(UpdateChecker.CheckSettingKey);
        await this.DeleteAsync(UpdateChecker.CheckedAtKey);
        await this.DeleteAsync(UpdateChecker.TagKey);
        await this.DeleteAsync(UpdateChecker.ReleaseNotesUrlKey);
        await this.DeleteAsync("updates.url");
    }

    public async Task SetAsync(string key, string value)
    {
        using IServiceScope scope = this.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SettingsStore>().SetAsync(key, value, false);
    }

    public async Task<string?> SettingAsync(string key)
    {
        using IServiceScope scope = this.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SettingsStore>().GetAsync(key);
    }

    public async Task RunAsync()
    {
        using IServiceScope scope = this.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<UpdateCheckJob>().RunAsync(CancellationToken.None);
    }

    protected override HttpMessageHandler CreateGitHubHandler() => new RecordingGitHubHandler(this.gitHub);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(this.clock);
            services.AddSingleton<ILogEventSink>(this.logs);
        });
    }

    private async Task DeleteAsync(string key)
    {
        using IServiceScope scope = this.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SettingsStore>().DeleteAsync(key);
    }
}

public sealed class ResettableTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;

    public void MoveTo(DateTimeOffset value) => this.now = value;

    public void Advance(TimeSpan by) => this.now += by;

    public override DateTimeOffset GetUtcNow() => this.now;
}

public sealed class GitHubCapture
{
    private readonly List<CapturedRequest> requests = [];

    public IReadOnlyList<CapturedRequest> Requests => this.requests;

    public Func<HttpResponseMessage>? Response { get; set; }

    public Func<Exception>? Error { get; set; }

    public void Reset()
    {
        this.requests.Clear();
        this.Response = null;
        this.Error = null;
    }

    public void Json(string body) => this.Status(HttpStatusCode.OK, body);

    public void Status(HttpStatusCode status, string body) =>
        this.Response = () => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    public void Add(CapturedRequest request) => this.requests.Add(request);
}

public sealed record CapturedRequest(string Method, string Uri, Dictionary<string, string> Headers)
{
    public string? Header(string name) => this.Headers.TryGetValue(name, out string? value) ? value : null;
}

public sealed class CollectingLog : ILogEventSink
{
    private readonly List<string> lines = [];

    public void Emit(LogEvent logEvent)
    {
        string message = logEvent.RenderMessage();
        if (logEvent.Exception is not null)
        {
            message = message + " " + logEvent.Exception.Message;
        }

        lock (this.lines)
        {
            this.lines.Add(message);
        }
    }

    public void Clear()
    {
        lock (this.lines)
        {
            this.lines.Clear();
        }
    }

    public string Text()
    {
        lock (this.lines)
        {
            return string.Join('\n', this.lines);
        }
    }
}

file sealed class RecordingGitHubHandler(GitHubCapture capture) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        capture.Add(new CapturedRequest(request.Method.Method, request.RequestUri?.AbsoluteUri ?? string.Empty, headers));
        Exception? error = capture.Error?.Invoke();
        if (error is not null)
        {
            return Task.FromException<HttpResponseMessage>(error);
        }

        HttpResponseMessage response = capture.Response?.Invoke()
            ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            };
        return Task.FromResult(response);
    }
}
