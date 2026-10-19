using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace Wishlistarr.Tests;

public class WebRootFactory : ApiFactory
{
    public const string IndexHtml = "<!doctype html><title>test-shell</title>";
    public const string AssetJs = "console.log(1);";

    private readonly string webRoot = Directory.CreateTempSubdirectory("wishlistarr-web").FullName;

    public WebRootFactory()
    {
        File.WriteAllText(Path.Combine(this.webRoot, "index.html"), IndexHtml);
        Directory.CreateDirectory(Path.Combine(this.webRoot, "assets"));
        File.WriteAllText(Path.Combine(this.webRoot, "assets", "x.js"), AssetJs);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices((context, _) =>
        {
            context.HostingEnvironment.WebRootPath = this.webRoot;
            context.HostingEnvironment.WebRootFileProvider = new PhysicalFileProvider(this.webRoot);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(this.webRoot))
        {
            Directory.Delete(this.webRoot, true);
        }
    }
}

public sealed class StaticFrontendTests(WebRootFactory factory) : IClassFixture<WebRootFactory>
{
    [Fact]
    public async Task Client_route_falls_back_to_index_html()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/some/client/route");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(WebRootFactory.IndexHtml, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Root_serves_index_html()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(WebRootFactory.IndexHtml, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unknown_api_route_is_not_the_spa_shell()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual(WebRootFactory.IndexHtml, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Static_asset_is_served()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/assets/x.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(WebRootFactory.AssetJs, await response.Content.ReadAsStringAsync());
    }
}
