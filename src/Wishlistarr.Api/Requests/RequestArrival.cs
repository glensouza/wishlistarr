using Microsoft.EntityFrameworkCore;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Library;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Requests;

public sealed class RequestArrivalJob(RequestArrival arrival) : IBackgroundJob
{
    public const string JobName = "request-arrival";

    public Task RunAsync(CancellationToken ct) => arrival.CheckAsync(ct);
}

public sealed partial class RequestArrival(
    WishlistarrDbContext db,
    LibraryRefresh refresh,
    LibraryLookup library,
    IMediaServer server,
    MediaRequests requests,
    RequestLabeling labeling,
    RequestLocks locks,
    ILogger<RequestArrival> logger)
{
    public const string Component = "arrival";
    private const int BatchSize = 50;

    public async Task CheckAsync(CancellationToken ct)
    {
        if (!await this.Pending().AnyAsync(ct))
        {
            return;
        }

        try
        {
            await refresh.RetryUnresolvedAsync(ct);
            // A ready index can still predate the download, including the external ids of a legacy agent item.
            await refresh.IncrementalAsync(ct);
        }
        catch (MediaServerNotConfiguredException)
        {
            return;
        }

        int after = 0;
        while (true)
        {
            List<int> ids = await this.Pending()
                .Where(request => request.Id > after)
                .OrderBy(request => request.Id)
                .Select(request => request.Id)
                .Take(BatchSize)
                .ToListAsync(ct);
            if (ids.Count == 0)
            {
                return;
            }

            foreach (int id in ids)
            {
                after = id;
                try
                {
                    await this.CheckOneAsync(id, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    this.LogCheckFailed(ex, id);
                }
            }
        }
    }

    private IQueryable<MediaRequest> Pending() => db.MediaRequests.AsNoTracking()
        .Where(request => request.DispatchedAt != null
            && (request.State == RequestState.Approved || request.State == RequestState.Fulfilling || request.State == RequestState.Available)
            && request.KidProfile!.ArchivedAt == null);

    private async Task CheckOneAsync(int id, CancellationToken ct)
    {
        using IDisposable held = await locks.AcquireAsync(id, ct);
        MediaRequest? request = await this.Pending().SingleOrDefaultAsync(row => row.Id == id, ct);
        if (request is null)
        {
            return;
        }

        List<MediaItem> visible = [];
        Dictionary<string, MediaItemLabels> expectedItems = [];
        foreach (MediaItem match in await library.FindAsync(new TitleKey(request.Kind, request.TmdbId), ct))
        {
            MediaItemLabels? current = await server.GetLabelsAsync(match.Id, ct);
            if (current is not null && IsSameTitle(current, match))
            {
                visible.Add(match);
                // Keep the resolved TMDB id as well as Plex's ids, so a later contradictory TMDB id cannot pass a legacy-only snapshot.
                expectedItems[match.Id] = current with { ExternalIds = [.. match.ExternalIds.Concat(current.ExternalIds).Distinct()] };
            }
        }

        if (visible.Count == 0)
        {
            return;
        }

        KidProfile? profile = await db.KidProfiles.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == request.KidProfileId && row.ArchivedAt == null, ct);
        if (profile is null)
        {
            return;
        }

        RequestActor actor = RequestActor.System(Component);
        if (request.State != RequestState.Available)
        {
            RequestResult available = await requests.TransitionAsync(id, RequestState.Available, actor, ct: ct, expectedFrom: request.State);
            if (available.Outcome != RequestOutcome.Changed)
            {
                return;
            }
        }

        await labeling.LabelLockedAsync(id, visible, profile, actor, expectedItems: expectedItems);
    }

    // An incremental refresh cannot remove deleted items, and Fix Match can keep a rating key while replacing the title.
    private static bool IsSameTitle(MediaItemLabels current, MediaItem indexed) =>
        current.Kind == indexed.Kind
        && current.Guid == indexed.Guid
        && current.ExternalIds.Any(indexed.ExternalIds.Contains)
        && current.ExternalIds.GroupBy(id => id.Source)
            .All(group => group.Any(indexed.ExternalIds.Contains) || indexed.ExternalIds.All(id => id.Source != group.Key));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking Plex arrival for request {RequestId} failed; it will be checked again")]
    private partial void LogCheckFailed(Exception ex, int requestId);
}
