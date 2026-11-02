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
    public const string NoLongerInLibraryReason = "The title is no longer in the library.";
    public const string TimeoutReason = "Plex did not answer in time.";
    public const string WriteFailedReason = "The Plex label could not be written.";

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
        string source = LabelSource,
        IReadOnlyDictionary<string, MediaItemLabels>? expectedItems = null,
        bool retryOnConnectionFailure = false)
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
        bool retry = false;
        bool writeSent = false;
        List<string> added = [];
        try
        {
            deadline = new(options.Value.LabelTimeout, time);
            CancellationToken ct = deadline.Token;
            int labeled = 0;
            foreach (MediaItem match in matches)
            {
                MediaItemLabels? expected = null;
                if (expectedItems is not null && !expectedItems.TryGetValue(match.Id, out expected))
                {
                    continue;
                }

                LabelWriteResult written = await labels.AddAsync(match.Id, profile.PlexLabel, profile.Id, source, ct, expected, retryOnConnectionFailure);
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
                failure = NoLongerInLibraryReason;
            }
        }
        catch (Exception ex)
        {
            this.LogLabelFailed(ex, requestId);
            writeSent = ex.Data.Contains(LabelWriter.WriteSentKey);
            failure = deadline?.IsCancellationRequested == true
                ? TimeoutReason
                : WriteFailedReason;
            retry = retryOnConnectionFailure
                && deadline?.IsCancellationRequested != true
                && (ex is HttpRequestException or TaskCanceledException || ex is MediaServerException and not MediaServerItemChangedException);
        }
        finally
        {
            deadline?.Dispose();
        }

        // A retry keeps the labels already added, so the shared ledger row they depend on is still there for the next attempt.
        if (failure is not null && added.Count > 0 && !retry)
        {
            await this.RollBackAsync(requestId, added, profile.PlexLabel, writeSent);
        }

        if (retry)
        {
            return new RequestLabelResult(new RequestResult(RequestOutcome.Unchanged, current, failure), false);
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
    // A copy whose write was sent may carry the label under the same shared ledger row, so that row stays.
    private async Task RollBackAsync(int requestId, IReadOnlyList<string> itemIds, string label, bool keepOwnership)
    {
        using CancellationTokenSource deadline = new(options.Value.LabelTimeout, time);
        foreach (string itemId in itemIds)
        {
            try
            {
                await labels.RemoveAsync(itemId, label, deadline.Token, forgetOwnership: !keepOwnership);
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
