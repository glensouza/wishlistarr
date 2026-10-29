using System.Net;
using System.Text.Json;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Integrations.Lists;

namespace Wishlistarr.Tests.Lists;

public sealed class ListItemsTests
{
    [Fact]
    public async Task Trakt_extracts_movie_and_show_ids_and_ignores_other_types()
    {
        using HttpClient http = new(new Handler(request =>
        {
            Assert.Equal("api.trakt.tv", request.RequestUri!.Host);
            Assert.Equal("/lists/42/items", request.RequestUri.AbsolutePath);
            Assert.Equal("2", request.Headers.GetValues("trakt-api-version").Single());
            Assert.Equal("test-key", request.Headers.GetValues("trakt-api-key").Single());
            return Json("""
                [{"type":"movie","movie":{"ids":{"tmdb":12}}},
                 {"type":"show","show":{"ids":{"tmdb":34}}},
                 {"type":"movie","movie":{"ids":{"tmdb":null}}},
                 {"type":"person","person":{"ids":{"tmdb":56}}}]
                """);
        }));
        IReadOnlyList<ListItem> items = await ListItems.TraktAsync(http, 42, "test-key");
        Assert.Equal([new ListItem(MediaKind.Movie, 12), new ListItem(MediaKind.Show, 34)], items);
    }

    [Fact]
    public async Task Mdblist_combines_ranked_movie_and_show_buckets()
    {
        using HttpClient http = new(new Handler(request =>
        {
            Assert.Equal("api.mdblist.com", request.RequestUri!.Host);
            Assert.Equal("/lists/42/items", request.RequestUri.AbsolutePath);
            Assert.Contains("apikey=test-key", request.RequestUri.Query, StringComparison.Ordinal);
            return Json("""
                {"movies":[{"id":12,"rank":2,"mediatype":"movie"}],
                 "shows":[{"id":34,"rank":1,"mediatype":"show"}]}
                """);
        }));
        IReadOnlyList<ListItem> items = await ListItems.MdbListAsync(http, 42, "test-key");
        Assert.Equal([new ListItem(MediaKind.Show, 34), new ListItem(MediaKind.Movie, 12)], items);
    }

    [Fact]
    public async Task Mdblist_external_list_uses_the_external_endpoint()
    {
        using HttpClient http = new(new Handler(request =>
        {
            Assert.Equal("/external/lists/42/items", request.RequestUri!.AbsolutePath);
            return Json("""{"movies":[{"id":12,"rank":1}],"shows":[]}""");
        }));
        Assert.Equal([new ListItem(MediaKind.Movie, 12)], await ListItems.MdbListAsync(http, 42, "test-key", external: true));
    }

    [Fact]
    public async Task Provider_failure_is_not_an_empty_list()
    {
        using HttpClient http = new(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        await Assert.ThrowsAsync<HttpRequestException>(() => ListItems.MdbListAsync(http, 42, "test-key"));
    }

    [Fact]
    public async Task Mdblist_unexpected_response_is_not_an_empty_list()
    {
        using HttpClient http = new(new Handler(_ => Json("{}")));
        await Assert.ThrowsAsync<JsonException>(() => ListItems.MdbListAsync(http, 42, "test-key"));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
