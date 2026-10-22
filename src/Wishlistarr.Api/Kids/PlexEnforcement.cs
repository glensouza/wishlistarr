using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Kids;

public sealed class PlexEnforcement(IMediaServerRestrictions restrictions)
{
    public async Task<IReadOnlyDictionary<int, ProfileRestrictions>> CheckAsync(IReadOnlyList<KidProfile> profiles, CancellationToken ct = default)
    {
        List<KidProfile> mapped = [.. profiles.Where(profile => !string.IsNullOrWhiteSpace(profile.PlexUserId))];
        if (mapped.Count == 0)
        {
            return new Dictionary<int, ProfileRestrictions>();
        }

        IReadOnlyDictionary<string, UserContentFilters>? users = await restrictions.GetUserFiltersAsync(ct);
        return mapped.ToDictionary(
            profile => profile.Id,
            profile => users is not null && users.TryGetValue(profile.PlexUserId!, out UserContentFilters? filters)
                ? LabelRestrictionEvaluator.Evaluate(filters, profile.PlexLabel)
                : ProfileRestrictions.Unknown);
    }
}
