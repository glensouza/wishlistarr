using Microsoft.EntityFrameworkCore;
using Wishlistarr.Api.Requests;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Tmdb;

public sealed class ProfileTitleDataStore(WishlistarrDbContext db, RequestTitles titles, TimeProvider time) : ITmdbDataStore
{
    public Type EntityType => typeof(ProfileTitleRule);

    public bool WaitsForLibraryRefresh => false;

    public async Task RefreshAsync(DateTimeOffset staleBefore, CancellationToken ct)
    {
        List<ProfileTitleRule> rows = await db.ProfileTitleRules.Where(row => row.Title != null && db.KidProfiles.Any(profile => profile.Id == row.KidProfileId && profile.ArchivedAt == null)).ToListAsync(ct);
        foreach (ProfileTitleRule row in rows.Where(row => row.TitleFetchedAt is null || row.TitleFetchedAt < staleBefore))
        {
            if (await titles.FetchNameAsync(row.Kind, row.TmdbId, ct) is string name)
            {
                row.Title = name;
                row.TitleFetchedAt = time.GetUtcNow();
                await db.SaveChangesAsync(ct);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<int> PurgeExpiredAsync(DateTimeOffset expiredBefore, CancellationToken ct)
    {
        List<ProfileTitleRule> rows = await db.ProfileTitleRules.ToListAsync(ct);
        List<ProfileTitleRule> expired = [.. rows.Where(row => (row.Title != null || row.TitleFetchedAt != null) && (row.TitleFetchedAt is null || row.TitleFetchedAt < expiredBefore))];
        foreach (ProfileTitleRule row in expired)
        {
            row.Title = null;
            row.TitleFetchedAt = null;
        }

        await db.SaveChangesAsync(ct);
        return expired.Count;
    }

    public Task<int> PurgeAllAsync(CancellationToken ct) => db.ProfileTitleRules
        .Where(row => row.Title != null || row.TitleFetchedAt != null)
        .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Title, (string?)null).SetProperty(row => row.TitleFetchedAt, (DateTimeOffset?)null), ct);
}
