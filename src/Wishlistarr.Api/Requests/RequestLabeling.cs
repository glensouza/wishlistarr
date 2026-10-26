using Microsoft.Extensions.Options;
using Wishlistarr.Api.Labels;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Requests;

public sealed record RequestLabelResult(RequestResult Result, bool LabelNotOwned);

public sealed partial class RequestLabeling(
    MediaRequests requests,
    LabelWriter labels,
    RequestLocks locks,
    IOptions<ApprovalOptions> options,
    TimeProvider time,
    ILogger<RequestLabeling> logger)
{
    public const string LabelSource = "request";
    public const string StoppedReason = "Labeling was stopped before it started.";

    public async Task<RequestLabelResult> LabelAsync(
        int requestId,
        IReadOnlyList<MediaItem> matches,
        KidProfile profile,
        RequestActor actor,
        CancellationToken ct = default)
    {
        IDisposable held;
        try
        {
            held = await locks.AcquireAsync(requestId, ct);
        }
        catch (OperationCanceledException)
        {
            // The request is already stored as available, so a caller that gives up waiting ends it as failed instead of leaving it there until it counts as stuck.
            RequestResult stopped = await requests.TransitionAsync(
                requestId,
                RequestState.Failed,
                actor,
                StoppedReason,
                CancellationToken.None,
                RequestState.Available);
            return new RequestLabelResult(stopped, false);
        }

        using (held)
        {
            return await this.LabelLockedAsync(requestId, matches, profile, actor);
        }
    }

    // The caller holds the request lock, so no revoke or other label write for this request runs at the same time.
    public async Task<RequestLabelResult> LabelLockedAsync(
        int requestId,
        IReadOnlyList<MediaItem> matches,
        KidProfile profile,
        RequestActor actor,
        string source = LabelSource)
    {
        MediaRequest? current = await requests.GetAsync(requestId, CancellationToken.None);
        if (current is not { State: RequestState.Available })
        {
            return new RequestLabelResult(
                current is null
                    ? new RequestResult(RequestOutcome.NotFound, Error: "Request not found.")
                    : new RequestResult(RequestOutcome.Rejected, current, $"The request is {current.State}, not {RequestState.Available}."),
                false);
        }

        // The request is already stored as available, so the caller going away does not stop labeling, but the deadline does.
        CancellationTokenSource? deadline = null;
        string? failure = null;
        bool notOwned = false;
        List<string> added = [];
        try
        {
            deadline = new(options.Value.LabelTimeout, time);
            CancellationToken ct = deadline.Token;
            int labeled = 0;
            foreach (MediaItem match in matches)
            {
                LabelWriteResult written = await labels.AddAsync(match.Id, profile.PlexLabel, profile.Id, source, ct);
                if (written.Outcome == LabelWriteOutcome.Added)
                {
                    added.Add(match.Id);
                }

                if (written.Outcome is LabelWriteOutcome.Added or LabelWriteOutcome.AlreadyPresent)
                {
                    labeled++;
                }

                if (written is { Outcome: LabelWriteOutcome.AlreadyPresent, Item: MediaItemLabels item }
                    && !await labels.OwnsAsync(item, profile.PlexLabel, ct))
                {
                    notOwned = true;
                }
            }

            if (labeled == 0)
            {
                failure = "The title is no longer in the library.";
            }
        }
        catch (Exception ex)
        {
            this.LogLabelFailed(ex, requestId);
            failure = deadline?.IsCancellationRequested == true
                ? "Plex did not answer in time."
                : "The Plex label could not be written.";
        }
        finally
        {
            deadline?.Dispose();
        }

        if (failure is not null && added.Count > 0)
        {
            await this.RollBackAsync(requestId, added, profile.PlexLabel);
        }

        RequestResult moved = await requests.TransitionAsync(
            requestId,
            failure is null ? RequestState.Labeled : RequestState.Failed,
            actor,
            failure,
            CancellationToken.None,
            RequestState.Available);
        return new RequestLabelResult(moved, failure is null && notOwned);
    }

    // A failed request should not leave the kid with some copies of the title, so labels this call added come off again.
    private async Task RollBackAsync(int requestId, IReadOnlyList<string> itemIds, string label)
    {
        using CancellationTokenSource deadline = new(options.Value.LabelTimeout, time);
        foreach (string itemId in itemIds)
        {
            try
            {
                await labels.RemoveAsync(itemId, label, deadline.Token);
            }
            catch (Exception ex)
            {
                this.LogRollBackFailed(ex, requestId, itemId);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Label for request {RequestId} failed")]
    private partial void LogLabelFailed(Exception ex, int requestId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Label for request {RequestId} failed and the label added to item {ItemId} could not be removed again; it stays in the ledger, so revoking the request removes it")]
    private partial void LogRollBackFailed(Exception ex, int requestId, string itemId);
}
