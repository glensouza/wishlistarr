using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Integrations.Plex;

namespace Wishlistarr.Tests.Plex;

public sealed class PlexApiFactory : ApiFactory
{
    public PlexApiFactory() => this.Fake = FakePlexServer.StartAsync().GetAwaiter().GetResult();

    public FakePlexServer Fake { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.Configure<PlexOptions>(options =>
        {
            options.PlexTvBaseAddress = this.Fake.Address;
            options.ProbeTimeout = TimeSpan.FromSeconds(3);
        }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            this.Fake.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}

public sealed class PlexApiTests
{
    [Fact]
    public async Task Parent_links_plex_picks_a_server_and_reads_it_without_seeing_a_token()
    {
        using PlexApiFactory factory = new();
        HttpClient client = await factory.CreateAuthorizedClientAsync();
        List<string> bodies = [];

        JsonElement before = await GetJsonAsync(client, "/api/v1/plex/status", bodies);
        JsonElement link = await PostJsonAsync(client, "/api/v1/plex/link", bodies);
        string linkId = link.GetProperty("id").GetString()!;
        JsonElement pending = await PostJsonAsync(client, $"/api/v1/plex/link/{linkId}/check", bodies);
        factory.Fake.PinApproved = true;
        JsonElement linked = await PostJsonAsync(client, $"/api/v1/plex/link/{linkId}/check", bodies);
        JsonElement servers = await GetJsonAsync(client, "/api/v1/plex/servers", bodies);
        HttpResponseMessage select = await client.PutAsJsonAsync("/api/v1/plex/server", new { serverId = FakePlexServer.MachineIdentifier });
        bodies.Add(await select.Content.ReadAsStringAsync());
        JsonElement test = await PostJsonAsync(client, "/api/v1/settings/plex/test", bodies);
        JsonElement libraries = await GetJsonAsync(client, "/api/v1/plex/libraries", bodies);
        JsonElement users = await GetJsonAsync(client, "/api/v1/plex/users", bodies);
        JsonElement lookup = await GetJsonAsync(client, "/api/v1/plex/lookup?kind=movie&source=tmdb&id=603", bodies);
        JsonElement after = await GetJsonAsync(client, "/api/v1/plex/status", bodies);
        JsonElement settings = await GetJsonAsync(client, "/api/v1/settings", bodies);

        Assert.False(before.GetProperty("linked").GetBoolean());
        Assert.Equal(before.GetProperty("clientIdentifier").GetString(), after.GetProperty("clientIdentifier").GetString());
        Assert.Equal(FakePlexServer.PinCode, link.GetProperty("code").GetString());
        Assert.StartsWith("https://app.plex.tv/auth#?", link.GetProperty("authUrl").GetString(), StringComparison.Ordinal);
        Assert.Equal("pending", pending.GetProperty("state").GetString());
        Assert.Equal("linked", linked.GetProperty("state").GetString());
        Assert.Equal(2, servers.GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, select.StatusCode);
        Assert.True(test.GetProperty("valid").GetBoolean());
        Assert.Equal(5, libraries.GetArrayLength());
        Assert.Equal("movie", libraries[0].GetProperty("type").GetString());
        Assert.Equal(5, users.GetArrayLength());
        Assert.Equal(["101", "302"], lookup.EnumerateArray().Select(item => item.GetProperty("id").GetString()).Order(StringComparer.Ordinal));
        Assert.True(after.GetProperty("linked").GetBoolean());
        Assert.Equal(FakePlexServer.MachineIdentifier, after.GetProperty("server").GetProperty("id").GetString());

        Dictionary<string, JsonElement> stored = settings.EnumerateArray().ToDictionary(item => item.GetProperty("key").GetString()!);
        foreach (string secret in new[] { "plex.token", "plex.server_token" })
        {
            Assert.True(stored[secret].GetProperty("isSecret").GetBoolean());
            Assert.Equal(JsonValueKind.Null, stored[secret].GetProperty("value").ValueKind);
        }

        Assert.All(bodies, body =>
        {
            Assert.DoesNotContain(FakePlexServer.AccountToken, body, StringComparison.Ordinal);
            Assert.DoesNotContain(FakePlexServer.ServerToken, body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Plex_settings_cannot_be_written_through_the_generic_settings_endpoint()
    {
        using PlexApiFactory factory = new();
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        HttpResponseMessage url = await client.PutAsJsonAsync("/api/v1/settings/plex.server_url", new { value = "http://169.254.169.254/" });
        HttpResponseMessage token = await client.PutAsJsonAsync("/api/v1/settings/PLEX.token", new { value = "x", isSecret = true });

        Assert.Equal(HttpStatusCode.BadRequest, url.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, token.StatusCode);
    }

    [Fact]
    public async Task Endpoints_explain_when_plex_is_not_set_up()
    {
        using PlexApiFactory factory = new();
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        JsonElement test = await (await client.PostAsync("/api/v1/settings/plex/test", null)).Content.ReadFromJsonAsync<JsonElement>();
        HttpResponseMessage servers = await client.GetAsync("/api/v1/plex/servers");
        HttpResponseMessage libraries = await client.GetAsync("/api/v1/plex/libraries");
        HttpResponseMessage unknownLink = await client.PostAsync("/api/v1/plex/link/12345/check", null);
        HttpResponseMessage noServerId = await client.PutAsJsonAsync("/api/v1/plex/server", new { });

        Assert.False(test.GetProperty("valid").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, servers.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, libraries.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownLink.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noServerId.StatusCode);
        Assert.Empty(factory.Fake.Requests);
    }

    [Theory]
    [InlineData("kind=movie&source=tmdb&id=abc")]
    [InlineData("kind=movie&source=imdb&id=0133093")]
    [InlineData("kind=season&source=tmdb&id=603")]
    [InlineData("kind=movie&source=anidb&id=1")]
    [InlineData("kind=1&source=tmdb&id=603")]
    [InlineData("source=tmdb&id=603")]
    public async Task Lookup_rejects_invalid_ids(string query)
    {
        using PlexApiFactory factory = new();
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        HttpResponseMessage response = await client.GetAsync($"/api/v1/plex/lookup?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/v1/plex/status")]
    [InlineData("POST", "/api/v1/plex/link")]
    [InlineData("GET", "/api/v1/plex/libraries")]
    [InlineData("GET", "/api/v1/plex/users")]
    [InlineData("POST", "/api/v1/settings/plex/test")]
    public async Task Plex_endpoints_require_a_parent(string method, string path)
    {
        using PlexApiFactory factory = new();

        HttpResponseMessage response = await factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.Fake.Requests);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path, List<string> bodies)
    {
        HttpResponseMessage response = await client.GetAsync(path);
        return await ReadAsync(response, bodies);
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string path, List<string> bodies)
    {
        HttpResponseMessage response = await client.PostAsync(path, null);
        return await ReadAsync(response, bodies);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, List<string> bodies)
    {
        string body = await response.Content.ReadAsStringAsync();
        bodies.Add(body);
        Assert.True(response.IsSuccessStatusCode, $"{response.RequestMessage?.RequestUri} returned {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
