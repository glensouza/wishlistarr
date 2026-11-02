using Microsoft.EntityFrameworkCore;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Labels;
using Wishlistarr.Api.Library;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;

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
    RequestSeasons seasons,
    RequestDispatch dispatch,
    RequestLabeling labeling,
    RequestLocks locks,
    IProfileLabelGate profileLabels,
    TimeProvider time,
    ILogger<RequestArrival> logger)
{
    public const string Component = "arrival";
    private const int BatchSize = 50;
    private static readonly TimeSpan MissingTitleWindow = TimeSpan.FromHours(24);

    public async Task CheckAsync(CancellationToken ct)
    {
        if (!await this.Pending().AnyAsync(ct))
        {
            return;
        }

        // A monitored show is already in the library, so only requests still waiting for their title need the index brought up to date.
        if (await this.Pending().AnyAsync(request => request.State != RequestState.Monitoring, ct))
        {
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
                catch (MediaServerNotConfiguredException)
                {
                    return;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    this.LogCheckFailed(ex, id);
                }
            }
        }
    }

    private IQueryable<MediaRequest> Pending() => db.MediaRequests.AsNoTracking()
        .Where(request => (request.State == RequestState.Monitoring
                || (request.DispatchedAt != null && (request.State == RequestState.Approved || request.State == RequestState.Fulfilling || request.State == RequestState.Available)))
            && request.KidProfile!.ArchivedAt == null);

    private async Task CheckOneAsync(int id, CancellationToken ct)
    {
        using IDisposable held = await locks.AcquireAsync(id, ct);
        if (!await this.Pending().AnyAsync(row => row.Id == id, ct))
        {
            return;
        }

        MediaRequest? request = await requests.GetAsync(id, ct);
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

        bool complete = request.Kind == MediaKind.Show && await this.TryRefreshSeasonsAsync(request, visible, ct);
        if (request.State == RequestState.Monitoring)
        {
            if (complete)
            {
                await requests.TransitionAsync(id, RequestState.Labeled, RequestActor.System(Component), ct: ct, expectedFrom: RequestState.Monitoring);
            }
            else if (request.DownloadRequested && request.DispatchedAt is null && request.Seasons.Any(season => (request.AllSeasons || season.Requested) && season.Availability != SeasonAvailability.Available))
            {
                await dispatch.StartAsync();
            }

            return;
        }

        if (visible.Count == 0)
        {
            if (request.State == RequestState.Available && time.GetUtcNow() - request.StateChangedAt >= MissingTitleWindow)
            {
                await requests.TransitionAsync(
                    id,
                    RequestState.Failed,
                    RequestActor.System(Component),
                    RequestLabeling.NoLongerInLibraryReason,
                    ct,
                    RequestState.Available);
            }

            return;
        }

        // A rename or archive takes the same gate, so the label written below is the profile's current one.
        using IDisposable gate = await profileLabels.EnterAsync(ct);
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

        RequestLabelResult labeled = await labeling.LabelLockedAsync(id, visible, profile, actor, expectedItems: expectedItems, retryOnConnectionFailure: true);
        if (complete && labeled.Result.Request?.State == RequestState.Monitoring)
        {
            await requests.TransitionAsync(id, RequestState.Labeled, actor, ct: ct, expectedFrom: RequestState.Monitoring);
        }
    }

    private async Task<bool> TryRefreshSeasonsAsync(MediaRequest request, IReadOnlyList<MediaItem> visible, CancellationToken ct)
    {
        try
        {
            return await seasons.RefreshAsync(request, visible, ct);
        }
        catch (TmdbNotConfiguredException ex)
        {
            this.LogSeasonsNotConfigured(ex, request.Id);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is TmdbException or HttpRequestException or TaskCanceledException)
        {
            this.LogSeasonsFailed(ex, request.Id);
        }

        return false;
    }

    // An incremental refresh cannot remove deleted items, and Fix Match can keep a rating key while replacing the title.
    private static bool IsSameTitle(MediaItemLabels current, MediaItem indexed) =>
        current.Kind == indexed.Kind
        && current.Guid == indexed.Guid
        && current.ExternalIds.Any(indexed.ExternalIds.Contains)
        && current.ExternalIds.GroupBy(id => id.Source)
            .All(group => group.Any(indexed.ExternalIds.Contains) || indexed.ExternalIds.All(id => id.Source != group.Key));

    [LoggerMessage(Level = LogLevel.Debug, Message = "TMDB is not configured, so the seasons of request {RequestId} were not checked")]
    private partial void LogSeasonsNotConfigured(Exception ex, int requestId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the seasons of request {RequestId} from TMDB failed; they will be checked again")]
    private partial void LogSeasonsFailed(Exception ex, int requestId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking Plex arrival for request {RequestId} failed; it will be checked again")]
    private partial void LogCheckFailed(Exception ex, int requestId);
}
