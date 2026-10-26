using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Wishlistarr.Tests;

public sealed class HealthEndpointTests(ApiFactory factory)
    : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Health_returns_ok()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/health");
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["status"], [.. body.EnumerateObject().Select(property => property.Name)]);
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }
}
