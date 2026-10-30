using Microsoft.EntityFrameworkCore;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Requests;

public sealed class RequestSeasons(WishlistarrDbContext db, ITmdbClient tmdb, IMediaServer server)
{
    // TMDB episode counts and production status are read for this check and never persisted.
    public async Task<bool> RefreshAsync(MediaRequest request, IReadOnlyList<MediaItem> matches, CancellationToken ct)
    {
        TmdbDetails details = await tmdb.GetDetailsAsync(TmdbMediaType.Tv, request.TmdbId, ct: ct);
        Dictionary<int, int> expected = (details.Seasons ?? []).Where(season => season.SeasonNumber >= 0)
            .GroupBy(season => season.SeasonNumber).ToDictionary(group => group.Key, group => group.Max(season => season.EpisodeCount));
        Dictionary<int, int> episodes = [];
        foreach (MediaItem match in matches)
        {
            foreach (MediaSeason season in await server.GetSeasonsAsync(match.Id, ct))
            {
                // Copies may overlap. Taking the largest copy cannot mistake duplicate episodes for a complete season.
                episodes[season.Number] = Math.Max(episodes.GetValueOrDefault(season.Number), season.EpisodeCount);
            }
        }

        await db.Entry(request).Collection(row => row.Seasons).LoadAsync(ct);
        foreach (int number in (request.AllSeasons ? expected.Keys : episodes.Keys).Where(number => number > 0 && request.Seasons.All(season => season.Number != number)))
        {
            request.Seasons.Add(new MediaRequestSeason { Number = number });
        }

        foreach (MediaRequestSeason season in request.Seasons)
        {
            int count = episodes.GetValueOrDefault(season.Number);
            season.AvailableEpisodes = count;
            season.Availability = count == 0 ? SeasonAvailability.Missing
                : expected.TryGetValue(season.Number, out int total) && total > 0
                    ? count >= total ? SeasonAvailability.Available : SeasonAvailability.Partial
                    : SeasonAvailability.Unknown;
        }

        await db.SaveChangesAsync(ct);
        return details.Status is "Ended" or "Canceled" && request.Seasons.Count > 0
            && request.Seasons.Any(season => request.AllSeasons || season.Requested)
            && request.Seasons.Where(season => request.AllSeasons || season.Requested).All(season => season.Availability == SeasonAvailability.Available);
    }
}
