using Microsoft.EntityFrameworkCore;
using Wishlistarr.Api.Fulfilment;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Core.Fulfilment;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Arr;

namespace Wishlistarr.Api.Requests;

public sealed class RequestDispatchJob(RequestDispatch dispatch) : IBackgroundJob
{
    public const string JobName = "request-dispatch";

    public Task RunAsync(CancellationToken ct) => dispatch.DispatchPendingAsync(ct);
}

public sealed partial class RequestDispatch(
    WishlistarrDbContext db,
    MediaRequests requests,
    FulfilmentMode mode,
    ISeerrBackend seerr,
    ArrBackends arr,
    JobRunner runner,
    TimeProvider time,
    ILogger<RequestDispatch> logger)
{
    public const string Component = "dispatch";
    public const string NotConfiguredReason = "The download service is not set up.";
    public const string UnavailableReason = "The download service did not accept the request.";
    public const string NotConfiguredCode = "notConfigured";
    public const string UnavailableCode = "unavailable";

    private const int BatchSize = 50;

    public static bool IsRetryable(MediaRequestEvent? failure) =>
        failure?.FromState is RequestState from && from != RequestState.Available;

    public static string? FailureCode(MediaRequestEvent? failure) => failure?.Reason switch
    {
        NotConfiguredReason => NotConfiguredCode,
        UnavailableReason => UnavailableCode,
        _ => null,
    };

    public async Task StartAsync()
    {
        try
        {
            await runner.StartAsync(RequestDispatchJob.JobName, JobRunTrigger.Request);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.LogStartFailed(ex);
        }
    }

    public async Task DispatchPendingAsync(CancellationToken ct)
    {
        HashSet<int> seen = [];
        while (true)
        {
            int[] skip = [.. seen];
            List<int> ids = await db.MediaRequests.AsNoTracking()
                .Where(request => !skip.Contains(request.Id)
                    && (request.State == RequestState.Approved
                        || (request.State == RequestState.Fulfilling && request.DispatchedAt == null)))
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
                seen.Add(id);
                await this.DispatchOneAsync(id, ct);
            }
        }
    }

    public async Task<IReadOnlyList<DecisionItem>> RetryManyAsync(
        IReadOnlyList<int> ids,
        RequestActor actor,
        string? reason,
        CancellationToken ct = default)
    {
        if (actor.Kind != RequestActorKind.Parent)
        {
            throw new ArgumentException("Only a parent can retry a request.", nameof(actor));
        }

        List<DecisionItem> results = new(ids.Count);
        foreach (int id in ids)
        {
            results.Add(await this.RetryOneAsync(id, actor, reason, ct));
        }

        if (results.Any(item => item.Status == DecisionStatus.Succeeded))
        {
            await this.StartAsync();
        }

        return results;
    }

    private async Task<DecisionItem> RetryOneAsync(int id, RequestActor actor, string? reason, CancellationToken ct)
    {
        try
        {
            MediaRequest? request = await requests.GetAsync(id, ct);
            if (request is null)
            {
                return new DecisionItem(id, DecisionStatus.NotFound, null);
            }

            if (request.State != RequestState.Failed)
            {
                bool sent = request.State is RequestState.Approved or RequestState.Fulfilling;
                return new DecisionItem(id, sent ? DecisionStatus.AlreadyDecided : DecisionStatus.Conflict, request.State);
            }

            MediaRequestEvent? failure = (await RequestViews.LastFailuresAsync(db, [id], ct)).GetValueOrDefault(id);
            if (!IsRetryable(failure))
            {
                return new DecisionItem(id, DecisionStatus.Conflict, request.State);
            }

            RequestResult result = await requests.RetryAsync(id, actor, reason, ct);
            return result.Outcome switch
            {
                RequestOutcome.Changed => new DecisionItem(id, DecisionStatus.Succeeded, result.Request!.State),
                RequestOutcome.Unchanged => new DecisionItem(id, DecisionStatus.AlreadyDecided, result.Request!.State),
                RequestOutcome.NotFound => new DecisionItem(id, DecisionStatus.NotFound, null),
                _ => new DecisionItem(id, DecisionStatus.Conflict, result.Request?.State),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.LogRetryFailed(ex, id);
            return new DecisionItem(id, DecisionStatus.Failed, null);
        }
    }

    private async Task DispatchOneAsync(int id, CancellationToken ct)
    {
        MediaRequest? request = await this.LoadDispatchableAsync(id, ct);
        if (request is null)
        {
            return;
        }

        FulfilmentBackend? backend = request.Backend ?? await mode.GetAsync(ct);
        if (backend is not FulfilmentBackend chosen)
        {
            return;
        }

        if (request.Backend is null)
        {
            await db.MediaRequests
                .Where(row => row.Id == id && row.Backend == null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.Backend, chosen)
                        .SetProperty(row => row.Version, row => row.Version + 1),
                    ct);
            request = await this.LoadDispatchableAsync(id, ct);
            if (request is null)
            {
                return;
            }

            chosen = request.Backend ?? chosen;
        }

        int? externalId;
        try
        {
            externalId = await this.SendAsync(chosen, request.Kind, request.TmdbId, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            this.LogDispatchFailed(ex, id, chosen);
            await requests.TransitionAsync(
                id,
                RequestState.Failed,
                RequestActor.System(Component),
                ex is SeerrNotConfiguredException or ArrNotConfiguredException ? NotConfiguredReason : UnavailableReason,
                CancellationToken.None);
            return;
        }

        // The title is with the backend before this is saved. If the process stops in between, the request is still approved and the next run finds the title there instead of adding it again.
        DateTimeOffset now = time.GetUtcNow();
        await db.MediaRequests
            .Where(row => row.Id == id && (row.State == RequestState.Approved || row.State == RequestState.Fulfilling))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.DispatchedAt, now)
                    .SetProperty(row => row.ExternalId, row => externalId ?? row.ExternalId)
                    .SetProperty(row => row.Version, row => row.Version + 1),
                CancellationToken.None);
        await requests.TransitionAsync(id, RequestState.Fulfilling, RequestActor.System(Component), ct: CancellationToken.None, expectedFrom: request.State);
    }

    private async Task<MediaRequest?> LoadDispatchableAsync(int id, CancellationToken ct) =>
        await db.MediaRequests.AsNoTracking().FirstOrDefaultAsync(
            row => row.Id == id
                && (row.State == RequestState.Approved || (row.State == RequestState.Fulfilling && row.DispatchedAt == null)),
            ct);

    private async Task<int?> SendAsync(FulfilmentBackend backend, MediaKind kind, int tmdbId, CancellationToken ct)
    {
        if (backend == FulfilmentBackend.Seerr)
        {
            SeerrSubmission submitted = await seerr.SubmitAsync(new SeerrSubmitRequest(kind, tmdbId, null), ct);
            return submitted.Id;
        }

        ArrService service = kind == MediaKind.Show ? ArrService.Sonarr : ArrService.Radarr;
        FulfilmentResult result = await arr.Get(service).FulfilAsync(new FulfilmentRequest(kind, tmdbId, []), ct);
        return result.ArrId > 0 ? result.ArrId : null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sending request {RequestId} to {Backend} failed")]
    private partial void LogDispatchFailed(Exception ex, int requestId, FulfilmentBackend backend);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retrying request {RequestId} failed")]
    private partial void LogRetryFailed(Exception ex, int requestId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The request dispatch job could not be started")]
    private partial void LogStartFailed(Exception ex);
}
