using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Jobs;
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

    private static readonly HashSet<(string Entity, string Property)> PlexSourced =
    [
        (nameof(LibraryItem), nameof(LibraryItem.Title)),
        (nameof(KidProfile), nameof(KidProfile.Name)),
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
                .Where(property => !PlexSourced.Contains((entity.ClrType.Name, property.Name)))
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
