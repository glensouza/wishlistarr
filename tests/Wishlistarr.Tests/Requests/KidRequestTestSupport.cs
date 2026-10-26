using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.Library;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Plex;
using Wishlistarr.Integrations.Tmdb;
using Wishlistarr.Tests.Plex;
using Wishlistarr.Tests.Tmdb;

namespace Wishlistarr.Tests.Requests;

public sealed class Rendezvous(int parties)
{
    private readonly TaskCompletionSource all = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int arrived;

    public int Arrived => Volatile.Read(ref this.arrived);

    public async Task ArriveAsync()
    {
        if (Interlocked.Increment(ref this.arrived) == parties)
        {
            this.all.TrySetResult();
        }

        await this.all.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}

public sealed class DecisionSaveGate : SaveChangesInterceptor
{
    public Func<Task>? Gate { get; set; }

    public Func<Task>? GrantCreateGate { get; set; }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        List<MediaRequestEvent> added = [.. eventData.Context!.ChangeTracker.Entries<MediaRequestEvent>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity)];
        bool decision = added.Any(item => item.FromState == RequestState.Pending && item.ToState is RequestState.Approved or RequestState.Denied);
        if (decision && this.Gate is not null)
        {
            await this.Gate();
        }

        bool grantCreated = added.Any(item => item.FromState is null && item.ActorKind == RequestActorKind.Parent);
        if (grantCreated && this.GrantCreateGate is not null)
        {
            await this.GrantCreateGate();
        }

        return result;
    }
}

public sealed class FakeLibraryIndex : ILibraryIndex
{
    private readonly Dictionary<(MediaKind, ExternalId), List<MediaItem>> items = [];

    public bool Ready { get; set; } = true;

    public IReadOnlyList<LibraryCatalogEntry>? Recent { get; set; }

    public int Calls { get; private set; }

    public int ManyCalls { get; private set; }

    public Func<Task>? Gate { get; set; }

    public int GuidCalls { get; private set; }

    public void Add(MediaKind kind, int tmdbId, MediaItem item)
    {
        ExternalId id = new(ExternalIdSource.Tmdb, tmdbId.ToString(CultureInfo.InvariantCulture));
        if (!this.items.TryGetValue((kind, id), out List<MediaItem>? list))
        {
            this.items[(kind, id)] = list = [];
        }

        list.Add(item);
    }

    public void Remove(MediaKind kind, int tmdbId) =>
        this.items.Remove((kind, new ExternalId(ExternalIdSource.Tmdb, tmdbId.ToString(CultureInfo.InvariantCulture))));

    public async Task<IReadOnlyList<MediaItem>?> FindAsync(MediaKind kind, ExternalId id, CancellationToken ct = default)
    {
        this.Calls++;
        if (this.Gate is not null)
        {
            await this.Gate();
        }

        if (!this.Ready)
        {
            return null;
        }

        return this.items.TryGetValue((kind, id), out List<MediaItem>? list) ? list : [];
    }

    public Task<IndexedGuids?> FindGuidsAsync(IReadOnlyCollection<TitleKey> titles, CancellationToken ct = default)
    {
        this.GuidCalls++;
        if (!this.Ready)
        {
            return Task.FromResult<IndexedGuids?>(null);
        }

        Dictionary<TitleKey, IReadOnlyList<string>> guids = [];
        foreach (TitleKey title in titles)
        {
            ExternalId id = new(ExternalIdSource.Tmdb, title.TmdbId.ToString(CultureInfo.InvariantCulture));
            if (this.items.TryGetValue((title.Kind, id), out List<MediaItem>? list) && list.Count > 0)
            {
                guids[title] = [.. list.Select(item => item.Guid)];
            }
        }

        return Task.FromResult<IndexedGuids?>(new IndexedGuids(FakePlexServer.MachineIdentifier, guids));
    }

    public Task<IReadOnlyList<LibraryCatalogEntry>?> ListRecentAsync(int limit, CancellationToken ct = default)
    {
        if (!this.Ready)
        {
            return Task.FromResult<IReadOnlyList<LibraryCatalogEntry>?>(null);
        }

        if (limit <= 0)
        {
            return Task.FromResult<IReadOnlyList<LibraryCatalogEntry>?>([]);
        }

        IReadOnlyList<LibraryCatalogEntry> recent = this.Recent ?? [];
        return Task.FromResult<IReadOnlyList<LibraryCatalogEntry>?>(recent.Take(limit).ToList());
    }

    public Task<IReadOnlyDictionary<TitleKey, IReadOnlyList<MediaItem>>?> FindManyByTmdbAsync(IReadOnlyCollection<TitleKey> titles, CancellationToken ct = default)
    {
        this.ManyCalls++;
        if (!this.Ready)
        {
            return Task.FromResult<IReadOnlyDictionary<TitleKey, IReadOnlyList<MediaItem>>?>(null);
        }

        Dictionary<TitleKey, IReadOnlyList<MediaItem>> found = [];
        foreach (TitleKey title in titles)
        {
            ExternalId id = new(ExternalIdSource.Tmdb, title.TmdbId.ToString(CultureInfo.InvariantCulture));
            if (this.items.TryGetValue((title.Kind, id), out List<MediaItem>? list))
            {
                found[title] = list;
            }
        }

        return Task.FromResult<IReadOnlyDictionary<TitleKey, IReadOnlyList<MediaItem>>?>(found);
    }
}

public class KidRequestApiFactory : AuthApiFactory
{
    public const string RequestsPath = "/api/v1/kid/requests";

    public KidRequestApiFactory(FakeLibraryIndex? index = null)
    {
        this.Index = index;
        this.Plex = FakePlexServer.StartAsync().GetAwaiter().GetResult();
        this.Tmdb = FakeTmdbServer.StartAsync().GetAwaiter().GetResult();
        this.Tmdb.SetBody("/3/movie/129", """{"id":129,"title":"Spirited Away","vote_average":8.5}""");
        this.Tmdb.SetBody("/3/movie/129/release_dates", """{"results":[{"iso_3166_1":"US","release_dates":[{"certification":"","type":3}]}]}""");
        this.Tmdb.SetBody("/3/tv/82728", """{"id":82728,"name":"Bluey","vote_average":8.8}""");
        this.Tmdb.SetBody("/3/tv/82728/content_ratings", """{"results":[{"iso_3166_1":"US","rating":"TV-Y"}]}""");
        foreach (int id in NotInLibrary)
        {
            this.Tmdb.SetBody($"/3/movie/{id}", $$"""{"id":{{id}},"title":"Movie {{id}}","vote_average":7}""");
            this.Tmdb.SetBody($"/3/movie/{id}/release_dates", """{"results":[{"iso_3166_1":"US","release_dates":[{"certification":"PG","type":3}]}]}""");
        }
    }

    public static IReadOnlyList<int> NotInLibrary { get; } = [700, 701, 702, 703];

    public FakeLibraryIndex? Index { get; }

    public FakePlexServer Plex { get; }

    public FakeTmdbServer Tmdb { get; }

    public TestClock Clock { get; } = new();

    public DecisionSaveGate Saves { get; } = new();

    public int LabelWrites => this.Plex.Requests.Count(request => request.Method == "PUT");

    public int LibraryScans => this.Plex.Requests.Count(request => request.Path.StartsWith("/library/sections/", StringComparison.Ordinal) && request.Method == "GET");

    public static Task<HttpResponseMessage> RequestAsync(HttpClient kid, string kind, int tmdbId) =>
        kid.PostAsJsonAsync(RequestsPath, new { kind, tmdbId });

    public async Task LinkPlexAsync()
    {
        IMediaServerAccount account = this.Services.GetRequiredService<IMediaServerAccount>();
        MediaServerLinkStart link = await account.StartLinkAsync();
        this.Plex.PinApproved = true;
        await account.CheckLinkAsync(link.Id);
        Assert.NotNull(await account.SelectServerAsync(FakePlexServer.MachineIdentifier));
    }

    public async Task<(HttpClient Kid, int ProfileId)> SignInKidAsync(
        string name = "Ada",
        int? movieAgeLimit = null,
        int? tvAgeLimit = null,
        int? weeklyAllowance = null,
        int[]? blockedGenreIds = null,
        int[]? blockedKeywordIds = null,
        string[]? allowedOriginalLanguages = null,
        string[]? homeRows = null,
        bool autoApprove = false,
        string? autoApproveMovieRating = null,
        string? autoApproveTvRating = null)
    {
        HttpClient parent = await this.CreateAuthorizedClientAsync();
        HttpResponseMessage created = await parent.PostAsJsonAsync(
            "/api/v1/kid-profiles",
            new
            {
                name,
                avatar = "fox",
                movieAgeLimit,
                tvAgeLimit,
                weeklyAllowance,
                blockedGenreIds,
                blockedKeywordIds,
                allowedOriginalLanguages,
                homeRows,
                autoApprove,
                autoApproveMovieRating,
                autoApproveTvRating,
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        int profileId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        HttpClient kid = await this.PairDeviceAsync(parent, "Tablet", [profileId]);
        Assert.Equal(HttpStatusCode.OK, (await KidApiFactory.SignInAsync(kid, profileId, null)).StatusCode);
        await AddAntiforgeryTokenAsync(kid);
        return (kid, profileId);
    }

    public async Task SetTimeZoneAsync(string zone)
    {
        HttpClient parent = await this.CreateAuthorizedClientAsync();
        HttpResponseMessage response = await parent.PutAsJsonAsync("/api/v1/settings/parent.time_zone", new { value = zone });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    public void SetClock(DateTimeOffset instant) => this.Clock.Advance(instant - this.Clock.GetUtcNow());

    public async Task<List<MediaRequest>> GetRequestsAsync()
    {
        using IServiceScope scope = this.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().MediaRequests
            .AsNoTracking()
            .OrderBy(request => request.Id)
            .ToListAsync();
    }

    public async Task<List<MediaRequestEvent>> GetHistoryAsync(int requestId)
    {
        using IServiceScope scope = this.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().MediaRequestEvents
            .AsNoTracking()
            .Where(item => item.MediaRequestId == requestId)
            .OrderBy(item => item.Id)
            .ToListAsync();
    }

    public async Task<List<OwnedLabel>> GetLedgerAsync()
    {
        using IServiceScope scope = this.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().OwnedLabels
            .AsNoTracking()
            .OrderBy(label => label.Id)
            .ToListAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.Configure<PlexOptions>(options =>
            {
                options.PlexTvBaseAddress = this.Plex.Address;
                options.ProbeTimeout = TimeSpan.FromSeconds(3);
            });
            services.Configure<TmdbOptions>(options =>
            {
                options.ApiBaseAddress = this.Tmdb.ApiBaseAddress;
                options.ImageBaseAddress = this.Tmdb.ImageBaseAddress;
            });
            services.RemoveAll<ITmdbApiKeyProvider>();
            services.AddSingleton<ITmdbApiKeyProvider>(new FixedKeyProvider());
            services.AddDbContext<WishlistarrDbContext>(options => options.AddInterceptors(this.Saves));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(this.Clock);
            if (this.Index is not null)
            {
                services.AddSingleton<ILibraryIndex>(this.Index);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            this.Plex.DisposeAsync().AsTask().GetAwaiter().GetResult();
            this.Tmdb.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
