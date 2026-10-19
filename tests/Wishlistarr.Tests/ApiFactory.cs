using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Security;
using Wishlistarr.Api.Settings;
using Wishlistarr.Integrations.Plex;

namespace Wishlistarr.Tests;

public class ApiFactory : WebApplicationFactory<Program>
{
    public const string ParentUsername = "parent";
    public const string ParentPassword = "correct horse battery";

    public ApiFactory() => this.DataPath = Directory.CreateTempSubdirectory("wishlistarr-data").FullName;

    public string DataPath { get; }

    public async Task<string> GetApiKeyAsync()
    {
        using IServiceScope scope = this.Services.CreateScope();
        SettingsStore store = scope.ServiceProvider.GetRequiredService<SettingsStore>();
        return await store.GetAsync(ApiKeyService.SettingKey) ?? throw new InvalidOperationException("No API key");
    }

    public async Task EnsureParentAsync()
    {
        using IServiceScope scope = this.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ParentAccounts>().CreateFirstAsync(ParentUsername, ParentPassword);
    }

    public async Task<HttpClient> CreateAuthorizedClientAsync()
    {
        await this.EnsureParentAsync();
        HttpClient client = this.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyService.HeaderName, await this.GetApiKeyAsync());
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("WISHLISTARR_DATA_DIR", this.DataPath);
        builder.ConfigureTestServices(services => services.Configure<PlexOptions>(options =>
            options.PlexTvBaseAddress = new Uri("http://127.0.0.1:9/")));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        SqliteConnection.ClearAllPools();
        for (int attempt = 0; attempt < 5 && Directory.Exists(this.DataPath); attempt++)
        {
            try
            {
                Directory.Delete(this.DataPath, true);
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
        }
    }
}
