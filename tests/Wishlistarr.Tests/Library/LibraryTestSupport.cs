using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Library;
using Wishlistarr.Core.Library;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;
using Wishlistarr.Tests.Jobs;

namespace Wishlistarr.Tests.Library;

public sealed class FakeScanner : ILibraryScanner
{
    public string ServerId { get; set; } = "server-a";

    public string? SelectedServerId { get; set; }

    public bool Complete { get; set; } = true;

    public int LabelScans { get; private set; }

    public List<LibraryEntry> Entries { get; } = [];

    public List<DateTimeOffset?> Scans { get; } = [];

    public static LibraryEntry Entry(
        string ratingKey,
        MediaKind kind,
        string title,
        IReadOnlyList<ExternalId> ids,
        IReadOnlyList<string>? labels = null,
        DateTimeOffset? updatedAt = null,
        string? guid = null) =>
        new(
            new MediaItem(ratingKey, guid ?? $"plex://{kind.ToString().ToLowerInvariant()}/{ratingKey}", title, 2000, kind, kind == MediaKind.Movie ? "1" : "2", ids),
            labels ?? [],
            updatedAt);

    public Task<LibraryScan> ScanAsync(DateTimeOffset? updatedSince, CancellationToken ct = default)
    {
        this.Scans.Add(updatedSince);
        IEnumerable<LibraryEntry> matching = this.Entries
            .Where(entry => updatedSince is null || entry.UpdatedAt > updatedSince)
            .Select(entry => entry with { Labels = null });
        return Task.FromResult(new LibraryScan(this.ServerId, matching.ToList().ToAsyncEnumerable())
        {
            Report = new LibraryScanReport { Complete = this.Complete },
        });
    }

    public Task<LibraryLabelScan> ScanLabelsAsync(CancellationToken ct = default)
    {
        this.LabelScans++;
        Dictionary<string, IReadOnlyList<string>> byItem = this.Entries
            .Where(entry => entry.Labels is { Count: > 0 })
            .ToDictionary(entry => entry.Item.Id, entry => entry.Labels!);
        return Task.FromResult(new LibraryLabelScan(this.ServerId, byItem, this.Complete));
    }

    public Task<string?> GetServerIdAsync(CancellationToken ct = default) => Task.FromResult<string?>(this.SelectedServerId ?? this.ServerId);

    public Task<LibrarySnapshot?> GetEntryAsync(string itemId, CancellationToken ct = default)
    {
        LibraryEntry? entry = this.Entries.FirstOrDefault(item => item.Item.Id == itemId);
        return Task.FromResult(entry is null ? null : new LibrarySnapshot(this.ServerId, entry));
    }
}

public sealed class FakeTmdbFinder : ITmdbClient
{
    public Dictionary<(TmdbMediaType Type, ExternalIdSource Source, string Value), int> Known { get; } = [];

    public List<(TmdbMediaType Type, ExternalIdSource Source, string Value)> Calls { get; } = [];

    public Exception? Failure { get; set; }

    public Task<int?> FindIdAsync(TmdbMediaType type, ExternalIdSource source, string externalId, CancellationToken ct = default)
    {
        this.Calls.Add((type, source, externalId));
        if (this.Failure is not null)
        {
            throw this.Failure;
        }

        return Task.FromResult<int?>(this.Known.TryGetValue((type, source, externalId), out int id) ? id : null);
    }

    public Task<bool> TestApiKeyAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<TmdbPage<TmdbSearchResult>> SearchAsync(string query, TmdbMediaType? type = null, int page = 1, string language = "en-US", CancellationToken ct = default) => throw new NotSupportedException();

    public Task<TmdbDetails> GetDetailsAsync(TmdbMediaType type, int id, string language = "en-US", CancellationToken ct = default) => throw new NotSupportedException();

    public Task<TmdbCredits> GetCreditsAsync(TmdbMediaType type, int id, string language = "en-US", CancellationToken ct = default) => throw new NotSupportedException();

    public Task<TmdbCertification?> GetCertificationAsync(TmdbMediaType type, int id, IReadOnlyList<string> regions, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<TmdbVideos> GetVideosAsync(TmdbMediaType type, int id, string language = "en-US", CancellationToken ct = default) => throw new NotSupportedException();

    public Task<TmdbImages> GetImagesAsync(TmdbMediaType type, int id, string imageLanguages = "en,null", CancellationToken ct = default) => throw new NotSupportedException();

    public Task<TmdbPage<TmdbSearchResult>> DiscoverAsync(TmdbMediaType type, TmdbDiscoverQuery query, string language = "en-US", CancellationToken ct = default) => throw new NotSupportedException();

    public Task<TmdbPage<TmdbSearchResult>> GetTrendingAsync(TmdbMediaType type, string window = "week", int page = 1, string language = "en-US", CancellationToken ct = default) => throw new NotSupportedException();

    public Task<TmdbWatchProviders> GetWatchProvidersAsync(TmdbMediaType type, int id, CancellationToken ct = default) => throw new NotSupportedException();
}

public sealed class SqlRecorder : DbCommandInterceptor
{
    private readonly List<string> statements = [];

    public IReadOnlyList<string> Statements
    {
        get
        {
            lock (this.statements)
            {
                return [.. this.statements];
            }
        }
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        string text = command.CommandText;
        foreach (DbParameter parameter in command.Parameters.Cast<DbParameter>().OrderByDescending(item => item.ParameterName.Length))
        {
            string literal = parameter.Value switch
            {
                null or DBNull => "NULL",
                string value => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'",
                bool flag => flag ? "1" : "0",
                IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
                var other => $"'{other}'",
            };
            text = text.Replace(parameter.ParameterName, literal, StringComparison.Ordinal);
        }

        lock (this.statements)
        {
            this.statements.Add(text);
        }

        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}

public sealed class LibraryHarness : IAsyncDisposable
{
    private readonly string directory;

    private LibraryHarness(ServiceProvider provider, string directory)
    {
        this.Provider = provider;
        this.directory = directory;
    }

    public ServiceProvider Provider { get; }

    public FakeScanner Scanner => this.Provider.GetRequiredService<FakeScanner>();

    public FakeTmdbFinder Tmdb => this.Provider.GetRequiredService<FakeTmdbFinder>();

    public SqlRecorder Sql => this.Provider.GetRequiredService<SqlRecorder>();

    public ManualTime Time => (ManualTime)this.Provider.GetRequiredService<TimeProvider>();

    public string DatabasePath => Path.Combine(this.directory, "library.db");

    public static async Task<LibraryHarness> CreateAsync()
    {
        string directory = Directory.CreateTempSubdirectory("wishlistarr-library").FullName;
        string database = Path.Combine(directory, "library.db");
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new ManualTime(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        services.AddSingleton<SqlRecorder>();
        services.AddDbContext<WishlistarrDbContext>((provider, options) =>
            options.UseWishlistarrSqlite(database).AddInterceptors(provider.GetRequiredService<SqlRecorder>()));
        services.AddSingleton<FakeScanner>();
        services.AddSingleton<ILibraryScanner>(provider => provider.GetRequiredService<FakeScanner>());
        services.AddSingleton<FakeTmdbFinder>();
        services.AddSingleton<ITmdbClient>(provider => provider.GetRequiredService<FakeTmdbFinder>());
        services.AddLibraryIndex();

        ServiceProvider provider = services.BuildServiceProvider();
        using (IServiceScope scope = provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().Database.MigrateAsync();
        }

        return new LibraryHarness(provider, directory);
    }

    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using IServiceScope scope = this.Provider.CreateScope();
        return await action(scope.ServiceProvider);
    }

    public Task FullAsync() => this.InScopeAsync(async services =>
    {
        await services.GetRequiredService<LibraryRefresh>().FullAsync(CancellationToken.None);
        return 0;
    });

    public Task IncrementalAsync() => this.InScopeAsync(async services =>
    {
        await services.GetRequiredService<LibraryRefresh>().IncrementalAsync(CancellationToken.None);
        return 0;
    });

    public Task<bool> ItemAsync(string ratingKey) =>
        this.InScopeAsync(services => services.GetRequiredService<LibraryRefresh>().TryItemAsync(ratingKey, CancellationToken.None));

    public Task<IReadOnlyList<MediaItem>?> FindAsync(MediaKind kind, ExternalIdSource source, string value) =>
        this.InScopeAsync(services => services.GetRequiredService<ILibraryIndex>().FindAsync(kind, new ExternalId(source, value)));

    public Task<List<LibraryItem>> GetItemsAsync() => this.InScopeAsync(services =>
        services.GetRequiredService<WishlistarrDbContext>().LibraryItems
            .AsNoTracking()
            .Include(item => item.ExternalIds)
            .Include(item => item.Labels)
            .OrderBy(item => item.RatingKey)
            .ToListAsync());

    public async ValueTask DisposeAsync()
    {
        await this.Provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(this.directory, true);
    }
}
