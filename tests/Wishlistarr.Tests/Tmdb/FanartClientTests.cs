using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using Wishlistarr.Integrations.Fanart;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Tests.Tmdb;

public sealed class FanartClientTests
{
    [Fact]
    public async Task Optional_key_and_movie_and_show_lookups_follow_the_fanart_api()
    {
        using TmdbCache cache = new(Options.Create(new TmdbOptions()), TimeProvider.System);
        Handler handler = new();
        using HttpClient http = new(handler) { BaseAddress = new Uri("https://webservice.fanart.tv/") };
        FanartClient client = new(http, cache);
        Assert.Null(await client.GetArtworkAsync(true, 10, null));
        Assert.Empty(handler.Requests);

        FanartArtwork? movie = await client.GetArtworkAsync(true, 10, "demo-key");
        Assert.Equal("https://assets.fanart.tv/fanart/movies/10/moviebackground/example.jpg", movie?.Background);
        Assert.Equal("https://assets.fanart.tv/fanart/movies/10/hdmovielogo/example.png", movie?.Logo);
        Assert.Equal("/v3/movies/10?api_key=demo-key", handler.Requests[0].PathAndQuery);
        await client.GetArtworkAsync(false, 20, "demo-key");
        Assert.Equal("/v3/tv/20?api_key=demo-key", handler.Requests[1].PathAndQuery);
        await client.GetArtworkAsync(true, 10, "project-key", "personal-key");
        Assert.Equal("/v3/movies/10?api_key=project-key&client_key=personal-key", handler.Requests[2].PathAndQuery);
        Assert.NotNull(await client.GetArtworkAsync(true, 10, null, "personal-key"));
        Assert.Equal("/v3/movies/10?client_key=personal-key", handler.Requests[3].PathAndQuery);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Cached_variants_are_fetched_only_from_the_asset_host()
    {
        using TmdbCache cache = new(Options.Create(new TmdbOptions()), TimeProvider.System);
        Handler handler = new();
        using HttpClient http = new(handler) { BaseAddress = new Uri("https://webservice.fanart.tv/") };
        FanartClient client = new(http, cache);
        const string url = "https://assets.fanart.tv/fanart/movies/10/moviebackground/example.jpg";
        await client.GetImageAsync(url, "bigpreview");
        await client.GetImageAsync(url, "bigpreview");
        await client.GetImageAsync(url, "full");
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://assets.fanart.tv/bigpreview/movies/10/moviebackground/example.jpg", handler.Requests[0].AbsoluteUri);
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetImageAsync("https://evil.test/fanart/image.jpg", "full"));
    }

    [Fact]
    public async Task Image_proxy_rejects_redirects_invalid_types_and_oversized_chunked_bodies()
    {
        using TmdbCache cache = new(Options.Create(new TmdbOptions()), TimeProvider.System);
        using HttpClient http = new(new FailureHandler()) { BaseAddress = new Uri("https://webservice.fanart.tv/") };
        FanartClient client = new(http, cache);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetImageAsync("https://assets.fanart.tv/fanart/redirect.jpg", "full"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetImageAsync("https://assets.fanart.tv/fanart/html.jpg", "full"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetImageAsync("https://assets.fanart.tv/fanart/large.jpg", "full"));
    }

    private sealed class FailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("redirect.jpg", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.invalid/image.jpg") } });
            if (request.RequestUri.AbsolutePath.EndsWith("html.jpg", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not an image", Encoding.UTF8, "text/html") });
            HttpContent content = new ChunkedContent();
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = content };
            response.Headers.TransferEncodingChunked = true;
            return Task.FromResult(response);
        }
    }

    private sealed class ChunkedContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(new byte[TmdbImageProxy.MaxBytes + 1]).AsTask();

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(new byte[TmdbImageProxy.MaxBytes + 1]));
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Requests.Add(request.RequestUri!);
            if (request.RequestUri!.Host == "assets.fanart.tv")
            {
                ByteArrayContent image = new([1, 2, 3]);
                image.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = image });
            }

            const string json = """{"moviebackground":[{"url":"https://evil.test/image.jpg"},{"url":"https://assets.fanart.tv/fanart/movies/10/moviebackground/example.jpg"}],"hdmovielogo":[{"url":"https://assets.fanart.tv/fanart/movies/10/hdmovielogo/example.png"}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
