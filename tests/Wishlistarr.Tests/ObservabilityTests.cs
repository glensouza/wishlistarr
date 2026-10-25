using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Requests;
using Wishlistarr.Api.Security;
using Wishlistarr.Api.System;
using Wishlistarr.Api.Webhooks;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.Fulfilment;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Seerr;
using Wishlistarr.Integrations.Tmdb;
using Wishlistarr.Tests.Arr;
using Wishlistarr.Tests.Seerr;
using Wishlistarr.Tests.Tmdb;

namespace Wishlistarr.Tests;

public sealed class ObservabilityApiFactory : AuthApiFactory
{
    public bool MetricsEnabled { get; init; }

    public int LogCapacity { get; init; } = 1000;

    public FakeTmdbServer? Tmdb { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Metrics:Enabled", this.MetricsEnabled ? "true" : "false");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logs:Capacity"] = this.LogCapacity.ToString(CultureInfo.InvariantCulture),
                ["Health:CacheDuration"] = "00:00:00",
                ["Jobs:Schedules:job-history-cleanup:Enabled"] = "false",
                ["Jobs:Schedules:webhook-events:Enabled"] = "false",
                ["Jobs:Schedules:tmdb-data-purge:Enabled"] = "false",
                ["Jobs:Schedules:library-full-refresh:Enabled"] = "false",
                ["Jobs:Schedules:library-incremental-refresh:Enabled"] = "false",
            }));
        if (this.Tmdb is not null)
        {
            FakeTmdbServer tmdb = this.Tmdb;
            builder.ConfigureTestServices(services => services.Configure<TmdbOptions>(options =>
            {
                options.ApiBaseAddress = tmdb.ApiBaseAddress;
                options.ImageBaseAddress = tmdb.ImageBaseAddress;
            }));
        }
    }
}

public sealed class LogRedactorTests
{
    [Theory]
    [InlineData("Authorization: Bearer bearer-secret-qq")]
    [InlineData("X-Plex-Token: plex-token-qq")]
    [InlineData("X-Api-Key: header-key-qq")]
    [InlineData("callback ?X-Plex-Token=plex-token-qq done")]
    [InlineData("sent apiKey=api-key-qq later")]
    [InlineData("password=password-secret-qq")]
    [InlineData("pin=pin-secret-qq")]
    [InlineData("/api/v1/webhooks/sonarr/webhook-token-qq arrived")]
    public void Redact_removes_the_secret(string text)
    {
        string secret = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Part).First(part => part.Contains("qq", StringComparison.Ordinal));
        string redacted = LogRedactor.Redact(text);

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains("***", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_removes_json_secrets_and_keeps_the_word_token()
    {
        string redacted = LogRedactor.Redact("""{"apiKey":"api-key-qq","password":"password-qq","pin":"pin-secret-qq"} the token word stays""");

        Assert.DoesNotContain("api-key-qq", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("password-qq", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("pin-secret-qq", redacted, StringComparison.Ordinal);
        Assert.Contains("token", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Password", true)]
    [InlineData("X-Plex-Token", true)]
    [InlineData("message", false)]
    public void Secret_names_are_matched_without_stripping_punctuation(string name, bool secret) =>
        Assert.Equal(secret, LogRedactor.IsSecretName(name));

    private static string Part(string token)
    {
        int equals = token.IndexOf('=');
        int colon = token.LastIndexOf(':');
        int slash = token.LastIndexOf('/');
        int cut = Math.Max(equals, Math.Max(colon, slash));
        return cut < 0 ? token : token[(cut + 1)..];
    }
}

public sealed class ObservabilityTests
{
    private const string BearerSecret = "bearer-secret-qq";
    private const string PlexToken = "plex-token-qq";
    private const string ApiKeySecret = "api-key-qq";
    private const string PasswordSecret = "password-qq";
    private const string PinSecret = "pin-secret-qq";
    private const string WebhookToken = "webhook-token-qq";
    private const string HeaderKey = "header-key-qq";
    private const string StructuredPassword = "structured-password-qq";
    private const string ExceptionSecret = "password-secret-qq";

    [Fact]
    public async Task Each_integration_reports_a_status_a_message_and_a_fix()
    {
        using ObservabilityApiFactory factory = new();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        HttpClient kid = factory.CreateClient();
        kid.DefaultRequestHeaders.Add(AuthApiFactory.KidHeader, "1");

        using JsonDocument document = JsonDocument.Parse(await parent.GetStringAsync("/api/v1/system/health"));
        JsonElement[] checks = [.. document.RootElement.EnumerateArray()];
        string kidBody = await (await kid.GetAsync("/api/v1/system/health")).Content.ReadAsStringAsync();
        string publicHealth = await factory.CreateClient().GetStringAsync("/api/health");

        Assert.Equal(["plex", "tmdb", "seerr", "sonarr", "radarr", "webhooks", "jobs", "database"], checks.Select(check => check.GetProperty("integration").GetString()));
        Assert.All(checks, check =>
        {
            Assert.False(string.IsNullOrWhiteSpace(check.GetProperty("message").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(check.GetProperty("fix").GetString()));
        });
        Assert.Equal("warning", Check(document.RootElement, "plex").GetProperty("status").GetString());
        Assert.Equal("Plex is not linked.", Check(document.RootElement, "plex").GetProperty("message").GetString());
        Assert.Contains("Link a Plex account", Check(document.RootElement, "plex").GetProperty("fix").GetString(), StringComparison.Ordinal);
        Assert.Equal("warning", Check(document.RootElement, "tmdb").GetProperty("status").GetString());
        Assert.Equal("The TMDB API key is not set.", Check(document.RootElement, "tmdb").GetProperty("message").GetString());
        Assert.Equal("ok", Check(document.RootElement, "seerr").GetProperty("status").GetString());
        Assert.Equal("ok", Check(document.RootElement, "sonarr").GetProperty("status").GetString());
        Assert.Equal("ok", Check(document.RootElement, "radarr").GetProperty("status").GetString());
        Assert.Equal("ok", Check(document.RootElement, "webhooks").GetProperty("status").GetString());
        Assert.Equal("ok", Check(document.RootElement, "jobs").GetProperty("status").GetString());
        Assert.Equal("No background job has failed.", Check(document.RootElement, "jobs").GetProperty("message").GetString());
        Assert.Equal("ok", Check(document.RootElement, "database").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await kid.GetAsync("/api/v1/system/health")).StatusCode);
        Assert.DoesNotContain("Plex is not linked", kidBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Plex", publicHealth, StringComparison.Ordinal);
        Assert.DoesNotContain("tmdb", publicHealth, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Tmdb_health_follows_the_saved_key()
    {
        await using FakeTmdbServer tmdb = await FakeTmdbServer.StartAsync();
        using ObservabilityApiFactory factory = new() { Tmdb = tmdb };
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        await client.PutAsJsonAsync($"/api/v1/settings/{TmdbOptions.ApiKeySetting}", new { value = "wrong-tmdb-key-qq" });
        using JsonDocument rejected = JsonDocument.Parse(await client.GetStringAsync("/api/v1/system/health"));
        string rejectedBody = rejected.RootElement.GetRawText();

        await client.PutAsJsonAsync($"/api/v1/settings/{TmdbOptions.ApiKeySetting}", new { value = FakeTmdbServer.ValidApiKey });
        using JsonDocument accepted = JsonDocument.Parse(await client.GetStringAsync("/api/v1/system/health"));

        Assert.Equal("error", Check(rejected.RootElement, "tmdb").GetProperty("status").GetString());
        Assert.Equal("TMDB rejected the API key.", Check(rejected.RootElement, "tmdb").GetProperty("message").GetString());
        Assert.DoesNotContain("wrong-tmdb-key-qq", rejectedBody, StringComparison.Ordinal);
        Assert.Equal("ok", Check(accepted.RootElement, "tmdb").GetProperty("status").GetString());
        Assert.Equal("The TMDB API key works.", Check(accepted.RootElement, "tmdb").GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(ArrService.Sonarr, "sonarr")]
    [InlineData(ArrService.Radarr, "radarr")]
    public async Task Arr_health_is_ok_until_a_saved_connection_fails(ArrService service, string route)
    {
        using ObservabilityApiFactory factory = new();
        await using FakeArrServer fake = await FakeArrServer.StartAsync(service);
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        using JsonDocument unconfigured = JsonDocument.Parse(await client.GetStringAsync("/api/v1/system/health"));
        await client.PutAsJsonAsync($"/api/v1/settings/{route}", new { url = fake.Address.ToString(), apiKey = "wrong-arr-key-qq" });
        using JsonDocument rejected = JsonDocument.Parse(await client.GetStringAsync("/api/v1/system/health"));
        await client.PutAsJsonAsync($"/api/v1/settings/{route}", new
        {
            url = fake.Address.ToString(),
            apiKey = FakeArrServer.ApiKey,
            qualityProfileId = 4,
            rootFolderPath = "/media/kids",
            tagIds = new[] { 1 },
        });
        using JsonDocument accepted = JsonDocument.Parse(await client.GetStringAsync("/api/v1/system/health"));

        Assert.Equal("ok", Check(unconfigured.RootElement, route).GetProperty("status").GetString());
        Assert.Contains("not configured", Check(unconfigured.RootElement, route).GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("error", Check(rejected.RootElement, route).GetProperty("status").GetString());
        Assert.Contains("rejected the API key", Check(rejected.RootElement, route).GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-arr-key-qq", rejected.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(FakeArrServer.ApiKey, accepted.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("ok", Check(accepted.RootElement, route).GetProperty("status").GetString());
        Assert.Contains("accepted the API key", Check(accepted.RootElement, route).GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Seerr_health_warns_about_permissions_and_hides_the_api_key()
    {
        using ObservabilityApiFactory factory = new();
        await using FakeSeerrServer fake = await FakeSeerrServer.StartAsync();
        fake.Permissions = 0;
        HttpClient client = await factory.CreateAuthorizedClientAsync();
        await client.PutAsJsonAsync("/api/v1/settings/seerr", new { url = fake.Address.ToString(), apiKey = "seerr-secret-key-qq", userId = FakeSeerrServer.DefaultUserId });

        using JsonDocument rejected = JsonDocument.Parse(await client.GetStringAsync("/api/v1/system/health"));
        await client.PutAsJsonAsync("/api/v1/settings/seerr", new { url = fake.Address.ToString(), apiKey = FakeSeerrServer.ApiKey, userId = FakeSeerrServer.DefaultUserId });
        using JsonDocument warned = JsonDocument.Parse(await client.GetStringAsync("/api/v1/system/health"));

        Assert.Equal("error", Check(rejected.RootElement, "seerr").GetProperty("status").GetString());
        Assert.Equal("Seerr rejected the API key.", Check(rejected.RootElement, "seerr").GetProperty("message").GetString());
        Assert.DoesNotContain("seerr-secret-key-qq", rejected.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("warning", Check(warned.RootElement, "seerr").GetProperty("status").GetString());
        Assert.Contains(SeerrBackend.MovieRequestWarning, Check(warned.RootElement, "seerr").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSeerrServer.ApiKey, warned.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Webhook_health_warns_only_after_an_event_sits_unprocessed()
    {
        using ObservabilityApiFactory fresh = new();
        await AddWebhookAsync(fresh, DateTimeOffset.UtcNow, "fresh-payload-secret-qq");
        using JsonDocument ready = JsonDocument.Parse(await (await fresh.CreateAuthorizedClientAsync()).GetStringAsync("/api/v1/system/health"));

        using ObservabilityApiFactory stuck = new();
        await AddWebhookAsync(stuck, DateTimeOffset.UtcNow.AddMinutes(-5), "stuck-payload-secret-qq");
        string body = await (await stuck.CreateAuthorizedClientAsync()).GetStringAsync("/api/v1/system/health");
        using JsonDocument waiting = JsonDocument.Parse(body);

        Assert.Equal("ok", Check(ready.RootElement, "webhooks").GetProperty("status").GetString());
        Assert.Equal("warning", Check(waiting.RootElement, "webhooks").GetProperty("status").GetString());
        Assert.Equal("Webhook events are still waiting to be processed.", Check(waiting.RootElement, "webhooks").GetProperty("message").GetString());
        Assert.DoesNotContain("stuck-payload-secret-qq", body, StringComparison.Ordinal);
        Assert.DoesNotContain("fresh-payload-secret-qq", ready.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Webhook_health_is_an_error_when_the_processor_failed()
    {
        using ObservabilityApiFactory factory = new();
        await AddJobFailureAsync(factory, WebhookEventProcessor.JobName, "password=password-secret-qq");

        string body = await (await factory.CreateAuthorizedClientAsync()).GetStringAsync("/api/v1/system/health");
        using JsonDocument document = JsonDocument.Parse(body);

        Assert.Equal("error", Check(document.RootElement, "webhooks").GetProperty("status").GetString());
        Assert.Equal("The webhook-events job failed on its last run.", Check(document.RootElement, "webhooks").GetProperty("message").GetString());
        Assert.DoesNotContain("password-secret-qq", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Job_health_names_a_failed_run_and_redacts_its_error()
    {
        using ObservabilityApiFactory factory = new();
        await AddJobFailureAsync(factory, JobHistoryCleanup.JobName, "password=password-secret-qq");

        string body = await (await factory.CreateAuthorizedClientAsync()).GetStringAsync("/api/v1/system/health");
        using JsonDocument document = JsonDocument.Parse(body);
        string message = Check(document.RootElement, "jobs").GetProperty("message").GetString()!;

        Assert.Equal("error", Check(document.RootElement, "jobs").GetProperty("status").GetString());
        Assert.Contains(JobHistoryCleanup.JobName, message, StringComparison.Ordinal);
        Assert.Contains("password=***", message, StringComparison.Ordinal);
        Assert.DoesNotContain(ExceptionSecret, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Database_health_fails_closed_without_the_path_or_the_exception()
    {
        string database = Path.Combine(Path.GetTempPath(), "wishlistarr-missing-" + Guid.NewGuid().ToString("N"), "wishlistarr.db");
        ServiceCollection services = new();
        services.AddDbContext<WishlistarrDbContext>(options => options.UseWishlistarrSqlite(database));
        await using ServiceProvider provider = services.BuildServiceProvider();

        HealthCheckResult result = await new DatabaseHealthCheck().CheckAsync(provider, CancellationToken.None);

        Assert.Equal("database", result.Integration);
        Assert.Equal(HealthCheckStatus.Error, result.Status);
        Assert.Equal("The database could not be opened.", result.Message);
        Assert.DoesNotContain(database, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQLite", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WISHLISTARR_DATA_DIR", result.Fix, StringComparison.Ordinal);
        Assert.False(File.Exists(database));
    }

    [Fact]
    public async Task Log_pipeline_redacts_secrets_before_they_can_be_listed_or_downloaded()
    {
        using ObservabilityApiFactory factory = new();
        HttpClient client = await factory.CreateAuthorizedClientAsync();
        ILogger logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Wishlistarr.Tests.Logs");
        logger.LogInformation(
            """
            Authorization: Bearer bearer-secret-qq ?X-Plex-Token=plex-token-qq {{"apiKey":"api-key-qq","password":"password-qq","pin":"pin-secret-qq"}} /api/v1/webhooks/sonarr/webhook-token-qq X-Api-Key: header-key-qq the token word stays
            """);
        logger.LogError(new InvalidOperationException("password=password-secret-qq"), "save failed");
        logger.LogWarning("structured {Password}", StructuredPassword);

        string listed = await client.GetStringAsync("/api/v1/system/logs");
        string downloaded = await client.GetStringAsync("/api/v1/system/logs/download");
        using JsonDocument document = JsonDocument.Parse(listed);
        JsonElement saved = document.RootElement.EnumerateArray().Single(entry => entry.GetProperty("message").GetString()!.Contains("save failed", StringComparison.Ordinal));

        Assert.Contains("the token word stays", listed, StringComparison.Ordinal);
        Assert.Contains("Wishlistarr.Tests.Logs", listed, StringComparison.Ordinal);
        Assert.Contains("save failed", saved.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("error", saved.GetProperty("level").GetString());
        Assert.DoesNotContain(StructuredPassword, listed, StringComparison.Ordinal);
        foreach (string secret in new[] { BearerSecret, PlexToken, ApiKeySecret, PasswordSecret, PinSecret, WebhookToken, HeaderKey, StructuredPassword, ExceptionSecret })
        {
            Assert.DoesNotContain(secret, listed, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, downloaded, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Log_list_filters_by_level_and_rejects_an_unknown_level()
    {
        using ObservabilityApiFactory factory = new();
        HttpClient client = await factory.CreateAuthorizedClientAsync();
        ILogger logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Wishlistarr.Tests.Logs");
        logger.LogInformation("info-line-qq");
        logger.LogWarning("warn-line-qq");

        string warning = await client.GetStringAsync("/api/v1/system/logs?level=warning");
        string all = await client.GetStringAsync("/api/v1/system/logs");
        HttpResponseMessage invalid = await client.GetAsync("/api/v1/system/logs?level=none");
        JsonElement problem = await invalid.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains("warn-line-qq", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("info-line-qq", warning, StringComparison.Ordinal);
        Assert.Contains("info-line-qq", all, StringComparison.Ordinal);
        Assert.Contains("warn-line-qq", all, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("The level must be trace, debug, information, warning, error or critical.", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Log_buffer_drops_the_oldest_entry_at_its_capacity()
    {
        using ObservabilityApiFactory factory = new() { LogCapacity = 2 };
        using (await factory.CreateAuthorizedClientAsync())
        {
        }

        ILogger logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Wishlistarr.Tests.Logs");
        logger.LogInformation("first-line-qq");
        logger.LogInformation("second-line-qq");
        logger.LogInformation("third-line-qq");

        LogBuffer logs = factory.Services.GetRequiredService<LogBuffer>();
        IReadOnlyList<LogEntry> entries = logs.List(null);

        Assert.Equal(2, entries.Count);
        Assert.DoesNotContain(entries, entry => entry.Message.Contains("first-line-qq", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Message.Contains("second-line-qq", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Message.Contains("third-line-qq", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Log_download_is_plain_text_and_a_kid_cannot_read_either_route()
    {
        using ObservabilityApiFactory factory = new();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        ILogger logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Wishlistarr.Tests.Logs");
        logger.LogWarning("Authorization: Bearer bearer-secret-qq warn-line-qq");
        HttpClient kid = factory.CreateClient();
        kid.DefaultRequestHeaders.Add(AuthApiFactory.KidHeader, "1");

        HttpResponseMessage download = await parent.GetAsync("/api/v1/system/logs/download?level=warning");
        string text = await download.Content.ReadAsStringAsync();
        HttpResponseMessage listed = await kid.GetAsync("/api/v1/system/logs");
        HttpResponseMessage downloaded = await kid.GetAsync("/api/v1/system/logs/download");

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("text/plain", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("wishlistarr.log", download.Content.Headers.ContentDisposition?.FileName);
        Assert.Contains("warn-line-qq", text, StringComparison.Ordinal);
        Assert.Contains("warning", text, StringComparison.Ordinal);
        Assert.DoesNotContain(BearerSecret, text, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, listed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, downloaded.StatusCode);
        Assert.DoesNotContain(BearerSecret, await listed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("warn-line-qq", await downloaded.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_history_is_newest_first_for_that_profile_only()
    {
        using ObservabilityApiFactory factory = new();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        int ada = await CreateProfileAsync(parent, "Ada");
        int ben = await CreateProfileAsync(parent, "Ben");
        await AddRequestAsync(factory, ada, "zz-ada-title-qq", 603);
        await AddRequestAsync(factory, ben, "zz-ben-title-qq", 604);
        int adaRequest = await RequestIdAsync(factory, ada);
        await TransitionAsync(factory, adaRequest);

        using JsonDocument adaEvents = JsonDocument.Parse(await parent.GetStringAsync($"/api/v1/kid-profiles/{ada}/events"));
        using JsonDocument limited = JsonDocument.Parse(await parent.GetStringAsync($"/api/v1/kid-profiles/{ada}/events?limit=1"));
        string benBody = await parent.GetStringAsync($"/api/v1/kid-profiles/{ben}/events");
        HttpResponseMessage missing = await parent.GetAsync("/api/v1/kid-profiles/999999/events");
        await parent.DeleteAsync($"/api/v1/kid-profiles/{ada}");
        string archived = await parent.GetStringAsync($"/api/v1/kid-profiles/{ada}/events");
        HttpClient kid = factory.CreateClient();
        kid.DefaultRequestHeaders.Add(AuthApiFactory.KidHeader, "1");
        HttpResponseMessage hidden = await kid.GetAsync($"/api/v1/kid-profiles/{ben}/events");

        JsonElement[] events = [.. adaEvents.RootElement.EnumerateArray()];
        Assert.Equal(["denied", "pending"], events.Select(item => item.GetProperty("toState").GetString()));
        Assert.Equal("zz-ada-title-qq", events[0].GetProperty("title").GetString());
        Assert.Equal("pending", events[0].GetProperty("fromState").GetString());
        Assert.Equal("parent", events[0].GetProperty("actorKind").GetString());
        Assert.Equal("not tonight", events[0].GetProperty("reason").GetString());
        Assert.Null(events[1].GetProperty("fromState").GetString());
        Assert.Equal("denied", limited.RootElement.EnumerateArray().Single().GetProperty("toState").GetString());
        Assert.Contains("zz-ben-title-qq", benBody, StringComparison.Ordinal);
        Assert.DoesNotContain("zz-ada-title-qq", benBody, StringComparison.Ordinal);
        Assert.DoesNotContain("zz-ben-title-qq", adaEvents.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("zz-ada-title-qq", archived, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, hidden.StatusCode);
        Assert.DoesNotContain("zz-ben-title-qq", await hidden.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Metrics_are_off_until_enabled_and_then_require_a_parent()
    {
        using ObservabilityApiFactory disabled = new();
        HttpClient disabledParent = await disabled.CreateAuthorizedClientAsync();
        HttpResponseMessage anonymous = await disabled.CreateClient().GetAsync("/metrics");
        HttpResponseMessage hidden = await disabledParent.GetAsync("/metrics");
        HttpClient disabledKid = disabled.CreateClient();
        disabledKid.DefaultRequestHeaders.Add(AuthApiFactory.KidHeader, "1");
        HttpResponseMessage kidHidden = await disabledKid.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, kidHidden.StatusCode);
        Assert.DoesNotContain("wishlistarr_", await hidden.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using ObservabilityApiFactory enabled = new() { MetricsEnabled = true };
        await enabled.EnsureParentAsync();
        int profile = await CreateProfileAsync(await enabled.CreateAuthorizedClientAsync(), "zz-profile-name-qq");
        await AddRequestAsync(enabled, profile, "zz-title-qq", 700);
        HttpClient parent = await enabled.CreateAuthorizedClientAsync();
        await parent.GetAsync("/api/v1/system/health");
        string apiKey = await enabled.GetApiKeyAsync();
        string webhookToken = await WebhookTokenAsync(parent);

        HttpResponseMessage enabledAnonymous = await enabled.CreateClient().GetAsync("/metrics");
        HttpClient enabledKid = enabled.CreateClient();
        enabledKid.DefaultRequestHeaders.Add(AuthApiFactory.KidHeader, "1");
        HttpResponseMessage enabledKidResponse = await enabledKid.GetAsync("/metrics");
        HttpResponseMessage scraped = await parent.GetAsync("/metrics");
        string body = await scraped.Content.ReadAsStringAsync();
        HttpClient session = await enabled.CreateSignedInClientAsync();
        HttpResponseMessage sessionScrape = await session.GetAsync("/metrics");
        RouteEndpoint endpoint = enabled.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Single(item => item.RoutePattern.RawText?.Contains("metric", StringComparison.OrdinalIgnoreCase) == true);

        Assert.Equal(HttpStatusCode.Unauthorized, enabledAnonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, enabledKidResponse.StatusCode);
        Assert.DoesNotContain("zz-title-qq", await enabledKidResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, scraped.StatusCode);
        Assert.Equal(HttpStatusCode.OK, sessionScrape.StatusCode);
        Assert.Contains("wishlistarr_integration_health", body, StringComparison.Ordinal);
        Assert.Contains("integration=\"database\"", body, StringComparison.Ordinal);
        Assert.Contains("integration=\"plex\"", body, StringComparison.Ordinal);
        Assert.Contains("kind=\"movie\"", body, StringComparison.Ordinal);
        Assert.Contains("state=\"pending\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("zz-profile-name-qq", body, StringComparison.Ordinal);
        Assert.DoesNotContain("zz-title-qq", body, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, body, StringComparison.Ordinal);
        Assert.DoesNotContain(webhookToken, body, StringComparison.Ordinal);
        Assert.DoesNotContain("profile=\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("title=\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pin=\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token=\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.MachineName, body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), data => data.Policy == Policies.Parent);
    }

    private static JsonElement Check(JsonElement checks, string name) =>
        checks.EnumerateArray().Single(item => item.GetProperty("integration").GetString() == name);

    private static async Task AddWebhookAsync(ObservabilityApiFactory factory, DateTimeOffset receivedAt, string payload)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        db.WebhookEvents.Add(new WebhookEvent
        {
            Source = "plex",
            DeliveryKey = payload,
            EventType = "library.new",
            Payload = payload,
            ReceivedAt = receivedAt,
        });
        await db.SaveChangesAsync();
    }

    private static async Task AddJobFailureAsync(ObservabilityApiFactory factory, string name, string error)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        db.JobRuns.Add(new JobRun
        {
            JobName = name,
            Trigger = JobRunTrigger.Manual,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
            Outcome = JobRunOutcome.Failed,
            Error = error,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<int> CreateProfileAsync(HttpClient parent, string name)
    {
        HttpResponseMessage response = await parent.PostAsJsonAsync("/api/v1/kid-profiles", new { name, avatar = "fox" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static async Task AddRequestAsync(ObservabilityApiFactory factory, int profileId, string title, int tmdbId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        MediaRequests requests = scope.ServiceProvider.GetRequiredService<MediaRequests>();
        RequestResult result = await requests.CreateAsync(profileId, new TitleKey(MediaKind.Movie, tmdbId), title, RequestActor.Kid(profileId, "Ada"));
        Assert.Equal(RequestOutcome.Created, result.Outcome);
    }

    private static async Task<int> RequestIdAsync(ObservabilityApiFactory factory, int profileId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        return await db.MediaRequests.Where(request => request.KidProfileId == profileId).Select(request => request.Id).SingleAsync();
    }

    private static async Task TransitionAsync(ObservabilityApiFactory factory, int requestId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        MediaRequests requests = scope.ServiceProvider.GetRequiredService<MediaRequests>();
        RequestResult result = await requests.TransitionAsync(requestId, RequestState.Denied, RequestActor.Parent(1, "parent"), "not tonight");
        Assert.Equal(RequestOutcome.Changed, result.Outcome);
    }

    private static async Task<string> WebhookTokenAsync(HttpClient parent)
    {
        JsonElement hooks = await parent.GetFromJsonAsync<JsonElement>("/api/v1/settings/webhooks");
        string url = hooks.EnumerateArray().First().GetProperty("url").GetString()!;
        string token = url.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];
        Assert.True(token.Length >= 32);
        return token;
    }
}
