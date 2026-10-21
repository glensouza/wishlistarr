using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Security;
using Wishlistarr.Core;
using Wishlistarr.Data;

namespace Wishlistarr.Tests;

public sealed class ApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Status_without_key_is_unauthorized_problem_details()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1/system/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Status_with_wrong_key_is_unauthorized()
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyService.HeaderName, "wrong");

        HttpResponseMessage response = await client.GetAsync("/api/v1/system/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Status_with_key_returns_version_and_uptime()
    {
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/api/v1/system/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("version").GetString()));
        Assert.True(body.GetProperty("uptimeSeconds").GetInt64() >= 0);
    }

    [Fact]
    public async Task Uptime_is_measured_from_process_start_not_first_request()
    {
        using ApiFactory fresh = new();
        HttpClient client = await fresh.CreateAuthorizedClientAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(1100));

        JsonElement body = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/status");

        Assert.True(body.GetProperty("uptimeSeconds").GetInt64() >= 1);
    }

    [Fact]
    public async Task Reserved_keys_cannot_be_written_and_are_not_listed()
    {
        HttpClient client = await factory.CreateAuthorizedClientAsync();
        string before = await factory.GetApiKeyAsync();

        HttpResponseMessage response = await client.PutAsJsonAsync(
            "/api/v1/settings/system.api_key",
            new { value = "replacement", isSecret = false });
        string listed = await client.GetStringAsync("/api/v1/settings");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("system.", listed);
        Assert.Equal(before, await factory.GetApiKeyAsync());
    }

    [Fact]
    public async Task Invalid_setting_updates_return_bad_request_problem_details()
    {
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        HttpResponseMessage missingValue = await client.PutAsJsonAsync("/api/v1/settings/a", new { isSecret = false });
        HttpResponseMessage nullValue = await client.PutAsJsonAsync("/api/v1/settings/a", new { value = (string?)null });
        HttpResponseMessage longKey = await client.PutAsJsonAsync(
            "/api/v1/settings/" + new string('k', 201),
            new { value = "x", isSecret = false });

        foreach (HttpResponseMessage response in new[] { missingValue, nullValue, longKey })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task Health_stays_public()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_api_route_returns_problem_details()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Database_is_created_and_migrated_on_first_start()
    {
        await factory.GetApiKeyAsync();

        Assert.True(File.Exists(Path.Combine(factory.DataPath, "wishlistarr.db")));
        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task Database_uses_wal_and_busy_timeout()
    {
        await factory.GetApiKeyAsync();
        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        await db.Database.OpenConnectionAsync();
        try
        {
            Assert.Equal("wal", await Pragma(db, "journal_mode"));
            Assert.Equal(SqliteConfiguration.BusyTimeoutMilliseconds.ToString(), await Pragma(db, "busy_timeout"));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task Secret_is_encrypted_at_rest_and_not_returned()
    {
        HttpClient client = await factory.CreateAuthorizedClientAsync();
        const string secret = "example-key-12345";

        HttpResponseMessage put = await client.PutAsJsonAsync(
            "/api/v1/settings/example.api_key",
            new { value = secret, isSecret = true });
        await client.PutAsJsonAsync("/api/v1/settings/example.url", new { value = "http://example.test", isSecret = false });
        string listed = await client.GetStringAsync("/api/v1/settings");

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.DoesNotContain(secret, listed);
        Assert.DoesNotContain(await factory.GetApiKeyAsync(), listed);
        Assert.Contains("http://example.test", listed);

        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        Setting stored = await db.Settings.AsNoTracking().SingleAsync(setting => setting.Key == "example.api_key");
        Assert.True(stored.IsSecret);
        Assert.DoesNotContain(secret, stored.Value);
    }

    [Fact]
    public async Task Api_key_is_not_stored_in_plain_text()
    {
        string key = await factory.GetApiKeyAsync();

        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        Setting stored = await db.Settings.AsNoTracking().SingleAsync(setting => setting.Key == ApiKeyService.SettingKey);
        Assert.DoesNotContain(key, stored.Value);
    }

    [Fact]
    public async Task Keys_and_logs_are_written_to_the_data_directory()
    {
        HttpClient client = await factory.CreateAuthorizedClientAsync();
        await client.PutAsJsonAsync("/api/v1/settings/a", new { value = "b", isSecret = true });
        await client.GetAsync("/api/v1/system/status");

        Assert.NotEmpty(Directory.GetFiles(Path.Combine(factory.DataPath, "keys")));
        string[] logs = Directory.GetFiles(Path.Combine(factory.DataPath, "logs"), "wishlistarr-*.log");
        Assert.NotEmpty(logs);
        string content = string.Empty;
        for (int attempt = 0; attempt < 50 && !content.Contains("/api/v1/system/status"); attempt++)
        {
            await using FileStream stream = new(logs[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(stream);
            content = await reader.ReadToEndAsync();
            await Task.Delay(100);
        }

        Assert.Contains("/api/v1/system/status", content);
    }

    [Fact]
    public void Data_directory_defaults_under_content_root()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        string contentRoot = Path.GetTempPath();

        DataDirectory directory = DataDirectory.Resolve(configuration, contentRoot);

        Assert.Equal(Path.Combine(contentRoot, "data"), directory.Root);
    }

    private static async Task<string?> Pragma(WishlistarrDbContext db, string name)
    {
        await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA " + name;
        return Convert.ToString(await command.ExecuteScalarAsync());
    }
}
