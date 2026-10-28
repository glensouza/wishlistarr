using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace Wishlistarr.Tests;

public class WebRootFactory : ApiFactory
{
    public const string IndexHtml = "<!doctype html><title>test-shell</title>";
    public const string AssetJs = "console.log(1);";
    public const string NpmNotice = """{"packages":[{"name":"react","version":"1.0.0","license":"MIT"}]}""";
    public const string ServiceWorker = "self.addEventListener('push',()=>{});";

    private readonly string webRoot = Directory.CreateTempSubdirectory("wishlistarr-web").FullName;

    public WebRootFactory()
    {
        File.WriteAllText(Path.Combine(this.webRoot, "index.html"), IndexHtml);
        File.WriteAllText(Path.Combine(this.webRoot, "sw.js"), ServiceWorker);
        Directory.CreateDirectory(Path.Combine(this.webRoot, "assets"));
        File.WriteAllText(Path.Combine(this.webRoot, "assets", "x.js"), AssetJs);
        Directory.CreateDirectory(Path.Combine(this.webRoot, "licenses"));
        File.WriteAllText(Path.Combine(this.webRoot, "licenses", "npm.json"), NpmNotice);
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

    [Fact]
    public async Task Service_worker_is_served_as_javascript_for_the_built_frontend()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/sw.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("javascript", response.Content.Headers.ContentType?.MediaType, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WebRootFactory.ServiceWorker, await response.Content.ReadAsStringAsync());
        Assert.NotEqual(WebRootFactory.IndexHtml, await client.GetStringAsync("/sw.js"));
    }

    [Fact]
    public async Task License_notice_is_served_without_signing_in()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/licenses/npm.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(WebRootFactory.NpmNotice, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Missing_license_notice_is_not_a_license_list()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/licenses/nuget.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("packages", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
