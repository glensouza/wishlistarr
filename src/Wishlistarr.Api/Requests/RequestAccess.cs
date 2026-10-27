using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Wishlistarr.Api.Labels;
using Wishlistarr.Api.Library;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.Library;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Requests;

public enum GrantStatus
{
    Granted,
    AlreadyGranted,
    ProfileNotFound,
    TitleNotFound,
    NotInLibrary,
    Conflict,
    InProgress,
    Unavailable,
}

public sealed record GrantItem(GrantStatus Status, MediaRequest? Request, bool LabelNotOwned = false);

public enum GrantPreviewStatus
{
    Found,
    TitleNotFound,
}

public sealed record GrantPreview(GrantPreviewStatus Status, string? Title = null, bool? InLibrary = null);

public sealed record RevokeItem(int Id, DecisionStatus Status, RequestState? State, bool LabelNotOwned = false, bool LabelInUse = false);

public sealed partial class RequestAccess(
    WishlistarrDbContext db,
    MediaRequests requests,
    RequestTitles titles,
    LibraryLookup library,
    LabelWriter labels,
    RequestLabeling labeling,
    RequestLocks locks,
    IOptions<ApprovalOptions> options,
    TimeProvider time,
    ILogger<RequestAccess> logger)
{
    public const string GrantReason = "Parent grant";
    public const string GrantSource = "grant";
    public const string GrantComponent = "grant";

    private static readonly StringComparer LabelComparer = StringComparer.OrdinalIgnoreCase;

    public async Task<GrantPreview> PreviewAsync(TitleKey title, CancellationToken ct = default)
    {
        if (await titles.FetchAsync(title.Kind, title.TmdbId, ct) is not (string name, _))
        {
            return new GrantPreview(GrantPreviewStatus.TitleNotFound);
        }

        try
        {
            return new GrantPreview(GrantPreviewStatus.Found, name, (await library.FindAsync(title, ct)).Count > 0);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is MediaServerException or HttpRequestException or TaskCanceledException)
        {
            this.LogPreviewLookupFailed(ex, title.Kind, title.TmdbId);
            return new GrantPreview(GrantPreviewStatus.Found, name);
        }
    }

    public async Task<GrantItem> GrantAsync(int kidProfileId, TitleKey title, RequestActor actor, CancellationToken ct = default)
    {
        if (actor.Kind != RequestActorKind.Parent)
        {
            throw new ArgumentException("Only a parent can grant a title.", nameof(actor));
        }

        KidProfile? profile = await db.KidProfiles.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == kidProfileId && row.ArchivedAt == null, ct);
        if (profile is null)
        {
            return new GrantItem(GrantStatus.ProfileNotFound, null);
        }

        if (await titles.FetchAsync(title.Kind, title.TmdbId, ct) is not (string name, _))
        {
            return new GrantItem(GrantStatus.TitleNotFound, null);
        }

        IReadOnlyList<MediaItem> items;
        try
        {
            items = await library.FindAsync(title, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is MediaServerException or HttpRequestException or TaskCanceledException)
        {
            this.LogGrantLookupFailed(ex, kidProfileId, title.Kind, title.TmdbId);
            return new GrantItem(GrantStatus.Unavailable, null);
        }

        if (items.Count == 0)
        {
            return new GrantItem(GrantStatus.NotInLibrary, null);
        }

        MediaRequest? open = await this.FindOpenAsync(kidProfileId, title, ct);
        if (open is null)
        {
            // A request the kid opened in the meantime comes back untouched, so a grant never counts or renames it.
            RequestResult created = await requests.CreateAsync(
                profile.Id,
                title,
                name,
                actor,
                countsTowardAllowance: false,
                ct: ct,
                reason: GrantReason,
                mergeExisting: false);
            if (created is not { Outcome: RequestOutcome.Created or RequestOutcome.Unchanged, Request: MediaRequest stored })
            {
                return new GrantItem(GrantStatus.ProfileNotFound, null);
            }

            open = stored;
        }

        if (open.State == RequestState.Labeled)
        {
            return new GrantItem(GrantStatus.AlreadyGranted, open);
        }

        if (open.State is not (RequestState.Pending or RequestState.Approved or RequestState.Available or RequestState.Failed)
            || !await this.IsParentGrantAsync(open.Id, ct))
        {
            return new GrantItem(GrantStatus.Conflict, open);
        }

        return await this.FinishGrantAsync(open, profile, items, actor, ct);
    }

    public async Task<IReadOnlyList<RevokeItem>> RevokeManyAsync(
        IReadOnlyList<int> ids,
        RequestActor actor,
        string? reason,
        CancellationToken ct = default)
    {
        if (actor.Kind != RequestActorKind.Parent)
        {
            throw new ArgumentException("Only a parent can revoke a title.", nameof(actor));
        }

        List<RevokeItem> results = new(ids.Count);
        foreach (int id in ids)
        {
            results.Add(await this.RevokeOneAsync(id, actor, reason, ct));
        }

        return results;
    }

    public async Task<IReadOnlySet<int>> RevocableAsync(IReadOnlyList<MediaRequest> rows, CancellationToken ct = default)
    {
        HashSet<int> revocable = [.. rows
            .Where(request => request.State == RequestState.Labeled || (request.State == RequestState.Available && this.IsStuck(request)))
            .Select(request => request.Id)];
        List<MediaRequest> failed = [.. rows.Where(request => request.State == RequestState.Failed)];
        if (failed.Count > 0)
        {
            revocable.UnionWith(await this.FailedWithAppLabelsAsync(failed, ct));
        }

        return revocable;
    }

    public async Task<int> CountUnfinishedAsync(CancellationToken ct = default)
    {
        IQueryable<MediaRequest> listed = db.MediaRequests.AsNoTracking()
            .Where(request => db.KidProfiles.Any(profile => profile.Id == request.KidProfileId));
        int failed = await listed.CountAsync(request => request.State == RequestState.Failed, ct);
        List<MediaRequest> available = await listed.Where(request => request.State == RequestState.Available).ToListAsync(ct);
        return failed + available.Count(this.IsStuck);
    }

    private async Task<RevokeItem> RevokeOneAsync(int id, RequestActor actor, string? reason, CancellationToken ct)
    {
        MediaRequest? seen = await requests.GetAsync(id, ct);
        if (this.Settle(id, seen) is RevokeItem early)
        {
            return early;
        }

        KidProfile? profile = await db.KidProfiles.AsNoTracking().FirstOrDefaultAsync(row => row.Id == seen!.KidProfileId, ct);
        if (profile is null)
        {
            return new RevokeItem(id, DecisionStatus.NotFound, null);
        }

        IReadOnlyList<MediaItem> items;
        try
        {
            items = await library.FindAsync(new TitleKey(seen!.Kind, seen.TmdbId), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is MediaServerException or HttpRequestException or TaskCanceledException)
        {
            this.LogRevokeFailed(ex, id);
            return new RevokeItem(id, DecisionStatus.Failed, seen!.State);
        }

        if (items.Count == 0)
        {
            return new RevokeItem(id, DecisionStatus.Failed, seen.State);
        }

        using IDisposable held = await locks.AcquireAsync(id, ct);
        MediaRequest? request = await requests.GetAsync(id, ct);
        if (this.Settle(id, request) is RevokeItem settled)
        {
            return settled;
        }

        return await this.RevokeLockedAsync(request!, profile, items, actor, reason);
    }

    // A failed request has finished labeling and a stuck one has no label write left, so both can be revoked like a labeled one.
    private RevokeItem? Settle(int id, MediaRequest? request)
    {
        if (request is null)
        {
            return new RevokeItem(id, DecisionStatus.NotFound, null);
        }

        if (request.State == RequestState.Revoked)
        {
            return new RevokeItem(id, DecisionStatus.AlreadyDecided, request.State);
        }

        bool revocable = request.State switch
        {
            RequestState.Labeled or RequestState.Failed => true,
            RequestState.Available => this.IsStuck(request),
            _ => false,
        };
        return revocable ? null : new RevokeItem(id, DecisionStatus.Conflict, request.State);
    }

    private bool IsStuck(MediaRequest request) =>
        time.GetUtcNow() - request.StateChangedAt >= options.Value.StuckAfter;

    private async Task<RevokeItem> RevokeLockedAsync(
        MediaRequest request,
        KidProfile profile,
        IReadOnlyList<MediaItem> items,
        RequestActor actor,
        string? reason)
    {
        int id = request.Id;
        RequestState from = request.State;

        // Once a label is removed, the request has to be marked revoked or the label put back, so the rest ignores the caller going away but not the deadline.
        CancellationToken none = CancellationToken.None;
        CancellationTokenSource? deadline = null;
        List<RemovedLabel> removed = [];
        RemovedLabel? inFlight = null;
        bool notOwned = false;
        bool inUse = false;
        try
        {
            deadline = new(options.Value.LabelTimeout, time);
            CancellationToken ct = deadline.Token;
            HashSet<string> taken = await this.LabelsOfOtherProfilesAsync(profile.Id, ct);
            foreach (MediaItem item in items)
            {
                if (await labels.FindProfileLabelsAsync(item.Id, profile.Id, ct) is not ProfileLabels found)
                {
                    continue;
                }

                foreach (OwnedLabel owned in found.Owned)
                {
                    // An archived profile's label can belong to a newer profile by now, and that profile still relies on it.
                    if (taken.Contains(owned.Label))
                    {
                        inUse = true;
                        continue;
                    }

                    // A removal that throws may still have reached Plex, so a label that was there is put back like one that was removed.
                    RemovedLabel attempt = new(item.Id, owned.Label, owned.Source);
                    inFlight = found.Item.Labels.Contains(owned.Label, LabelComparer) ? attempt : null;
                    LabelWriteResult result = await labels.RemoveAsync(item.Id, owned.Label, ct);
                    inFlight = null;
                    if (result.Outcome == LabelWriteOutcome.Removed)
                    {
                        removed.Add(attempt);
                    }
                    else if (!IsGone(result, owned.Label))
                    {
                        return await this.UndoRevokeAsync(id, from, profile.Id, removed, DecisionStatus.Failed);
                    }
                }

                if (found.Item.Labels.Contains(profile.PlexLabel, LabelComparer)
                    && !found.Owned.Any(owned => LabelComparer.Equals(owned.Label, profile.PlexLabel))
                    && !taken.Contains(profile.PlexLabel))
                {
                    notOwned = true;
                }
            }
        }
        catch (Exception ex) when (ex is MediaServerException or HttpRequestException or OperationCanceledException)
        {
            this.LogRevokeFailed(ex, id);
            if (inFlight is not null)
            {
                removed.Add(inFlight);
            }

            return await this.UndoRevokeAsync(id, from, profile.Id, removed, DecisionStatus.Failed);
        }
        finally
        {
            deadline?.Dispose();
        }

        RequestResult moved;
        try
        {
            moved = await requests.TransitionAsync(id, RequestState.Revoked, actor, reason, none, from);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            this.LogRevokeFailed(ex, id);
            return await this.UndoRevokeAsync(id, from, profile.Id, removed, DecisionStatus.Conflict);
        }

        return moved.Outcome switch
        {
            RequestOutcome.Changed => new RevokeItem(id, DecisionStatus.Succeeded, RequestState.Revoked, notOwned, inUse),
            RequestOutcome.Unchanged => new RevokeItem(id, DecisionStatus.AlreadyDecided, RequestState.Revoked, notOwned, inUse),
            RequestOutcome.NotFound => new RevokeItem(id, DecisionStatus.NotFound, null),
            _ => await this.UndoRevokeAsync(id, from, profile.Id, removed, DecisionStatus.Conflict),
        };
    }

    private async Task<RevokeItem> UndoRevokeAsync(
        int id,
        RequestState from,
        int profileId,
        IReadOnlyList<RemovedLabel> removed,
        DecisionStatus status)
    {
        CancellationTokenSource? deadline = null;
        try
        {
            // A request that has moved on, for example to revoked, must not get its labels back.
            MediaRequest? current = await requests.GetAsync(id, CancellationToken.None);
            if (current?.State != from)
            {
                if (removed.Count > 0)
                {
                    this.LogRevokeUndoSkipped(id, current?.State);
                }

                return new RevokeItem(id, status, current?.State);
            }

            deadline = new(options.Value.LabelTimeout, time);
            foreach (RemovedLabel label in removed)
            {
                await labels.AddAsync(label.ItemId, label.Label, profileId, label.Source, deadline.Token);
            }

            return new RevokeItem(id, status, from);
        }
        catch (Exception ex) when (ex is MediaServerException or HttpRequestException or OperationCanceledException or DbUpdateException)
        {
            this.LogRevokeUndoFailed(ex, id);
            return new RevokeItem(id, DecisionStatus.Failed, from);
        }
        finally
        {
            deadline?.Dispose();
        }
    }

    private async Task<GrantItem> FinishGrantAsync(
        MediaRequest seen,
        KidProfile profile,
        IReadOnlyList<MediaItem> items,
        RequestActor parent,
        CancellationToken ct)
    {
        using IDisposable held = await locks.AcquireAsync(seen.Id, ct);
        MediaRequest? request = await requests.GetAsync(seen.Id, ct);
        if (request is null)
        {
            return new GrantItem(GrantStatus.Conflict, seen);
        }

        // The approval and the move to available are saved together, so the dispatch job never sees the request while it is only approved and sends a title that is already in the library.
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct);
        if (request.State == RequestState.Pending)
        {
            RequestResult approved = await requests.TransitionAsync(request.Id, RequestState.Approved, parent, GrantReason, ct, RequestState.Pending);
            if (approved is not { Outcome: RequestOutcome.Changed, Request: MediaRequest next })
            {
                return Settled(approved, request);
            }

            request = next;
        }

        // Only a request this call moves to available is labeled here. One that is already available has a label write of its own that has not finished.
        if (request.State is not (RequestState.Approved or RequestState.Failed))
        {
            return request.State switch
            {
                RequestState.Labeled => new GrantItem(GrantStatus.AlreadyGranted, request),
                RequestState.Available => new GrantItem(GrantStatus.InProgress, request),
                _ => new GrantItem(GrantStatus.Conflict, request),
            };
        }

        // The parent's decision is stored, so the move to available and the label finish even if the caller goes away.
        RequestActor system = RequestActor.System(GrantComponent);
        RequestResult available = await requests.TransitionAsync(
            request.Id,
            RequestState.Available,
            system,
            ct: CancellationToken.None,
            expectedFrom: request.State);
        if (available is not { Outcome: RequestOutcome.Changed, Request: MediaRequest moved })
        {
            return Settled(available, request);
        }

        await transaction.CommitAsync(CancellationToken.None);

        RequestLabelResult labeled = await labeling.LabelLockedAsync(moved.Id, items, profile, system, GrantSource);
        return labeled.Result is { Request.State: RequestState.Labeled, Request: MediaRequest done }
            ? new GrantItem(GrantStatus.Granted, done, labeled.LabelNotOwned)
            : new GrantItem(GrantStatus.Unavailable, labeled.Result.Request ?? moved);
    }

    // Before the index can answer, no failed request is offered: the list would otherwise wait on Plex once per row.
    private async Task<IEnumerable<int>> FailedWithAppLabelsAsync(IReadOnlyList<MediaRequest> failed, CancellationToken ct)
    {
        IndexedGuids? indexed = await library.FindIndexedGuidsAsync(
            [.. failed.Select(request => new TitleKey(request.Kind, request.TmdbId)).Distinct()],
            ct);
        if (indexed is null || indexed.Guids.Count == 0)
        {
            return [];
        }

        string serverId = indexed.ServerId;
        string[] guids = [.. indexed.Guids.Values.SelectMany(list => list).Distinct(StringComparer.Ordinal)];
        int[] profiles = [.. failed.Select(request => request.KidProfileId).Distinct()];
        var owned = await db.OwnedLabels.AsNoTracking()
            .Where(row => row.ServerId == serverId && profiles.Contains(row.KidProfileId) && guids.Contains(row.Guid))
            .Select(row => new { row.KidProfileId, row.Guid })
            .Distinct()
            .ToListAsync(ct);
        HashSet<(int, string)> held = [.. owned.Select(row => (row.KidProfileId, row.Guid))];
        return failed
            .Where(request => indexed.Guids.TryGetValue(new TitleKey(request.Kind, request.TmdbId), out IReadOnlyList<string>? copies)
                && copies.Any(guid => held.Contains((request.KidProfileId, guid))))
            .Select(request => request.Id);
    }

    private async Task<HashSet<string>> LabelsOfOtherProfilesAsync(int profileId, CancellationToken ct)
    {
        List<string> active = await db.KidProfiles.AsNoTracking()
            .Where(row => row.ArchivedAt == null && row.Id != profileId)
            .Select(row => row.PlexLabel)
            .ToListAsync(ct);
        return active.ToHashSet(LabelComparer);
    }

    // A concurrent revoke can remove the label and its ledger row between the read and this removal, which leaves nothing to undo.
    private static bool IsGone(LabelWriteResult result, string label) => result.Outcome switch
    {
        LabelWriteOutcome.NotPresent or LabelWriteOutcome.ItemNotFound => true,
        LabelWriteOutcome.NotOwned => result.Item is { } item && !item.Labels.Contains(label, LabelComparer),
        _ => false,
    };

    private static GrantItem Settled(RequestResult result, MediaRequest request) => result.Request?.State switch
    {
        RequestState.Labeled => new GrantItem(GrantStatus.AlreadyGranted, result.Request),
        null => new GrantItem(GrantStatus.Conflict, request),
        _ => new GrantItem(GrantStatus.Conflict, result.Request),
    };

    private Task<MediaRequest?> FindOpenAsync(int kidProfileId, TitleKey title, CancellationToken ct) =>
        db.MediaRequests.AsNoTracking().FirstOrDefaultAsync(
            request => request.KidProfileId == kidProfileId
                && request.Kind == title.Kind
                && request.TmdbId == title.TmdbId
                && request.State != RequestState.Denied
                && request.State != RequestState.Revoked,
            ct);

    private async Task<bool> IsParentGrantAsync(int requestId, CancellationToken ct)
    {
        MediaRequestEvent? created = await db.MediaRequestEvents.AsNoTracking()
            .Where(item => item.MediaRequestId == requestId && item.FromState == null)
            .OrderBy(item => item.Id)
            .FirstOrDefaultAsync(ct);
        return created is { ActorKind: RequestActorKind.Parent, Reason: GrantReason };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Library lookup for a grant to profile {ProfileId} of {Kind} {TmdbId} failed")]
    private partial void LogGrantLookupFailed(Exception ex, int profileId, MediaKind kind, int tmdbId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Library lookup for a grant preview of {Kind} {TmdbId} failed")]
    private partial void LogPreviewLookupFailed(Exception ex, MediaKind kind, int tmdbId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Revoke for request {RequestId} failed before the request was marked revoked")]
    private partial void LogRevokeFailed(Exception ex, int requestId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Revoke for request {RequestId} did not put its labels back because the request is now {State}")]
    private partial void LogRevokeUndoSkipped(int requestId, RequestState? state);

    [LoggerMessage(Level = LogLevel.Error, Message = "Revoke for request {RequestId} removed a label but could neither mark the request revoked nor put the label back; the request keeps its state and revoking it again removes what is left")]
    private partial void LogRevokeUndoFailed(Exception ex, int requestId);

    private sealed record RemovedLabel(string ItemId, string Label, string Source);
}
