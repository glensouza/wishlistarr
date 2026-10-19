using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Tests.Tmdb;

public sealed class TmdbApiFactory : ApiFactory
{
    public TmdbApiFactory() => this.Fake = FakeTmdbServer.StartAsync().GetAwaiter().GetResult();

    public FakeTmdbServer Fake { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.Configure<TmdbOptions>(options =>
        {
            options.ApiBaseAddress = this.Fake.ApiBaseAddress;
            options.ImageBaseAddress = this.Fake.ImageBaseAddress;
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

public sealed class TmdbApiTests(TmdbApiFactory factory) : IClassFixture<TmdbApiFactory>
{
    [Fact]
    public async Task Api_key_setting_is_always_stored_as_a_secret()
    {
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        HttpResponseMessage put = await client.PutAsJsonAsync(
            $"/api/v1/settings/{TmdbOptions.ApiKeySetting}",
            new { value = "abc123", isSecret = false });
        JsonElement settings = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings");

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        JsonElement entry = settings.EnumerateArray().Single(item => item.GetProperty("key").GetString() == TmdbOptions.ApiKeySetting);
        Assert.True(entry.GetProperty("isSecret").GetBoolean());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task Key_test_reports_missing_invalid_and_valid_keys()
    {
        using TmdbApiFactory isolated = new();
        HttpClient client = await isolated.CreateAuthorizedClientAsync();

        Assert.False(await TestKeyAsync(client));
        Assert.Empty(isolated.Fake.Requests);

        await client.PutAsJsonAsync($"/api/v1/settings/{TmdbOptions.ApiKeySetting}", new { value = "wrong" });
        Assert.False(await TestKeyAsync(client));

        await client.PutAsJsonAsync($"/api/v1/settings/{TmdbOptions.ApiKeySetting}", new { value = FakeTmdbServer.ValidApiKey });
        Assert.True(await TestKeyAsync(client));
    }

    [Fact]
    public async Task Key_test_requires_authentication()
    {
        HttpResponseMessage response = await factory.CreateClient().PostAsync("/api/v1/settings/tmdb/test", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Image_proxy_serves_allowed_images()
    {
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/api/v1/images/tmdb/w500/poster.jpg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(FakeTmdbServer.ImageBytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Theory]
    [InlineData("/api/v1/images/tmdb/w501/poster.jpg")]
    [InlineData("/api/v1/images/tmdb/w500/poster.svg")]
    [InlineData("/api/v1/images/tmdb/w500/%2e%2e")]
    [InlineData("/api/v1/images/tmdb/w500/%2e%2e%2fsecret.jpg")]
    [InlineData("/api/v1/images/tmdb/w500/http:%2f%2fevil.test%2fx.jpg")]
    public async Task Image_proxy_rejects_other_sizes_and_paths(string url)
    {
        using TmdbApiFactory isolated = new();
        HttpClient client = await isolated.CreateAuthorizedClientAsync();

        HttpResponseMessage response = await client.GetAsync(url);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(isolated.Fake.Requests);
    }

    [Fact]
    public async Task Cache_headers_are_only_set_on_successful_images()
    {
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        HttpResponseMessage ok = await client.GetAsync("/api/v1/images/tmdb/w500/poster.jpg");
        HttpResponseMessage rejected = await client.GetAsync("/api/v1/images/tmdb/w501/poster.jpg");
        HttpResponseMessage missing = await client.GetAsync("/api/v1/images/tmdb/w500/missing.jpg");

        Assert.NotNull(ok.Headers.CacheControl);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Null(rejected.Headers.CacheControl);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Null(missing.Headers.CacheControl);
    }

    private static async Task<bool> TestKeyAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.PostAsync("/api/v1/settings/tmdb/test", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("valid").GetBoolean();
    }
}
