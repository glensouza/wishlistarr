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

public sealed class ProductionApiFactory : WebRootFactory
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
        JsonElement paths = root.GetProperty("paths");
        JsonElement operation = paths.GetProperty("/api/v1/system/status").GetProperty("get");
        JsonElement scheme = root.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey");
        Assert.Equal("apiKey", scheme.GetProperty("type").GetString());
        Assert.Equal("header", scheme.GetProperty("in").GetString());
        Assert.Equal("X-Api-Key", scheme.GetProperty("name").GetString());
        Assert.True(operation.GetProperty("security")[0].TryGetProperty("ApiKey", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("401", out _));
        Assert.False(paths.GetProperty("/api/health").GetProperty("get").TryGetProperty("security", out _));
    }

    [Fact]
    public async Task Development_document_marks_secret_flag_optional()
    {
        HttpClient client = development.CreateClient();

        string json = await client.GetStringAsync("/openapi/v1.json");

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement schema = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("SettingUpdate");
        bool required = schema.TryGetProperty("required", out JsonElement list)
            && list.EnumerateArray().Any(item => item.GetString() == "isSecret");
        Assert.False(required);
    }

    [Fact]
    public async Task Development_serves_the_scalar_ui()
    {
        HttpClient client = development.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/scalar/v1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("scalar", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Production_does_not_serve_the_openapi_document()
    {
        HttpClient client = production.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Production_serves_the_spa_shell_instead_of_the_scalar_ui()
    {
        HttpClient client = production.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/scalar/v1");

        Assert.Equal(WebRootFactory.IndexHtml, await response.Content.ReadAsStringAsync());
    }
}
