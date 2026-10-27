using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Tmdb;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Tests.Requests;

public sealed class ProfileTitleDataStoreTests
{
    private static ITmdbDataStore Store(IServiceScope scope) => Assert.Single(scope.ServiceProvider.GetServices<ITmdbDataStore>(), store => store.EntityType == typeof(ProfileTitleRule));

    [Fact]
    public async Task Refresh_skips_purged_names_and_archived_profiles_but_refreshes_stale_active_names()
    {
        using KidRequestApiFactory factory = new();
        (_, int active) = await factory.SignInKidAsync();
        (_, int archived) = await factory.SignInKidAsync("Bea");
        DateTimeOffset old = factory.Clock.GetUtcNow().AddDays(-100);
        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        await db.KidProfiles.Where(row => row.Id == archived).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ArchivedAt, old));
        db.ProfileTitleRules.AddRange(
            new ProfileTitleRule { KidProfileId = active, Kind = MediaKind.Movie, TmdbId = 603, Title = "Old name", TitleFetchedAt = old },
            new ProfileTitleRule { KidProfileId = active, Kind = MediaKind.Movie, TmdbId = 700 },
            new ProfileTitleRule { KidProfileId = archived, Kind = MediaKind.Movie, TmdbId = 701, Title = "Archived name", TitleFetchedAt = old });
        await db.SaveChangesAsync();
        await Store(scope).RefreshAsync(old.AddDays(1), CancellationToken.None);
        Assert.Equal("The Matrix", (await db.ProfileTitleRules.SingleAsync(row => row.TmdbId == 603)).Title);
        Assert.Equal(0, factory.Tmdb.Count("/3/movie/700"));
        Assert.Equal(0, factory.Tmdb.Count("/3/movie/701"));
        await Store(scope).PurgeAllAsync(CancellationToken.None);
        await Store(scope).RefreshAsync(factory.Clock.GetUtcNow(), CancellationToken.None);
        Assert.Equal(1, factory.Tmdb.Count("/3/movie/603"));
        Assert.Equal(0, factory.Tmdb.Count("/3/movie/700"));
        Assert.Equal(0, factory.Tmdb.Count("/3/movie/701"));
    }

    [Fact]
    public async Task A_later_refresh_failure_preserves_earlier_progress()
    {
        using KidRequestApiFactory factory = new();
        (_, int id) = await factory.SignInKidAsync();
        DateTimeOffset old = factory.Clock.GetUtcNow().AddDays(-100);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            foreach (int tmdbId in new[] { 603, 700 })
                db.ProfileTitleRules.Add(new ProfileTitleRule { KidProfileId = id, Kind = MediaKind.Movie, TmdbId = tmdbId, Title = "Old name", TitleFetchedAt = old });
            await db.SaveChangesAsync();
            factory.Tmdb.Enqueue("/3/movie/700", 500);
            await Assert.ThrowsAsync<TmdbException>(() => Store(scope).RefreshAsync(old.AddDays(1), CancellationToken.None));
        }
        using IServiceScope verify = factory.Services.CreateScope();
        ProfileTitleRule first = await verify.ServiceProvider.GetRequiredService<WishlistarrDbContext>().ProfileTitleRules.SingleAsync(row => row.TmdbId == 603);
        Assert.Equal("The Matrix", first.Title);
        Assert.Equal(factory.Clock.GetUtcNow(), first.TitleFetchedAt);
    }

    [Fact]
    public async Task Client_and_imported_names_never_get_a_fetched_timestamp_and_placeholders_are_not_names()
    {
        using KidRequestApiFactory factory = new();
        (_, int id) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        string path = $"/api/v1/kid-profiles/{id}";
        Assert.Equal(HttpStatusCode.NoContent, (await parent.PostAsJsonAsync(path + "/titles", new { kind = "movie", tmdbId = 603, title = "The Matrix", blocked = true })).StatusCode);
        JsonElement export = await parent.GetFromJsonAsync<JsonElement>(path + "/content-rules");
        factory.Clock.Advance(TimeSpan.FromDays(100));
        Assert.Equal(HttpStatusCode.OK, (await parent.PutAsJsonAsync(path + "/content-rules", export)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await parent.PostAsJsonAsync(path + "/titles", new { kind = "show", tmdbId = 1, title = "Show 1", blocked = true })).StatusCode);
        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        ProfileTitleRule movie = await db.ProfileTitleRules.SingleAsync(row => row.TmdbId == 603);
        Assert.Equal("The Matrix", movie.Title);
        Assert.Null(movie.TitleFetchedAt);
        Assert.Null((await db.ProfileTitleRules.SingleAsync(row => row.Kind == MediaKind.Show)).Title);
        Assert.Equal(1, await Store(scope).PurgeExpiredAsync(factory.Clock.GetUtcNow().AddDays(-180), CancellationToken.None));
        Assert.Null(movie.Title);
    }
}
