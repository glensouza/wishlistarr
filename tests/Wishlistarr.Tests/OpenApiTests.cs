using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Wishlistarr.Tests;

public sealed class DevelopmentApiFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment("Development");
    }
}

public sealed class ProductionApiFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment("Production");
    }
}

public sealed class OpenApiTests(DevelopmentApiFactory development, ProductionApiFactory production)
    : IClassFixture<DevelopmentApiFactory>, IClassFixture<ProductionApiFactory>
{
    [Fact]
    public async Task Development_serves_a_valid_document_with_api_key_scheme()
    {
        HttpClient client = development.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = document.RootElement;
        JsonElement operation = root.GetProperty("paths").GetProperty("/api/v1/system/status").GetProperty("get");
        JsonElement scheme = root.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey");
        Assert.Equal("apiKey", scheme.GetProperty("type").GetString());
        Assert.Equal("header", scheme.GetProperty("in").GetString());
        Assert.Equal("X-Api-Key", scheme.GetProperty("name").GetString());
        Assert.True(operation.GetProperty("security")[0].TryGetProperty("ApiKey", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("401", out _));
    }

    [Fact]
    public async Task Development_serves_the_scalar_ui()
    {
        HttpClient client = development.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/scalar/v1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/openapi/v1.json")]
    [InlineData("/scalar/v1")]
    public async Task Production_does_not_expose_api_docs(string path)
    {
        HttpClient client = production.CreateClient();

        HttpResponseMessage response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
