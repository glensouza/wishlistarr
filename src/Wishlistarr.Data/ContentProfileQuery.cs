using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace Wishlistarr.Data;

public static class ContentProfileQuery
{
    public static async Task<KidProfile?> FirstWithContentRulesAsync(this IQueryable<KidProfile> profiles, Expression<Func<KidProfile, bool>> predicate, CancellationToken ct)
    {
        KidProfile? profile = await profiles.Include(row => row.TitleRules).FirstOrDefaultAsync(predicate, ct);
        profile?.MarkContentRulesLoaded();
        return profile;
    }

    public static async Task<List<KidProfile>> ListWithContentRulesAsync(this IQueryable<KidProfile> profiles, Expression<Func<KidProfile, bool>> predicate, CancellationToken ct)
    {
        List<KidProfile> found = await profiles.Include(row => row.TitleRules).Where(predicate).ToListAsync(ct);
        foreach (KidProfile profile in found)
        {
            profile.MarkContentRulesLoaded();
        }

        return found;
    }
}
