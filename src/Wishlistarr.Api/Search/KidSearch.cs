using Microsoft.EntityFrameworkCore;
using Wishlistarr.Api.Kids;
using Wishlistarr.Core.Content;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Search;

public sealed record KidSearchResponse(string Query, int Page, bool HasMore, IReadOnlyList<KidMediaItem> Results);

public sealed class KidSearch(
    WishlistarrDbContext db,
    ITmdbClient tmdb,
    KidContentPipeline pipeline)
{
    public const int MinQueryLength = 2;
    public const int MaxQueryLength = 100;
    public const int MaxPage = 10;
    public const int MaxParallelLookups = KidContentPipeline.MaxParallelLookups;
    public const string PosterSize = KidContentPipeline.PosterSize;
    public const string PosterRoute = KidContentPipeline.PosterRoute;

    public static readonly TimeSpan LookupBudget = KidContentPipeline.LookupBudget;

    public static bool TryNormalize(string? query, out string normalized)
    {
        normalized = string.Join(' ', (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length is >= MinQueryLength and <= MaxQueryLength && !normalized.Any(char.IsControl);
    }

    public async Task<KidSearchResponse?> SearchAsync(int kidProfileId, string query, int page, CancellationToken ct = default)
    {
        KidProfile? profile = await db.KidProfiles.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == kidProfileId && row.ArchivedAt == null, ct);
        if (profile is null)
        {
            return null;
        }

        TmdbPage<TmdbSearchResult> found = await tmdb.SearchAsync(query, page: page, ct: ct);
        List<KidCandidate> candidates = [.. found.Results.Select(KidContentPipeline.ToCandidate).OfType<KidCandidate>().DistinctBy(item => item.Key)];

        ContentPolicy policy = ContentPolicy.ForProfile(
            profile.MovieAgeLimit,
            profile.TvAgeLimit,
            profile.BlockedGenreIds,
            profile.BlockedKeywordIds,
            profile.AllowedOriginalLanguages);

        IReadOnlyList<KidMediaItem> results = await pipeline.FilterAsync(candidates, policy, allowedOnly: false, ct);
        return new KidSearchResponse(query, page, page < Math.Min(found.TotalPages, MaxPage), results);
    }
}
