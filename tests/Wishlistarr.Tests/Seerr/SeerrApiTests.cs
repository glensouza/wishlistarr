using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Wishlistarr.Core.Fulfilment;
using Wishlistarr.Integrations.Seerr;

namespace Wishlistarr.Tests.Seerr;

public sealed class SeerrApiTests
{
    [Fact]
    public async Task Test_button_uses_the_saved_connection_and_ignores_an_address_or_key_in_the_body()
    {
        using ApiFactory factory = new();
        await using FakeSeerrServer fake = await FakeSeerrServer.StartAsync();
        HttpClient client = await ConfigureAsync(factory, fake);
        using HttpRequestMessage message = new(HttpMethod.Post, "/api/v1/settings/seerr/test")
        {
            Content = JsonContent.Create(new { url = "http://127.0.0.1:9/", apiKey = "not-the-saved-key" }),
        };

        HttpResponseMessage response = await client.SendAsync(message);
        string body = await response.Content.ReadAsStringAsync();
        JsonElement result = JsonDocument.Parse(body).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(FakeSeerrServer.ReportedVersion, result.GetProperty("version").GetString());
        Assert.Empty(result.GetProperty("warnings").EnumerateArray());
        Assert.DoesNotContain(FakeSeerrServer.ApiKey, body, StringComparison.Ordinal);
        Assert.DoesNotContain("not-the-saved-key", body, StringComparison.Ordinal);
        Assert.All(fake.Requests, request => Assert.Equal(FakeSeerrServer.ApiKey, request.ApiKey));
    }

    [Theory]
    [InlineData(FakeSeerrServer.Admin, "")]
    [InlineData(FakeSeerrServer.ManageRequests | FakeSeerrServer.Request, "")]
    [InlineData(FakeSeerrServer.Request | FakeSeerrServer.AutoApprove, "")]
    [InlineData(FakeSeerrServer.Request, "movie-waits show-waits")]
    [InlineData(FakeSeerrServer.Request | FakeSeerrServer.AutoApproveMovie, "show-waits")]
    [InlineData(FakeSeerrServer.Request | FakeSeerrServer.AutoApproveTv, "movie-waits")]
    [InlineData(FakeSeerrServer.RequestMovie | FakeSeerrServer.AutoApproveMovie, "show-refused")]
    [InlineData(FakeSeerrServer.RequestTv, "movie-refused show-waits")]
    [InlineData(0, "movie-refused show-refused")]
    public async Task Test_warns_per_kind_when_the_user_cannot_request_or_lacks_auto_approve(long permissions, string expected)
    {
        using ApiFactory factory = new();
        await using FakeSeerrServer fake = await FakeSeerrServer.StartAsync();
        fake.Permissions = permissions;
        HttpClient client = await ConfigureAsync(factory, fake);

        HttpResponseMessage response = await client.PostAsync("/api/v1/settings/seerr/test", null);
        JsonElement result = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(FakeSeerrServer.ReportedVersion, result.GetProperty("version").GetString());
        string[] warnings = [.. expected.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(code => code switch
        {
            "movie-waits" => SeerrBackend.MovieAutoApproveWarning,
            "show-waits" => SeerrBackend.ShowAutoApproveWarning,
            "movie-refused" => SeerrBackend.MovieRequestWarning,
            _ => SeerrBackend.ShowRequestWarning,
        })];
        Assert.Equal(warnings, result.GetProperty("warnings").EnumerateArray().Select(item => item.GetString()));
    }

    [Theory]
    [InlineData("movie", FakeSeerrServer.MovieTmdbId, FakeSeerrServer.Request, "pending")]
    [InlineData("movie", FakeSeerrServer.MovieTmdbId, FakeSeerrServer.Request | FakeSeerrServer.AutoApproveMovie, "approved")]
    [InlineData("movie", FakeSeerrServer.MovieTmdbId, FakeSeerrServer.Request | FakeSeerrServer.AutoApproveTv, "pending")]
    [InlineData("show", FakeSeerrServer.ShowTmdbId, FakeSeerrServer.Request | FakeSeerrServer.AutoApproveTv, "approved")]
    [InlineData("show", FakeSeerrServer.ShowTmdbId, FakeSeerrServer.Request | FakeSeerrServer.AutoApproveMovie, "pending")]
    [InlineData("show", FakeSeerrServer.ShowTmdbId, FakeSeerrServer.ManageRequests | FakeSeerrServer.Request, "approved")]
    public async Task A_new_request_follows_the_configured_users_auto_approve(string kind, int tmdbId, long permissions, string expected)
    {
        using ApiFactory factory = new();
        await using FakeSeerrServer fake = await FakeSeerrServer.StartAsync();
        fake.Permissions = permissions;
        HttpClient client = await ConfigureAsync(factory, fake);

        HttpResponseMessage response = await client.PostAsync("/api/v1/seerr/requests", RequestContent(kind, tmdbId));
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("attached").GetBoolean());
    }

    [Fact]
    public async Task An_available_title_without_a_request_says_so_explicitly()
    {
        using ApiFactory factory = new();
        await using FakeSeerrServer fake = await FakeSeerrServer.StartAsync();
        fake.SetMediaStatus("movie", FakeSeerrServer.MovieTmdbId, FakeSeerrServer.MediaAvailable);
        HttpClient client = await ConfigureAsync(factory, fake);

        HttpResponseMessage response = await client.PostAsync("/api/v1/seerr/requests", RequestContent("movie", FakeSeerrServer.MovieTmdbId));
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("alreadyAvailable", body.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("id").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("status").ValueKind);
        Assert.Equal("available", body.GetProperty("availability").GetString());
        Assert.False(body.GetProperty("attached").GetBoolean());
        Assert.Empty(fake.Sent("POST", "/api/v1/request"));
    }

    [Fact]
    public async Task An_unknown_season_is_a_bad_request()
    {
        using ApiFactory factory = new();
        await using FakeSeerrServer fake = await FakeSeerrServer.StartAsync();
        HttpClient client = await ConfigureAsync(factory, fake);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/seerr/requests",
            new { kind = "show", tmdbId = FakeSeerrServer.ShowTmdbId, seasons = new[] { 12 } });
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("That show has no season 12.", problem.GetProperty("title").GetString());
        Assert.Empty(fake.Sent("POST", "/api/v1/request"));
    }

    [Fact]
    public async Task A_user_who_cannot_request_gets_seerrs_reason()
    {
        using ApiFactory factory = new();
        await using FakeSeerrServer fake = await FakeSeerrServer.StartAsync();
        fake.Permissions = FakeSeerrServer.RequestTv;
        HttpClient client = await ConfigureAsync(factory, fake);

        HttpResponseMessage response = await client.PostAsync("/api/v1/seerr/requests", RequestContent("movie", FakeSeerrServer.MovieTmdbId));
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("Seerr refused the request: You do not have permission to make movie requests.", problem.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("movie", "movie", FakeSeerrServer.MovieTmdbId)]
    [InlineData("show", "tv", FakeSeerrServer.ShowTmdbId)]
    public async Task Submitting_an_existing_title_attaches_that_request_and_does_not_post(string kind, string mediaType, int tmdbId)
    {
        using ApiFactory factory = new();
        await using FakeSeerrServer fake = await FakeSeerrServer.StartAsync();
        int existing = fake.SeedRequest(mediaType, tmdbId, status: 2, seasons: kind == "show" ? [1, 2] : null);
        HttpClient client = await ConfigureAsync(factory, fake);

        HttpResponseMessage response = await client.PostAsync("/api/v1/seerr/requests", RequestContent(kind, tmdbId));
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(existing, body.GetProperty("id").GetInt32());
        Assert.Equal("approved", body.GetProperty("status").GetString());
        Assert.Equal("pending", body.GetProperty("availability").GetString());
        Assert.Equal("attached", body.GetProperty("outcome").GetString());
        Assert.True(body.GetProperty("attached").GetBoolean());
        Assert.Empty(fake.Sent("POST", "/api/v1/request"));
    }

    [Theory]
    [InlineData("movie", "movie", FakeSeerrServer.MovieTmdbId)]
    [InlineData("show", "tv", FakeSeerrServer.ShowTmdbId)]
    public async Task Submitting_a_new_title_posts_once_and_returns_the_created_id(string kind, string mediaType, int tmdbId)
    {
        using ApiFactory factory = new();
        await using FakeSeerrServer fake = await FakeSeerrServer.StartAsync();
        HttpClient client = await ConfigureAsync(factory, fake);

        HttpResponseMessage response = await client.PostAsync("/api/v1/seerr/requests", RequestContent(kind, tmdbId));
        JsonElement created = await response.Content.ReadFromJsonAsync<JsonElement>();
        HttpResponseMessage again = await client.PostAsync("/api/v1/seerr/requests", RequestContent(kind, tmdbId));
        JsonElement attached = await again.Content.ReadFromJsonAsync<JsonElement>();
        FakeSeerrServer.RecordedRequest post = Assert.Single(fake.Sent("POST", "/api/v1/request"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(created.GetProperty("id").GetInt32() > 0);
        Assert.Equal("approved", created.GetProperty("status").GetString());
        Assert.Equal("created", created.GetProperty("outcome").GetString());
        Assert.False(created.GetProperty("attached").GetBoolean());
        Assert.Equal(created.GetProperty("id").GetInt32(), attached.GetProperty("id").GetInt32());
        Assert.True(attached.GetProperty("attached").GetBoolean());
        Assert.Equal(mediaType, post.Body!["mediaType"]!.GetValue<string>());
        Assert.Equal(tmdbId, post.Body["mediaId"]!.GetValue<int>());
        Assert.Equal(FakeSeerrServer.DefaultUserId.ToString(System.Globalization.CultureInfo.InvariantCulture), post.ApiUser);
        Assert.Null(post.Body["userId"]);
        Assert.All(fake.Requests.Where(request => request.Method == "GET"), request => Assert.Null(request.ApiUser));
        if (kind == "show")
        {
            Assert.Equal([1, 2], post.Body["seasons"]!.AsArray().Select(item => item!.GetValue<int>()));
        }
        else
        {
            Assert.Null(post.Body["seasons"]);
        }
    }

    [Fact]
    public async Task Reading_a_request_returns_the_status_and_a_later_change()
    {
        using ApiFactory factory = new();
        await using FakeSeerrServer fake = await FakeSeerrServer.StartAsync();
        fake.Permissions = FakeSeerrServer.Request;
        HttpClient client = await ConfigureAsync(factory, fake);
        JsonElement created = await (await client.PostAsync("/api/v1/seerr/requests", RequestContent("movie", FakeSeerrServer.MovieTmdbId)))
            .Content.ReadFromJsonAsync<JsonElement>();
        int id = created.GetProperty("id").GetInt32();

        JsonElement pending = await GetRequestAsync(client, id);
        fake.SetStatus(id, FakeSeerrServer.RequestApproved);
        fake.SetMediaStatus("movie", FakeSeerrServer.MovieTmdbId, FakeSeerrServer.MediaProcessing);
        JsonElement approved = await GetRequestAsync(client, id);
        fake.SetStatus(id, FakeSeerrServer.RequestFailed);
        JsonElement failed = await GetRequestAsync(client, id);
        fake.SetStatus(id, FakeSeerrServer.RequestCompleted);
        fake.SetMediaStatus("movie", FakeSeerrServer.MovieTmdbId, FakeSeerrServer.MediaAvailable);
        JsonElement completed = await GetRequestAsync(client, id);
        fake.SetStatus(id, FakeSeerrServer.RequestDeclined);
        fake.SetMediaStatus("movie", FakeSeerrServer.MovieTmdbId, FakeSeerrServer.MediaUnknown);
        JsonElement declined = await GetRequestAsync(client, id);

        Assert.Equal(("pending", "pending"), Read(pending));
        Assert.Equal(("approved", "processing"), Read(approved));
        Assert.Equal(("failed", "processing"), Read(failed));
        Assert.Equal(("completed", "available"), Read(completed));
        Assert.Equal(("declined", "unknown"), Read(declined));
        Assert.Equal(id, approved.GetProperty("id").GetInt32());

        static (string?, string?) Read(JsonElement body) =>
            (body.GetProperty("status").GetString(), body.GetProperty("availability").GetString());
    }

    [Theory]
    [InlineData(FakeSeerrServer.MediaPartiallyAvailable, "partiallyAvailable")]
    [InlineData(FakeSeerrServer.MediaBlocklisted, "blocklisted")]
    [InlineData(FakeSeerrServer.MediaDeleted, "deleted")]
    [InlineData(42, "unknown")]
    public async Task Tracking_reports_availability_by_name(int status, string expected)
    {
        using ApiFactory factory = new();
        await using FakeSeerrServer fake = await FakeSeerrServer.StartAsync();
        int id = fake.SeedRequest("tv", FakeSeerrServer.ShowTmdbId, seasons: [1]);
        fake.SetMediaStatus("tv", FakeSeerrServer.ShowTmdbId, status);
        HttpClient client = await ConfigureAsync(factory, fake);

        JsonElement body = await GetRequestAsync(client, id);

        Assert.Equal(expected, body.GetProperty("availability").GetString());
    }

    [Fact]
    public async Task Tracking_can_read_a_request_made_by_another_seerr_user()
    {
        using ApiFactory factory = new();
        await using FakeSeerrServer fake = await FakeSeerrServer.StartAsync();
        fake.Permissions = FakeSeerrServer.Request;
        int id = fake.SeedRequest("movie", FakeSeerrServer.MovieTmdbId, requestedBy: 9);
        HttpClient client = await ConfigureAsync(factory, fake);

        JsonElement body = await GetRequestAsync(client, id);

        Assert.Equal("approved", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_unrecognised_status_number_is_reported_as_unknown()
    {
        using ApiFactory factory = new();
        await using FakeSeerrServer fake = await FakeSeerrServer.StartAsync();
        int id = fake.SeedRequest("movie", FakeSeerrServer.MovieTmdbId, status: 9);
        HttpClient client = await ConfigureAsync(factory, fake);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/seerr/requests/{id}");
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("unknown", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_url_base_is_kept_for_status_user_request_and_tracking_calls()
    {
        using ApiFactory factory = new();
        await using FakeSeerrServer fake = await FakeSeerrServer.StartAsync();
        fake.PathBase = "/seerr";
        HttpClient client = await ConfigureAsync(factory, fake, new Uri(fake.Address, "seerr").ToString());

        HttpResponseMessage test = await client.PostAsync("/api/v1/settings/seerr/test", null);
        HttpResponseMessage submit = await client.PostAsync("/api/v1/seerr/requests", RequestContent("show", FakeSeerrServer.ShowTmdbId));
        int id = (await submit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        HttpResponseMessage read = await client.GetAsync($"/api/v1/seerr/requests/{id}");

        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Single(fake.Sent("GET", "/api/v1/status"));
        Assert.Single(fake.Sent("GET", "/api/v1/user/" + FakeSeerrServer.DefaultUserId));
        Assert.Single(fake.Sent("GET", "/api/v1/tv/" + FakeSeerrServer.ShowTmdbId));
        Assert.Single(fake.Sent("POST", "/api/v1/request"));
        Assert.Single(fake.Sent("GET", "/api/v1/request/" + id));
        Assert.All(fake.Requests, request => Assert.StartsWith("/seerr/api/v1/", request.Path, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ftp://seerr.local/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://user:pass@seerr.local/")]
    [InlineData("http://seerr.local/?apikey=1")]
    [InlineData("http://seerr.local/#frag")]
    [InlineData("seerr.local")]
    [InlineData("")]
    public async Task Invalid_addresses_are_rejected(string url)
    {
        using ApiFactory factory = new();
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        HttpResponseMessage response = await client.PutAsJsonAsync("/api/v1/settings/seerr", new { url, apiKey = "key", userId = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False((await GetSettingsAsync(client)).GetProperty("hasApiKey").GetBoolean());
    }

    [Fact]
    public async Task An_overlong_address_is_rejected()
    {
        using ApiFactory factory = new();
        HttpClient client = await factory.CreateAuthorizedClientAsync();
        string url = "http://seerr.local/" + new string('a', 500);

        HttpResponseMessage response = await client.PutAsJsonAsync("/api/v1/settings/seerr", new { url, apiKey = "key", userId = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_non_positive_user_id_is_rejected(int userId)
    {
        using ApiFactory factory = new();
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        HttpResponseMessage response = await client.PutAsJsonAsync(
            "/api/v1/settings/seerr",
            new { url = "http://seerr.local:5055", apiKey = "key", userId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False((await GetSettingsAsync(client)).GetProperty("hasApiKey").GetBoolean());
    }

    [Fact]
    public async Task An_api_key_is_required_first_and_when_the_address_changes()
    {
        using ApiFactory factory = new();
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        HttpResponseMessage first = await client.PutAsJsonAsync("/api/v1/settings/seerr", new { url = "http://seerr.local:5055", userId = 1 });
        await client.PutAsJsonAsync("/api/v1/settings/seerr", new { url = "http://seerr.local:5055", apiKey = "key", userId = 1 });
        HttpResponseMessage sameAddress = await client.PutAsJsonAsync("/api/v1/settings/seerr", new { url = "http://seerr.local:5055/", userId = 4 });
        HttpResponseMessage otherAddress = await client.PutAsJsonAsync("/api/v1/settings/seerr", new { url = "http://elsewhere.local:5055", userId = 4 });
        JsonElement view = await GetSettingsAsync(client);

        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, sameAddress.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, otherAddress.StatusCode);
        Assert.True(view.GetProperty("hasApiKey").GetBoolean());
        Assert.Equal(4, view.GetProperty("userId").GetInt32());
        Assert.Equal("http://seerr.local:5055/", view.GetProperty("url").GetString());
    }

    [Fact]
    public async Task An_api_key_with_a_line_break_is_rejected()
    {
        using ApiFactory factory = new();
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        HttpResponseMessage response = await client.PutAsJsonAsync(
            "/api/v1/settings/seerr",
            new { url = "http://seerr.local:5055", apiKey = "abc\nkey", userId = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False((await GetSettingsAsync(client)).GetProperty("hasApiKey").GetBoolean());
    }

    [Fact]
    public async Task Settings_get_never_returns_the_api_key()
    {
        using ApiFactory factory = new();
        await using FakeSeerrServer fake = await FakeSeerrServer.StartAsync();
        HttpClient client = await ConfigureAsync(factory, fake);

        string settings = await client.GetStringAsync("/api/v1/settings/seerr");
        string listed = await client.GetStringAsync("/api/v1/settings");
        JsonElement view = JsonDocument.Parse(settings).RootElement;

        Assert.False(view.TryGetProperty("apiKey", out _));
        Assert.True(view.GetProperty("hasApiKey").GetBoolean());
        Assert.Equal(fake.Address.ToString(), view.GetProperty("url").GetString());
        Assert.Equal(FakeSeerrServer.DefaultUserId, view.GetProperty("userId").GetInt32());
        Assert.DoesNotContain(FakeSeerrServer.ApiKey, settings, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSeerrServer.ApiKey, listed, StringComparison.Ordinal);
        JsonElement stored = JsonDocument.Parse(listed).RootElement.EnumerateArray()
            .Single(item => item.GetProperty("key").GetString() == "seerr.api_key");
        Assert.True(stored.GetProperty("isSecret").GetBoolean());
        Assert.Equal(JsonValueKind.Null, stored.GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task Deleting_the_connection_forgets_the_key()
    {
        using ApiFactory factory = new();
        HttpClient client = await factory.CreateAuthorizedClientAsync();
        await client.PutAsJsonAsync("/api/v1/settings/seerr", new { url = "http://seerr.local:5055", apiKey = "key", userId = 1 });

        HttpResponseMessage delete = await client.DeleteAsync("/api/v1/settings/seerr");
        JsonElement view = await GetSettingsAsync(client);

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.False(view.GetProperty("hasApiKey").GetBoolean());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("url").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("userId").ValueKind);
    }

    [Fact]
    public async Task Test_and_requests_are_a_conflict_when_seerr_is_not_configured()
    {
        using ApiFactory factory = new();
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        HttpResponseMessage test = await client.PostAsync("/api/v1/settings/seerr/test", null);
        HttpResponseMessage submit = await client.PostAsync("/api/v1/seerr/requests", RequestContent("movie", FakeSeerrServer.MovieTmdbId));
        HttpResponseMessage read = await client.GetAsync("/api/v1/seerr/requests/1");

        Assert.Equal(HttpStatusCode.Conflict, test.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, submit.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, read.StatusCode);
        Assert.Contains("not configured", (await test.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_user_is_not_a_successful_test()
    {
        using ApiFactory factory = new();
        await using FakeSeerrServer fake = await FakeSeerrServer.StartAsync();
        fake.MissingUser = true;
        HttpClient client = await ConfigureAsync(factory, fake);

        HttpResponseMessage response = await client.PostAsync("/api/v1/settings/seerr/test", null);
        string body = await response.Content.ReadAsStringAsync();
        JsonElement problem = JsonDocument.Parse(body).RootElement;

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("Seerr has no user with that id.", problem.GetProperty("title").GetString());
        Assert.DoesNotContain(FakeSeerrServer.ApiKey, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upstream_errors_do_not_include_the_api_key()
    {
        using ApiFactory factory = new();
        await using FakeSeerrServer fake = await FakeSeerrServer.StartAsync();
        fake.FailWith = 400;
        fake.FailureBody = "{\"message\":\"" + FakeSeerrServer.ApiKey + "\"}";
        HttpClient client = await ConfigureAsync(factory, fake);

        HttpResponseMessage response = await client.PostAsync("/api/v1/settings/seerr/test", null);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("refused", body, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSeerrServer.ApiKey, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connection_settings_cannot_be_written_through_the_generic_settings_endpoint()
    {
        using ApiFactory factory = new();
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        HttpResponseMessage url = await client.PutAsJsonAsync("/api/v1/settings/seerr.url", new { value = "http://169.254.169.254/" });
        HttpResponseMessage key = await client.PutAsJsonAsync("/api/v1/settings/SEERR.api_key", new { value = "x", isSecret = true });

        Assert.Equal(HttpStatusCode.BadRequest, url.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, key.StatusCode);
    }

    private static async Task<HttpClient> ConfigureAsync(ApiFactory factory, FakeSeerrServer fake, string? url = null)
    {
        HttpClient client = await factory.CreateAuthorizedClientAsync();
        HttpResponseMessage save = await client.PutAsJsonAsync("/api/v1/settings/seerr", new
        {
            url = url ?? fake.Address.ToString().TrimEnd('/'),
            apiKey = FakeSeerrServer.ApiKey,
            userId = FakeSeerrServer.DefaultUserId,
        });
        save.EnsureSuccessStatusCode();
        return client;
    }

    private static JsonContent RequestContent(string kind, int tmdbId)
    {
        if (kind == "show")
        {
            int[] seasons = [1, 2];
            return JsonContent.Create(new { kind, tmdbId, seasons });
        }

        return JsonContent.Create(new { kind, tmdbId });
    }

    private static async Task<JsonElement> GetSettingsAsync(HttpClient client) =>
        JsonDocument.Parse(await client.GetStringAsync("/api/v1/settings/seerr")).RootElement;

    private static async Task<JsonElement> GetRequestAsync(HttpClient client, int id)
    {
        HttpResponseMessage response = await client.GetAsync($"/api/v1/seerr/requests/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
