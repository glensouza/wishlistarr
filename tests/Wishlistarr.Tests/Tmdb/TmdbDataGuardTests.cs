using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Library;
using Wishlistarr.Api.Settings;
using Wishlistarr.Api.Tmdb;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;

namespace Wishlistarr.Tests.Tmdb;

public sealed class TmdbDataGuardTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly string[] TmdbLookingNames =
    [
        "Overview", "Tagline", "Poster", "PosterPath", "Backdrop", "BackdropPath", "Genres", "Genre", "Keywords", "Cast", "Crew",
        "Credits", "Trailer", "Videos", "Certification", "Rating", "VoteAverage", "Popularity", "Runtime", "Title", "ReleaseDate",
        "Name", "Description", "ImagePath", "Image", "Keyword", "Studio", "Language", "Homepage",
    ];

    private static readonly HashSet<(string Entity, string Property)> NotFromTmdb =
    [
        (nameof(LibraryItem), nameof(LibraryItem.Title)),
        (nameof(LabelGuardianEntry), nameof(LabelGuardianEntry.Title)),
        (nameof(LibraryBackfillItem), nameof(LibraryBackfillItem.Title)),
        (nameof(KidProfile), nameof(KidProfile.Name)),
        (nameof(KidDevice), nameof(KidDevice.Name)),
    ];

    [Fact]
    public void Every_entity_marked_as_holding_tmdb_data_has_a_fetched_at_and_a_purge_store()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        IEntityType[] marked = [.. ModelEntities(scope).Where(entity => entity.ClrType.GetCustomAttribute<TmdbDataAttribute>() is not null)];
        HashSet<Type> stores = [.. scope.ServiceProvider.GetServices<ITmdbDataStore>().Select(store => store.EntityType)];

        Assert.NotEmpty(marked);
        foreach (IEntityType entity in marked)
        {
            TmdbDataAttribute marker = entity.ClrType.GetCustomAttribute<TmdbDataAttribute>()!;
            IProperty fetchedAt = entity.FindProperty(marker.FetchedAtProperty)
                ?? throw new Xunit.Sdk.XunitException($"{entity.ClrType.Name} does not map {marker.FetchedAtProperty}.");
            Assert.Equal(typeof(DateTimeOffset?), fetchedAt.ClrType);
            Assert.NotEmpty(marker.Properties);
            Assert.All(marker.Properties, name => Assert.NotNull(entity.FindProperty(name)));
            Assert.Contains(entity.ClrType, stores);
        }
    }

    [Fact]
    public void Every_purge_store_belongs_to_an_entity_marked_as_holding_tmdb_data()
    {
        using IServiceScope scope = factory.Services.CreateScope();

        foreach (ITmdbDataStore store in scope.ServiceProvider.GetServices<ITmdbDataStore>())
        {
            Assert.NotNull(store.EntityType.GetCustomAttribute<TmdbDataAttribute>());
        }
    }

    [Fact]
    public void Entities_with_tmdb_looking_columns_must_be_marked_unless_they_are_known_to_come_from_plex()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        List<string> unmarked = [];
        foreach (IEntityType entity in ModelEntities(scope).Where(entity => entity.ClrType.GetCustomAttribute<TmdbDataAttribute>() is null))
        {
            unmarked.AddRange(entity.GetProperties()
                .Where(property => TmdbLookingNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                .Where(property => !NotFromTmdb.Contains((entity.ClrType.Name, property.Name)))
                .Select(property => $"{entity.ClrType.Name}.{property.Name}"));
        }

        Assert.True(unmarked.Count == 0, $"Mark the entity with [TmdbData], give it a FetchedAt and register a purge store: {string.Join(", ", unmarked)}");
    }

    [Fact]
    public void The_purge_job_is_registered_with_the_job_runner()
    {
        JobRunner runner = factory.Services.GetRequiredService<JobRunner>();

        JobDefinition? definition = runner.Find(TmdbDataPurgeJob.JobName);

        Assert.NotNull(definition);
        Assert.Equal(TimeSpan.FromDays(1), definition.DefaultInterval);
    }

    [Fact]
    public async Task Retention_values_above_180_days_or_that_are_not_whole_days_are_rejected()
    {
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        HttpResponseMessage tooLong = await client.PutAsJsonAsync("/api/v1/settings/tmdb.retention_days", new { value = "181" });
        HttpResponseMessage zero = await client.PutAsJsonAsync("/api/v1/settings/tmdb.retention_days", new { value = "0" });
        HttpResponseMessage text = await client.PutAsJsonAsync("/api/v1/settings/tmdb.retention_days", new { value = "soon" });
        HttpResponseMessage max = await client.PutAsJsonAsync("/api/v1/settings/tmdb.retention_days", new { value = "180" });

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, text.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, max.StatusCode);
        using IServiceScope scope = factory.Services.CreateScope();
        Assert.Equal("180", await scope.ServiceProvider.GetRequiredService<SettingsStore>().GetAsync(TmdbRetention.SettingKey));
    }

    [Fact]
    public async Task Clearing_tmdb_data_is_parent_only_and_purges_everything()
    {
        await this.SeedAsync();
        await factory.EnsureParentAsync();
        HttpClient anonymous = factory.CreateClient();
        HttpClient kid = factory.CreateClient();
        kid.DefaultRequestHeaders.Add(AuthApiFactory.KidHeader, "1");
        HttpClient parent = await factory.CreateAuthorizedClientAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/v1/settings/tmdb/clear-data", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await kid.PostAsync("/api/v1/settings/tmdb/clear-data", null)).StatusCode);
        Assert.NotEmpty(await this.StoredTitlesAsync());
        HttpResponseMessage cleared = await parent.PostAsync("/api/v1/settings/tmdb/clear-data", null);

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.True((await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("purged").GetInt32() >= 2);
        Assert.Empty(await this.StoredTitlesAsync());
        Assert.Equal(0, await this.StoredLookupsAsync());
    }

    [Fact]
    public async Task Removing_the_tmdb_key_purges_stored_tmdb_data()
    {
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await parent.PutAsJsonAsync("/api/v1/settings/tmdb.api_key", new { value = "a-test-key" })).StatusCode);
        await this.SeedAsync();

        HttpResponseMessage removed = await parent.PutAsJsonAsync("/api/v1/settings/tmdb.api_key", new { value = " " });

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Empty(await this.StoredTitlesAsync());
        Assert.Equal(0, await this.StoredLookupsAsync());
        using IServiceScope scope = factory.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<SettingsStore>().GetAsync("tmdb.api_key"));
    }

    [Fact]
    public async Task A_client_that_gives_up_while_a_refresh_holds_the_gate_still_gets_everything_purged()
    {
        await this.SeedAsync();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        TmdbPurgeRunner runner = factory.Services.GetRequiredService<TmdbPurgeRunner>();
        runner.WaitLimit = TimeSpan.FromSeconds(5);
        IDisposable held = await factory.Services.GetRequiredService<LibraryRefreshGate>().EnterAsync(CancellationToken.None);
        try
        {
            using CancellationTokenSource giveUp = new(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parent.PostAsync("/api/v1/settings/tmdb/clear-data", null, giveUp.Token));

            await WaitForAsync(async () => (await this.StoredTitlesAsync()).Count == 0);
            Assert.True(await this.StoredLookupsAsync() > 0);
        }
        finally
        {
            held.Dispose();
            runner.WaitLimit = TimeSpan.FromSeconds(5);
        }

        await WaitForAsync(async () => await this.StoredLookupsAsync() == 0);
    }

    [Fact]
    public async Task Clearing_and_key_removal_answer_202_while_library_data_waits_and_purge_it_afterwards()
    {
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        TmdbPurgeRunner runner = factory.Services.GetRequiredService<TmdbPurgeRunner>();
        runner.WaitLimit = TimeSpan.FromMilliseconds(300);
        try
        {
            await this.SeedAsync();
            IDisposable held = await factory.Services.GetRequiredService<LibraryRefreshGate>().EnterAsync(CancellationToken.None);
            HttpResponseMessage clear = await parent.PostAsync("/api/v1/settings/tmdb/clear-data", null);
            JsonElement body = await clear.Content.ReadFromJsonAsync<JsonElement>();
            held.Dispose();
            await WaitForAsync(async () => await this.StoredLookupsAsync() == 0);

            await this.SeedAsync();
            Assert.Equal(HttpStatusCode.NoContent, (await parent.PutAsJsonAsync("/api/v1/settings/tmdb.api_key", new { value = "a-test-key" })).StatusCode);
            held = await factory.Services.GetRequiredService<LibraryRefreshGate>().EnterAsync(CancellationToken.None);
            HttpResponseMessage removed = await parent.PutAsJsonAsync("/api/v1/settings/tmdb.api_key", new { value = "" });
            held.Dispose();
            await WaitForAsync(async () => await this.StoredLookupsAsync() == 0);

            Assert.Equal(HttpStatusCode.Accepted, clear.StatusCode);
            Assert.True(body.GetProperty("pending").GetBoolean());
            Assert.Equal(HttpStatusCode.Accepted, removed.StatusCode);
            Assert.Empty(await this.StoredTitlesAsync());
        }
        finally
        {
            runner.WaitLimit = TimeSpan.FromSeconds(5);
        }
    }

    [Fact]
    public void The_library_refresh_gate_has_one_registration_shared_by_both_features()
    {
        ServiceCollection services = new();
        services.AddLibraryIndex();
        services.AddTmdbDataRetention();
        services.AddLibraryIndex();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(LibraryRefreshGate));
        Assert.Same(provider.GetRequiredService<LibraryRefreshGate>(), provider.GetRequiredService<LibraryRefreshGate>());
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (int attempt = 0; attempt < 200 && !await condition(); attempt++)
        {
            await Task.Delay(50);
        }

        Assert.True(await condition(), "The condition was not met in time.");
    }

    private static IEnumerable<IEntityType> ModelEntities(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().Model.GetEntityTypes().Where(entity => !entity.IsOwned());

    private async Task SeedAsync()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        KidProfile profile = new() { Name = $"Kid {Guid.NewGuid():N}"[..20], Avatar = "fox", PlexLabel = $"k{Guid.NewGuid():N}"[..20], CreatedAt = now };
        db.KidProfiles.Add(profile);
        db.MediaRequests.Add(new MediaRequest
        {
            KidProfile = profile,
            Kind = MediaKind.Movie,
            TmdbId = 603,
            Title = "The Matrix",
            TitleFetchedAt = now,
            State = RequestState.Pending,
            RequestCount = 1,
            CreatedAt = now,
            LastRequestedAt = now,
            StateChangedAt = now,
        });
        db.LibraryItems.Add(new LibraryItem
        {
            ServerId = "server-a",
            RatingKey = Guid.NewGuid().ToString("N")[..10],
            Guid = "plex://movie/x",
            Kind = MediaKind.Movie,
            LibraryId = "1",
            Title = "Legacy",
            ExternalIds = [new LibraryExternalId { Source = LibraryIdSource.Tmdb, Value = "278", Origin = LibraryIdOrigin.TmdbLookup, FetchedAt = now }],
        });
        await db.SaveChangesAsync();
    }

    private async Task<List<string?>> StoredTitlesAsync()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().MediaRequests
            .Where(request => request.Title != null)
            .Select(request => request.Title)
            .ToListAsync();
    }

    private async Task<int> StoredLookupsAsync()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().LibraryExternalIds
            .CountAsync(id => id.Origin == LibraryIdOrigin.TmdbLookup);
    }
}
