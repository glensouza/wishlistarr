using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Settings;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Settings;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Plex;

namespace Wishlistarr.Tests.Plex;

public sealed class SqlitePlexHarness : IAsyncDisposable
{
    private readonly ServiceProvider provider;
    private readonly string directory;

    private SqlitePlexHarness(ServiceProvider provider, FakePlexServer server, string directory, string databasePath)
    {
        this.provider = provider;
        this.Server = server;
        this.directory = directory;
        this.DatabasePath = databasePath;
    }

    public FakePlexServer Server { get; }

    public string DatabasePath { get; }

    public IMediaServer Media => this.provider.GetRequiredService<IMediaServer>();

    public IMediaServerAccount Account => this.provider.GetRequiredService<IMediaServerAccount>();

    public static async Task<SqlitePlexHarness> CreateAsync(params IInterceptor[] interceptors)
    {
        string directory = Directory.CreateTempSubdirectory("wishlistarr-plex").FullName;
        string database = Path.Combine(directory, "wishlistarr.db");
        FakePlexServer server = await FakePlexServer.StartAsync();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<WishlistarrDbContext>(options =>
        {
            options.UseWishlistarrSqlite(database);
            if (interceptors.Length > 0)
            {
                options.AddInterceptors(interceptors);
            }
        });
        services.AddScoped<SettingsStore>();
        services.AddPlex<StoredAppSettings>();
        services.Configure<PlexOptions>(options =>
        {
            options.PlexTvBaseAddress = server.Address;
            options.ProbeTimeout = TimeSpan.FromSeconds(3);
        });

        ServiceProvider provider = services.BuildServiceProvider();
        try
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().Database.MigrateAsync();
            return new SqlitePlexHarness(provider, server, directory, database);
        }
        catch
        {
            await provider.DisposeAsync();
            await server.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
            throw;
        }
    }

    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using AsyncServiceScope scope = this.provider.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    public async Task LinkAsync()
    {
        MediaServerLinkStart link = await this.Account.StartLinkAsync();
        this.Server.PinApproved = true;
        MediaServerLinkCheck? check = await this.Account.CheckLinkAsync(link.Id);
        Assert.Equal(MediaServerLinkState.Linked, check?.State);
    }

    public async Task<SelectedMediaServer> LinkAndSelectAsync()
    {
        await this.LinkAsync();
        SelectedMediaServer? selected = await this.Account.SelectServerAsync(FakePlexServer.MachineIdentifier);
        Assert.NotNull(selected);
        return selected;
    }

    public Task<int> CountAsync(string key) => this.InScopeAsync(services =>
        services.GetRequiredService<WishlistarrDbContext>().Settings.CountAsync(row => row.Key == key));

    public async Task<Dictionary<string, string>> ReadRawAsync(params string[] keys)
    {
        Dictionary<string, string> values = [];
        await using SqliteConnection connection = new($"Data Source={this.DatabasePath}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """SELECT "Key", "Value" FROM "Settings" WHERE "Key" = $key""";
        SqliteParameter parameter = command.CreateParameter();
        parameter.ParameterName = "$key";
        command.Parameters.Add(parameter);
        foreach (string key in keys)
        {
            parameter.Value = key;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                values[key] = reader.GetString(1);
            }
        }

        return values;
    }

    public async ValueTask DisposeAsync()
    {
        await this.provider.DisposeAsync();
        await this.Server.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(this.directory, true);
    }
}
