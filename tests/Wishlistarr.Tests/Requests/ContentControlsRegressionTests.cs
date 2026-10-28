using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;

namespace Wishlistarr.Tests.Requests;

public sealed class ContentControlsRegressionTests
{
    [Fact]
    public void Content_reason_numbers_preserve_the_existing_contract()
    {
        Assert.Equal(0, (int)Wishlistarr.Core.Content.ContentReasonCode.ExplicitlyBlocked);
        Assert.Equal(13, (int)Wishlistarr.Core.Content.ContentReasonCode.LanguageNotAllowed);
        Assert.Equal(14, (int)Wishlistarr.Core.Content.ContentReasonCode.PreviouslyRevoked);
        Assert.Equal(15, (int)Wishlistarr.Core.Content.ContentReasonCode.MetadataUnavailable);
        Assert.Equal(16, (int)Wishlistarr.Core.Content.ContentReasonCode.BlockedDescriptor);
        Assert.Equal(17, (int)Wishlistarr.Core.Content.ContentReasonCode.NoBlockedDescriptor);
        Assert.Equal(18, (int)Wishlistarr.Core.Content.ContentReasonCode.BlockedTopic);
        Assert.Equal(19, (int)Wishlistarr.Core.Content.ContentReasonCode.NoBlockedTopic);
    }

    private static void Catalog(KidRequestApiFactory factory)
    {
        const string page = """{"page":1,"total_pages":1,"total_results":1,"results":[{"id":603,"media_type":"movie","title":"The Matrix","genre_ids":[28],"original_language":"en"}]}""";
        factory.Tmdb.SetCatalog("/3/search/multi", page);
        factory.Tmdb.SetCatalog("/3/discover/movie", page);
        factory.Tmdb.SetBody("/3/discover/tv", """{"page":1,"total_pages":1,"total_results":0,"results":[]}""");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Adding_and_removing_entries_immediately_changes_all_kid_surfaces(bool blocked)
    {
        using KidRequestApiFactory factory = new();
        Catalog(factory);
        (HttpClient kid, int id) = await factory.SignInKidAsync(movieAgeLimit: blocked ? null : 5, homeRows: ["popular"]);
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        string path = $"/api/v1/kid-profiles/{id}";
        await Surfaces(kid, visible: blocked, home: blocked);
        var title = new { kind = "movie", tmdbId = 603, title = "The Matrix", blocked };
        Assert.Equal(HttpStatusCode.NoContent, (await parent.PostAsJsonAsync(path + "/titles", title)).StatusCode);
        await Surfaces(kid, visible: !blocked, home: !blocked);
        if (!blocked)
            Assert.DoesNotContain("certification.lte", factory.Tmdb.Requests.Last(row => row.Path == "/3/discover/movie").Query.Keys);
        Assert.Equal(HttpStatusCode.NoContent, (await parent.DeleteAsync(path + $"/titles/movie/603?blocked={blocked}")).StatusCode);
        await Surfaces(kid, visible: blocked, home: blocked);
    }

    [Theory]
    [InlineData("ordinary", "allowed")]
    [InlineData("age", "blocked")]
    [InlineData("block", "blocked")]
    [InlineData("allow-keyword-failure", "allowed")]
    [InlineData("allow-certification-failure", "blocked")]
    [InlineData("allow-details-failure", "blocked")]
    [InlineData("allow-adult", "blocked")]
    [InlineData("keyword-failure", "blocked")]
    [InlineData("revoked", "needsApproval")]
    [InlineData("revoked-block", "blocked")]
    public async Task Preview_request_search_details_and_home_share_decisions(string scenario, string outcome)
    {
        using KidRequestApiFactory factory = new();
        Catalog(factory);
        bool revoked = scenario.StartsWith("revoked", StringComparison.Ordinal);
        (HttpClient kid, int id) = await factory.SignInKidAsync(movieAgeLimit: scenario == "ordinary" ? null : 10, blockedKeywordIds: [1721], homeRows: ["popular"]);
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        string path = $"/api/v1/kid-profiles/{id}";
        if (revoked)
        {
            using IServiceScope scope = factory.Services.CreateScope();
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            db.MediaRequests.Add(new MediaRequest { KidProfileId = id, Kind = MediaKind.Movie, TmdbId = 603, State = RequestState.Revoked });
            await db.SaveChangesAsync();
        }
        bool listed = scenario.StartsWith("allow", StringComparison.Ordinal) || scenario is "block" or "revoked-block";
        if (listed)
            Assert.Equal(HttpStatusCode.NoContent, (await parent.PostAsJsonAsync(path + "/titles", new { kind = "movie", tmdbId = 603, title = "The Matrix", blocked = !scenario.StartsWith("allow", StringComparison.Ordinal) })).StatusCode);
        if (scenario == "allow-adult")
            factory.Tmdb.SetBody("/3/movie/603", """{"id":603,"title":"The Matrix","adult":true}""");
        string? failing = scenario.Contains("keyword-failure", StringComparison.Ordinal) ? "/3/movie/603/keywords"
            : scenario == "allow-certification-failure" ? "/3/movie/603/release_dates"
            : scenario == "allow-details-failure" ? "/3/movie/603" : null;
        if (failing is not null)
            for (int i = 0; i < 12; i++) factory.Tmdb.Enqueue(failing, 500);
        else factory.Tmdb.SetBody("/3/movie/603/keywords", """{"keywords":[]}""");
        JsonElement preview = await parent.PostAsJsonAsync(path + "/preview", new { kind = "movie", tmdbId = 603 }).ResultBody();
        Assert.Equal(outcome, preview.GetProperty("decision").GetString());
        await Surfaces(kid, outcome != "blocked", outcome == "allowed", outcome == "needsApproval");
        Assert.Equal(outcome == "blocked" ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.Created,
            (await KidRequestApiFactory.RequestAsync(kid, "movie", 603)).StatusCode);
        if (scenario == "allow-keyword-failure") Assert.Equal(0, factory.Tmdb.Count("/3/movie/603/keywords"));
    }

    private static async Task Surfaces(HttpClient kid, bool visible, bool home, bool masked = false)
    {
        JsonElement search = await kid.GetFromJsonAsync<JsonElement>("/api/v1/kid/search?q=matrix");
        JsonElement[] results = [.. search.GetProperty("results").EnumerateArray()];
        Assert.Equal(visible, results.Any(row => row.GetProperty("tmdbId").GetInt32() == 603));
        HttpResponseMessage details = await kid.GetAsync("/api/v1/kid/titles/movie/603");
        Assert.Equal(visible ? HttpStatusCode.OK : HttpStatusCode.NotFound, details.StatusCode);
        if (visible)
        {
            Assert.Equal(masked, Assert.Single(results).GetProperty("needsParent").GetBoolean());
            JsonElement detail = await details.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(masked, detail.GetProperty("needsParent").GetBoolean());
        }
        JsonElement dashboard = await kid.GetFromJsonAsync<JsonElement>("/api/v1/kid/home");
        Assert.Equal(home, dashboard.GetProperty("rows").EnumerateArray().SelectMany(row => row.GetProperty("items").EnumerateArray()).Any(row => row.GetProperty("tmdbId").GetInt32() == 603));
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("a", 1)]
    [InlineData("matrix", 0)]
    [InlineData("matrix", 11)]
    public async Task Parent_search_rejects_query_and_page_bounds(string query, int page)
    {
        using KidRequestApiFactory factory = new();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await parent.GetAsync($"/api/v1/kid-profiles/title-search?q={query}&page={page}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await parent.GetAsync("/api/v1/kid-profiles/title-search?q=" + new string('a', 101))).StatusCode);
    }

    [Fact]
    public async Task Parent_search_maps_both_kinds_filters_people_deduplicates_and_reports_failure()
    {
        using KidRequestApiFactory factory = new();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        factory.Tmdb.SetBody("/3/search/multi", """{"page":1,"total_pages":1,"total_results":4,"results":[{"id":1,"media_type":"movie","title":"Movie title","name":"Wrong"},{"id":1,"media_type":"movie","title":"Duplicate"},{"id":1,"media_type":"tv","name":"Show name","title":"Wrong"},{"id":3,"media_type":"person","name":"Person"}]}""");
        JsonElement[] rows = (await parent.GetFromJsonAsync<JsonElement[]>("/api/v1/kid-profiles/title-search?q=matrix&page=10"))!;
        Assert.Equal(["Movie title", "Show name"], rows.Select(row => row.GetProperty("title").GetString()));
        Assert.Equal(["movie", "show"], rows.Select(row => row.GetProperty("kind").GetString()));
        factory.Tmdb.Enqueue("/3/search/multi", 500);
        Assert.Equal(HttpStatusCode.BadGateway, (await parent.GetAsync("/api/v1/kid-profiles/title-search?q=other")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_caps_body_before_parsing_even_without_content_length(bool unknownLength)
    {
        using KidRequestApiFactory factory = new();
        (_, int id) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        string original = await parent.GetStringAsync($"/api/v1/kid-profiles/{id}/content-rules");
        byte[] bytes = Encoding.UTF8.GetBytes(new string(' ', 1048577) + "{");
        using HttpContent content = unknownLength ? new ChunkedContent(bytes) : new ByteArrayContent(bytes);
        content.Headers.ContentType = new("application/json");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await parent.PutAsync($"/api/v1/kid-profiles/{id}/content-rules", content)).StatusCode);
        Assert.Equal(original, await parent.GetStringAsync($"/api/v1/kid-profiles/{id}/content-rules"));
    }

    [Fact]
    public async Task Rules_require_explicit_loading_and_kind_is_stored_as_text()
    {
        using KidRequestApiFactory factory = new();
        (_, int id) = await factory.SignInKidAsync();
        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        db.ProfileTitleRules.Add(new ProfileTitleRule { KidProfileId = id, Kind = MediaKind.Movie, TmdbId = 603, Blocked = true });
        await db.SaveChangesAsync();
        KidProfile profile = await db.KidProfiles.AsNoTracking().SingleAsync(row => row.Id == id);
        Assert.Empty(profile.TitleRules);
        Assert.Throws<InvalidOperationException>(() => profile.ContentPolicy());
        KidProfile? included = await db.KidProfiles.AsNoTracking().FirstWithContentRulesAsync(row => row.Id == id, CancellationToken.None);
        Assert.NotNull(included);
        Assert.Single(included.TitleRules);
        Assert.Single(included.ContentPolicy().Blocked);
        Assert.Equal(typeof(string), db.Model.FindEntityType(typeof(ProfileTitleRule))!.FindProperty(nameof(ProfileTitleRule.Kind))!.GetTypeMapping().Converter!.ProviderClrType);
    }
    private sealed class ChunkedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

}
