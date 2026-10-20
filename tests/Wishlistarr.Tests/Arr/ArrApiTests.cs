using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Wishlistarr.Core.Fulfilment;

namespace Wishlistarr.Tests.Arr;

public sealed class ArrApiTests
{
    [Theory]
    [InlineData(ArrService.Sonarr, "sonarr")]
    [InlineData(ArrService.Radarr, "radarr")]
    public async Task Parent_saves_tests_and_lists_without_ever_seeing_the_api_key(ArrService service, string route)
    {
        using ApiFactory factory = new();
        await using FakeArrServer fake = await FakeArrServer.StartAsync(service);
        HttpClient client = await factory.CreateAuthorizedClientAsync();
        List<string> bodies = [];

        HttpResponseMessage save = await client.PutAsJsonAsync($"/api/v1/settings/{route}", new
        {
            url = fake.Address.ToString().TrimEnd('/'),
            apiKey = FakeArrServer.ApiKey,
            qualityProfileId = 4,
            rootFolderPath = "/media/kids",
            tagIds = new[] { 1 },
        });
        bodies.Add(await save.Content.ReadAsStringAsync());
        JsonElement view = await GetJsonAsync(client, $"/api/v1/settings/{route}", bodies);
        JsonElement test = await PostJsonAsync(client, $"/api/v1/settings/{route}/test", bodies);
        JsonElement profiles = await GetJsonAsync(client, $"/api/v1/{route}/profiles", bodies);
        JsonElement folders = await GetJsonAsync(client, $"/api/v1/{route}/rootfolders", bodies);
        JsonElement tags = await GetJsonAsync(client, $"/api/v1/{route}/tags", bodies);
        JsonElement settings = await GetJsonAsync(client, "/api/v1/settings", bodies);

        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        Assert.True(view.GetProperty("hasApiKey").GetBoolean());
        Assert.Equal(fake.Address.ToString(), view.GetProperty("url").GetString());
        Assert.Equal(4, view.GetProperty("qualityProfileId").GetInt32());
        Assert.Equal("/media/kids", view.GetProperty("rootFolderPath").GetString());
        Assert.Equal(1, view.GetProperty("tagIds")[0].GetInt32());
        Assert.True(test.GetProperty("valid").GetBoolean());
        Assert.Equal(2, profiles.GetArrayLength());
        Assert.Equal("/media/kids", folders[0].GetProperty("path").GetString());
        Assert.Equal("kids", tags[0].GetProperty("label").GetString());

        JsonElement key = settings.EnumerateArray().Single(item => item.GetProperty("key").GetString() == $"{route}.api_key");
        Assert.True(key.GetProperty("isSecret").GetBoolean());
        Assert.Equal(JsonValueKind.Null, key.GetProperty("value").ValueKind);
        Assert.All(bodies, body => Assert.DoesNotContain(FakeArrServer.ApiKey, body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Test_button_reports_a_clear_message_for_a_wrong_key()
    {
        using ApiFactory factory = new();
        await using FakeArrServer fake = await FakeArrServer.StartAsync(ArrService.Sonarr);
        HttpClient client = await factory.CreateAuthorizedClientAsync();
        await client.PutAsJsonAsync("/api/v1/settings/sonarr", new { url = fake.Address.ToString(), apiKey = "nope" });

        HttpResponseMessage response = await client.PostAsync("/api/v1/settings/sonarr/test", null);
        JsonElement result = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(result.GetProperty("valid").GetBoolean());
        Assert.Equal("Sonarr rejected the API key.", result.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Test_and_lists_explain_when_nothing_is_configured()
    {
        using ApiFactory factory = new();
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        JsonElement test = await (await client.PostAsync("/api/v1/settings/radarr/test", null)).Content.ReadFromJsonAsync<JsonElement>();
        HttpResponseMessage profiles = await client.GetAsync("/api/v1/radarr/profiles");

        Assert.False(test.GetProperty("valid").GetBoolean());
        Assert.Contains("not configured", test.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, profiles.StatusCode);
    }

    [Fact]
    public async Task Lists_report_a_bad_gateway_when_the_backend_fails()
    {
        using ApiFactory factory = new();
        await using FakeArrServer fake = await FakeArrServer.StartAsync(ArrService.Radarr);
        HttpClient client = await factory.CreateAuthorizedClientAsync();
        await client.PutAsJsonAsync("/api/v1/settings/radarr", new { url = fake.Address.ToString(), apiKey = FakeArrServer.ApiKey });
        fake.FailWith = 500;

        HttpResponseMessage response = await client.GetAsync("/api/v1/radarr/tags");
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("Radarr answered 500.", problem.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("ftp://sonarr.local/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://user:pass@sonarr.local/")]
    [InlineData("http://sonarr.local/?apikey=1")]
    [InlineData("sonarr.local")]
    [InlineData("")]
    public async Task Invalid_addresses_are_rejected(string url)
    {
        using ApiFactory factory = new();
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        HttpResponseMessage response = await client.PutAsJsonAsync("/api/v1/settings/sonarr", new { url, apiKey = "key" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False((await GetJsonAsync(client, "/api/v1/settings/sonarr", [])).GetProperty("hasApiKey").GetBoolean());
    }

    [Fact]
    public async Task An_api_key_is_required_first_and_when_the_address_changes()
    {
        using ApiFactory factory = new();
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        HttpResponseMessage first = await client.PutAsJsonAsync("/api/v1/settings/sonarr", new { url = "http://sonarr.local:8989" });
        await client.PutAsJsonAsync("/api/v1/settings/sonarr", new { url = "http://sonarr.local:8989", apiKey = "key" });
        HttpResponseMessage sameAddress = await client.PutAsJsonAsync("/api/v1/settings/sonarr", new { url = "http://sonarr.local:8989/", qualityProfileId = 4 });
        HttpResponseMessage otherAddress = await client.PutAsJsonAsync("/api/v1/settings/sonarr", new { url = "http://elsewhere.local:8989" });

        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, sameAddress.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, otherAddress.StatusCode);
    }

    [Fact]
    public async Task Deleting_the_connection_forgets_the_key()
    {
        using ApiFactory factory = new();
        HttpClient client = await factory.CreateAuthorizedClientAsync();
        await client.PutAsJsonAsync("/api/v1/settings/radarr", new { url = "http://radarr.local:7878", apiKey = "key" });

        HttpResponseMessage delete = await client.DeleteAsync("/api/v1/settings/radarr");
        JsonElement view = await GetJsonAsync(client, "/api/v1/settings/radarr", []);

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.False(view.GetProperty("hasApiKey").GetBoolean());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("url").ValueKind);
    }

    [Fact]
    public async Task Connection_settings_cannot_be_written_through_the_generic_settings_endpoint()
    {
        using ApiFactory factory = new();
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        HttpResponseMessage url = await client.PutAsJsonAsync("/api/v1/settings/sonarr.url", new { value = "http://169.254.169.254/" });
        HttpResponseMessage key = await client.PutAsJsonAsync("/api/v1/settings/RADARR.api_key", new { value = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, url.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, key.StatusCode);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path, List<string> bodies)
    {
        string body = await client.GetStringAsync(path);
        bodies.Add(body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string path, List<string> bodies)
    {
        string body = await (await client.PostAsync(path, null)).Content.ReadAsStringAsync();
        bodies.Add(body);
        return JsonDocument.Parse(body).RootElement;
    }
}
