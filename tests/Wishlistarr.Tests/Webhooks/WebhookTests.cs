using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Webhooks;
using Wishlistarr.Data;

namespace Wishlistarr.Tests.Webhooks;

public sealed class WebhookRecorder
{
    private readonly List<(string Source, string EventType)> handled = [];

    public IReadOnlyList<(string Source, string EventType)> Handled
    {
        get
        {
            lock (this.handled)
            {
                return [.. this.handled];
            }
        }
    }

    public void Add(string source, string eventType)
    {
        lock (this.handled)
        {
            this.handled.Add((source, eventType));
        }
    }
}

public sealed class RecordingTestHandler(WebhookSource source, WebhookRecorder recorder) : IWebhookHandler
{
    public WebhookSource Source => source;

    public Task HandleAsync(WebhookEvent webhookEvent, CancellationToken ct)
    {
        recorder.Add(webhookEvent.Source, webhookEvent.EventType);
        return Task.CompletedTask;
    }
}

public sealed class WebhookApiFactory : AuthApiFactory
{
    public const int PayloadLimit = 4096;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Webhooks:MaxPayloadBytes", PayloadLimit.ToString(CultureInfo.InvariantCulture));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<WebhookRecorder>();
            foreach (WebhookSource source in Enum.GetValues<WebhookSource>())
            {
                services.AddScoped<IWebhookHandler>(provider => new RecordingTestHandler(
                    source,
                    provider.GetRequiredService<WebhookRecorder>()));
            }
        });
    }
}

public sealed class WebhookTests(WebhookApiFactory factory) : IClassFixture<WebhookApiFactory>
{
    private static readonly string[] Sources = ["plex", "radarr", "seerr", "sonarr"];

    [Fact]
    public async Task Each_source_has_a_distinct_generated_url_with_a_token()
    {
        HttpClient parent = await factory.CreateAuthorizedClientAsync();

        Dictionary<string, string> urls = await GetUrlsAsync(parent);

        Assert.Equal(Sources, urls.Keys.Order());
        Assert.Equal(4, urls.Values.Distinct().Count());
        foreach ((string source, string url) in urls)
        {
            string[] segments = new Uri(url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(["api", "v1", "webhooks", source], segments[..4]);
            Assert.True(segments[4].Length >= 43);
        }
    }

    [Fact]
    public async Task Settings_endpoints_require_a_parent()
    {
        await factory.EnsureParentAsync();
        HttpClient anonymous = factory.CreateClient();
        HttpClient kid = factory.CreateClient();
        kid.DefaultRequestHeaders.Add(AuthApiFactory.KidHeader, "1");

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/settings/webhooks")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/v1/settings/webhooks/plex/regenerate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await kid.GetAsync("/api/v1/settings/webhooks")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await kid.PostAsync("/api/v1/settings/webhooks/plex/regenerate", null)).StatusCode);
    }

    [Fact]
    public async Task Tokens_are_not_listed_in_general_settings()
    {
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        Dictionary<string, string> urls = await GetUrlsAsync(parent);

        string settings = await parent.GetStringAsync("/api/v1/settings");

        Assert.DoesNotContain(TokenOf(urls["plex"]), settings);
        Assert.DoesNotContain("webhook", settings, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Regenerating_a_token_invalidates_the_old_address()
    {
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        HttpClient hook = factory.CreateClient();
        Dictionary<string, string> before = await GetUrlsAsync(parent);

        HttpResponseMessage response = await parent.PostAsync("/api/v1/settings/webhooks/sonarr/regenerate", null);
        JsonElement view = await response.Content.ReadFromJsonAsync<JsonElement>();
        string fresh = view.GetProperty("url").GetString()!;
        Dictionary<string, string> after = await GetUrlsAsync(parent);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(before["sonarr"], fresh);
        Assert.Equal(fresh, after["sonarr"]);
        Assert.Equal(before["radarr"], after["radarr"]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostJsonAsync(hook, before["sonarr"], """{"eventType":"Grab","n":1}""")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await PostJsonAsync(hook, fresh, """{"eventType":"Grab","n":1}""")).StatusCode);
    }

    [Fact]
    public async Task Unknown_source_cannot_be_regenerated()
    {
        HttpClient parent = await factory.CreateAuthorizedClientAsync();

        HttpResponseMessage response = await parent.PostAsync("/api/v1/settings/webhooks/nope/regenerate", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("wrong")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Requests_with_an_invalid_token_are_rejected_and_nothing_is_stored(string token)
    {
        await factory.EnsureParentAsync();
        HttpClient hook = factory.CreateClient();
        string marker = Guid.NewGuid().ToString("N");

        HttpResponseMessage response = await hook.PostAsync(
            $"/api/v1/webhooks/sonarr/{token}",
            new StringContent($$"""{"eventType":"Grab","marker":"{{marker}}"}""", Encoding.UTF8, "application/json"));

        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound);
        Assert.Equal(0, await this.CountStoredAsync(marker));
    }

    [Fact]
    public async Task A_token_for_another_source_is_rejected()
    {
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        HttpClient hook = factory.CreateClient();
        Dictionary<string, string> urls = await GetUrlsAsync(parent);

        HttpResponseMessage response = await PostJsonAsync(
            hook,
            $"/api/v1/webhooks/radarr/{TokenOf(urls["sonarr"])}",
            """{"eventType":"Grab"}""");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_source_is_not_found()
    {
        await factory.EnsureParentAsync();

        HttpResponseMessage response = await PostJsonAsync(factory.CreateClient(), "/api/v1/webhooks/nope/token", "{}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Oversized_payload_is_rejected_and_not_stored()
    {
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        HttpClient hook = factory.CreateClient();
        Dictionary<string, string> urls = await GetUrlsAsync(parent);
        string marker = Guid.NewGuid().ToString("N");
        string big = $$"""{"eventType":"Grab","marker":"{{marker}}","pad":"{{new string('x', WebhookApiFactory.PayloadLimit)}}"}""";

        HttpResponseMessage response = await PostJsonAsync(hook, urls["radarr"], big);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, await this.CountStoredAsync(marker));
    }

    [Fact]
    public async Task Oversized_body_without_a_length_header_is_rejected()
    {
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        HttpClient hook = factory.CreateClient();
        Dictionary<string, string> urls = await GetUrlsAsync(parent);
        string big = $$"""{"eventType":"Grab","pad":"{{new string('y', WebhookApiFactory.PayloadLimit)}}"}""";
        using HttpRequestMessage request = new(HttpMethod.Post, urls["radarr"])
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(big))),
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.TransferEncodingChunked = true;

        HttpResponseMessage response = await hook.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_delivery_is_processed_once()
    {
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        HttpClient hook = factory.CreateClient();
        Dictionary<string, string> urls = await GetUrlsAsync(parent);
        WebhookRecorder recorder = factory.Services.GetRequiredService<WebhookRecorder>();
        string marker = Guid.NewGuid().ToString("N");
        string body = $$"""{"eventType":"Download","marker":"{{marker}}"}""";

        HttpResponseMessage first = await PostJsonAsync(hook, urls["sonarr"], body);
        HttpResponseMessage second = await PostJsonAsync(hook, urls["sonarr"], body);
        HttpResponseMessage third = await PostJsonAsync(hook, urls["sonarr"], body);
        await WaitForAsync(() => recorder.Handled.Any(item => item == ("sonarr", "Download")));
        await this.WaitForIdleAsync();

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal("duplicate", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(1, await this.CountStoredAsync(marker));
        Assert.Single(recorder.Handled, item => item == ("sonarr", "Download"));
    }

    [Fact]
    public async Task Concurrent_duplicate_deliveries_are_stored_once()
    {
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        HttpClient hook = factory.CreateClient();
        Dictionary<string, string> urls = await GetUrlsAsync(parent);
        string marker = Guid.NewGuid().ToString("N");
        string body = $$"""{"eventType":"Test","marker":"{{marker}}"}""";

        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => PostJsonAsync(hook, urls["radarr"], body)));

        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Accepted));
        Assert.Equal(5, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, await this.CountStoredAsync(marker));
    }

    [Fact]
    public async Task Same_payload_from_different_sources_is_not_a_duplicate()
    {
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        HttpClient hook = factory.CreateClient();
        Dictionary<string, string> urls = await GetUrlsAsync(parent);
        string body = $$"""{"eventType":"Test","marker":"{{Guid.NewGuid():N}}"}""";

        HttpResponseMessage sonarr = await PostJsonAsync(hook, urls["sonarr"], body);
        HttpResponseMessage radarr = await PostJsonAsync(hook, urls["radarr"], body);

        Assert.Equal(HttpStatusCode.Accepted, sonarr.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, radarr.StatusCode);
    }

    [Fact]
    public async Task Plex_multipart_payload_is_parsed_and_enqueued()
    {
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        HttpClient hook = factory.CreateClient();
        Dictionary<string, string> urls = await GetUrlsAsync(parent);
        WebhookRecorder recorder = factory.Services.GetRequiredService<WebhookRecorder>();
        string marker = Guid.NewGuid().ToString("N");
        using MultipartFormDataContent form = new()
        {
            { new StringContent($$$"""{"event":"library.new","Metadata":{"title":"{{{marker}}}"}}""", Encoding.UTF8), "payload" },
            { new ByteArrayContent([1, 2, 3, 4]), "thumb", "poster.jpg" },
        };

        HttpResponseMessage response = await hook.PostAsync(urls["plex"], form);
        await WaitForAsync(() => recorder.Handled.Any(item => item == ("plex", "library.new")));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, await this.CountStoredAsync(marker));
    }

    [Fact]
    public async Task Plex_multipart_without_a_payload_part_is_rejected()
    {
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        Dictionary<string, string> urls = await GetUrlsAsync(parent);
        using MultipartFormDataContent form = new() { { new ByteArrayContent([1, 2, 3]), "thumb", "poster.jpg" } };

        HttpResponseMessage response = await factory.CreateClient().PostAsync(urls["plex"], form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("")]
    public async Task Malformed_payload_is_rejected(string body)
    {
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        Dictionary<string, string> urls = await GetUrlsAsync(parent);

        HttpResponseMessage response = await PostJsonAsync(factory.CreateClient(), urls["seerr"], body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("sonarr", """{"eventType":"Grab"}""", "Grab")]
    [InlineData("radarr", """{"eventType":"Download"}""", "Download")]
    [InlineData("seerr", """{"notification_type":"MEDIA_AVAILABLE"}""", "MEDIA_AVAILABLE")]
    [InlineData("seerr", """{"other":1}""", "unknown")]
    public async Task Event_type_is_read_from_the_source_specific_field(string source, string body, string expected)
    {
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        Dictionary<string, string> urls = await GetUrlsAsync(parent);
        WebhookRecorder recorder = factory.Services.GetRequiredService<WebhookRecorder>();
        string unique = body[..^1] + $$""","marker":"{{Guid.NewGuid():N}}"}""";

        HttpResponseMessage response = await PostJsonAsync(factory.CreateClient(), urls[source], unique);
        await WaitForAsync(() => recorder.Handled.Any(item => item == (source, expected)));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Parser_reads_the_payload_part_of_a_multipart_body()
    {
        string body = "--xyz\r\nContent-Disposition: form-data; name=\"thumb\"; filename=\"a.jpg\"\r\nContent-Type: image/jpeg\r\n\r\nbinary\r\n"
            + "--xyz\r\nContent-Disposition: form-data; name=\"payload\"\r\n\r\n{\"event\":\"media.play\"}\r\n--xyz--\r\n";

        ParsedWebhook? parsed = await WebhookPayloads.ParseAsync(
            WebhookSource.Plex,
            "multipart/form-data; boundary=xyz",
            Encoding.UTF8.GetBytes(body));

        Assert.NotNull(parsed);
        Assert.Equal("media.play", parsed.EventType);
        Assert.Equal("""{"event":"media.play"}""", parsed.Payload);
    }

    [Fact]
    public void Log_properties_do_not_expose_the_token()
    {
        Assert.Equal("/api/v1/webhooks/plex/***", WebhookTokenRedactor.Redact("/api/v1/webhooks/plex/secret-token"));
        Assert.Equal("/api/v1/webhooks/plex", WebhookTokenRedactor.Redact("/api/v1/webhooks/plex"));
        Assert.Equal("/api/v1/jobs", WebhookTokenRedactor.Redact("/api/v1/jobs"));

        LogEvent logEvent = new(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            null,
            new Serilog.Parsing.MessageTemplateParser().Parse("HTTP {RequestPath}"),
            [new LogEventProperty("RequestPath", new ScalarValue("/api/v1/webhooks/sonarr/secret-token"))]);
        new WebhookTokenRedactor().Enrich(logEvent, null!);

        Assert.DoesNotContain("secret-token", logEvent.RenderMessage(), StringComparison.Ordinal);
        Assert.Contains("***", logEvent.RenderMessage(), StringComparison.Ordinal);
    }

    private static string TokenOf(string url) => url[(url.LastIndexOf('/') + 1)..];

    private static async Task<Dictionary<string, string>> GetUrlsAsync(HttpClient parent)
    {
        JsonElement list = await parent.GetFromJsonAsync<JsonElement>("/api/v1/settings/webhooks");
        return list.EnumerateArray().ToDictionary(
            item => item.GetProperty("source").GetString()!,
            item => item.GetProperty("url").GetString()!);
    }

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string url, string body) =>
        client.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition(), "The condition was not met in time.");
    }

    private async Task<int> CountStoredAsync(string marker)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        return await db.WebhookEvents.CountAsync(item => item.Payload.Contains(marker));
    }

    private async Task WaitForIdleAsync()
    {
        JobRunner runner = factory.Services.GetRequiredService<JobRunner>();
        for (int attempt = 0; attempt < 100 && runner.IsRunning(WebhookEventProcessor.JobName); attempt++)
        {
            await Task.Delay(50);
        }
    }
}
