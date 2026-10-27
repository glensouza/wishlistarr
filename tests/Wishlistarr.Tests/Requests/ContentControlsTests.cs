using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Tmdb;
using Wishlistarr.Data;

namespace Wishlistarr.Tests.Requests;

public sealed class ContentControlsTests
{
    [Fact]
    public async Task Purging_title_names_preserves_the_policy_and_export_remains_importable()
    {
        using KidRequestApiFactory factory = new();
        (HttpClient kid, int id) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        string path = $"/api/v1/kid-profiles/{id}";
        Assert.Equal(HttpStatusCode.NoContent, (await parent.PostAsJsonAsync(path + "/titles", new { kind = "movie", tmdbId = 603, title = "The Matrix", blocked = true })).StatusCode);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            ITmdbDataStore store = Assert.Single(scope.ServiceProvider.GetServices<ITmdbDataStore>(), store => store.EntityType == typeof(ProfileTitleRule));
            Assert.Equal(1, await store.PurgeAllAsync(CancellationToken.None));
        }

        JsonElement exported = await parent.GetFromJsonAsync<JsonElement>(path + "/content-rules");
        Assert.Equal("Movie 603", exported.GetProperty("titles")[0].GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.OK, (await parent.PutAsJsonAsync(path + "/content-rules", exported)).StatusCode);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            ProfileTitleRule rule = await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().ProfileTitleRules.SingleAsync();
            Assert.Null(rule.Title);
            Assert.Null(rule.TitleFetchedAt);
        }
        JsonElement filtered = await parent.GetFromJsonAsync<JsonElement>(path + "/titles?q=Movie%20603");
        Assert.Equal(1, filtered.GetProperty("total").GetInt32());
        filtered = await parent.GetFromJsonAsync<JsonElement>(path + "/titles?q=603");
        Assert.Equal(1, filtered.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await KidRequestApiFactory.RequestAsync(kid, "movie", 603)).StatusCode);
    }

    [Fact]
    public async Task Stored_blocks_apply_immediately_to_preview_and_requests_and_parent_routes_reject_kids()
    {
        using KidRequestApiFactory factory = new();
        (HttpClient kid, int id) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        string path = $"/api/v1/kid-profiles/{id}";
        var title = new { kind = "movie", tmdbId = 603, title = "The Matrix", blocked = true };
        Assert.Equal(HttpStatusCode.NoContent, (await parent.PostAsJsonAsync(path + "/titles", title)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await parent.PostAsJsonAsync(path + "/titles", title)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await kid.GetAsync(path + "/titles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await kid.PostAsJsonAsync(path + "/preview", title)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await kid.PostAsJsonAsync(path + "/titles", title)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await kid.DeleteAsync(path + "/titles/movie/603?blocked=true")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await kid.GetAsync(path + "/content-rules")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await kid.PutAsJsonAsync(path + "/content-rules", new { })).StatusCode);
        JsonElement preview = await parent.PostAsJsonAsync(path + "/preview", title).ResultBody();
        Assert.Equal("blocked", preview.GetProperty("decision").GetString());
        Assert.Contains("blocked list", preview.GetProperty("reason").GetString());
        Assert.Empty(await factory.GetRequestsAsync());
        Assert.Equal(0, factory.LabelWrites);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await KidRequestApiFactory.RequestAsync(kid, "movie", 603)).StatusCode);
        Assert.Empty(await factory.GetRequestsAsync());
        HttpResponseMessage removed = await parent.DeleteAsync(path + "/titles/movie/603?blocked=true");
        Assert.True(removed.StatusCode == HttpStatusCode.NoContent, await removed.Content.ReadAsStringAsync());
        preview = await parent.PostAsJsonAsync(path + "/preview", title).ResultBody();
        Assert.Equal("allowed", preview.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Always_allowed_overrides_age_but_explicit_block_wins_and_search_is_paged_on_server()
    {
        using KidRequestApiFactory factory = new();
        (HttpClient kid, int id) = await factory.SignInKidAsync(movieAgeLimit: 5);
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        string path = $"/api/v1/kid-profiles/{id}";
        var allowed = new { kind = "movie", tmdbId = 603, title = "The Matrix", blocked = false };
        Assert.Equal(HttpStatusCode.NoContent, (await parent.PostAsJsonAsync(path + "/titles", allowed)).StatusCode);
        JsonElement preview = await parent.PostAsJsonAsync(path + "/preview", allowed).ResultBody();
        Assert.Equal("allowed", preview.GetProperty("decision").GetString());
        Assert.Equal("explicitlyAllowed", preview.GetProperty("reasonCode").GetString());
        Assert.Equal(HttpStatusCode.Created, (await KidRequestApiFactory.RequestAsync(kid, "movie", 603)).StatusCode);
        foreach (var title in new[] { new { kind = "movie", tmdbId = 603, title = "The Matrix", blocked = true }, new { kind = "show", tmdbId = 82728, title = "Bluey", blocked = true } })
            Assert.Equal(HttpStatusCode.NoContent, (await parent.PostAsJsonAsync(path + "/titles", title)).StatusCode);
        preview = await parent.PostAsJsonAsync(path + "/preview", allowed).ResultBody();
        Assert.Equal("blocked", preview.GetProperty("decision").GetString());
        JsonElement page = await parent.GetFromJsonAsync<JsonElement>(path + "/titles?blocked=true&q=MATRIX&limit=1&offset=0");
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        Assert.Equal(603, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("tmdbId").GetInt32());
        page = await parent.GetFromJsonAsync<JsonElement>(path + "/titles?blocked=true&q=matrix&limit=1&offset=1");
        Assert.Empty(page.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Import_is_atomic_rejects_partial_invalid_and_duplicate_files_and_round_trips_titles()
    {
        using KidRequestApiFactory factory = new();
        (_, int id) = await factory.SignInKidAsync(movieAgeLimit: 5);
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        string path = $"/api/v1/kid-profiles/{id}/content-rules";
        string original = await parent.GetStringAsync(path);
        JsonObject file = JsonNode.Parse(original)!.AsObject();
        file["rules"]!["movieAgeLimit"] = 18;
        file["titles"] = JsonSerializer.SerializeToNode(new[] { new { kind = "movie", tmdbId = 603, title = "The Matrix", blocked = true }, new { kind = "show", tmdbId = 82728, title = "Bluey", blocked = false } });
        JsonObject partial = file.DeepClone().AsObject();
        partial["rules"]!.AsObject().Remove("autoApprove");
        Assert.Equal(HttpStatusCode.BadRequest, (await parent.PutAsJsonAsync(path, partial)).StatusCode);
        Assert.Equal(original, await parent.GetStringAsync(path));
        JsonObject invalid = file.DeepClone().AsObject();
        invalid["titles"]![0]!["tmdbId"] = -1;
        Assert.Equal(HttpStatusCode.BadRequest, (await parent.PutAsJsonAsync(path, invalid)).StatusCode);
        Assert.Equal(original, await parent.GetStringAsync(path));
        invalid = file.DeepClone().AsObject();
        invalid["titles"]!.AsArray().Add(invalid["titles"]![0]!.DeepClone());
        Assert.Equal(HttpStatusCode.BadRequest, (await parent.PutAsJsonAsync(path, invalid)).StatusCode);
        Assert.Equal(original, await parent.GetStringAsync(path));
        Assert.Equal(HttpStatusCode.OK, (await parent.PutAsJsonAsync(path, file)).StatusCode);
        JsonNode exported = JsonNode.Parse(await parent.GetStringAsync(path))!;
        Assert.True(JsonNode.DeepEquals(file, exported));
        Assert.Equal(HttpStatusCode.OK, (await parent.PutAsJsonAsync(path, exported)).StatusCode);
    }
}

internal static class ContentControlsResponses
{
    internal static async Task<JsonElement> ResultBody(this Task<HttpResponseMessage> response)
    {
        HttpResponseMessage result = await response;
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        return await result.Content.ReadFromJsonAsync<JsonElement>();
    }
}
