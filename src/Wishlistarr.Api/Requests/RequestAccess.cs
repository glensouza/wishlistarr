using Microsoft.EntityFrameworkCore;
using Wishlistarr.Api.Labels;
using Wishlistarr.Api.Library;
using Wishlistarr.Core.Content;
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
    Unavailable,
}

public sealed record GrantItem(GrantStatus Status, MediaRequest? Request, bool LabelNotOwned = false);

public enum GrantPreviewStatus
{
    Found,
    TitleNotFound,
}

public sealed record GrantPreview(GrantPreviewStatus Status, string? Title = null, bool? InLibrary = null);

public sealed record RevokeItem(int Id, DecisionStatus Status, RequestState? State, bool LabelNotOwned = false);

public sealed partial class RequestAccess(
    WishlistarrDbContext db,
    MediaRequests requests,
    RequestTitles titles,
    LibraryLookup library,
    LabelWriter labels,
    RequestLabeling labeling,
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
            RequestResult created = await requests.CreateAsync(
                profile.Id,
                title,
                name,
                actor,
                countsTowardAllowance: false,
                ct: ct,
                reason: GrantReason);
            if (created is not { Outcome: RequestOutcome.Created or RequestOutcome.Merged, Request: MediaRequest stored })
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

    private async Task<RevokeItem> RevokeOneAsync(int id, RequestActor actor, string? reason, CancellationToken ct)
    {
        MediaRequest? request = await requests.GetAsync(id, ct);
        if (request is null)
        {
            return new RevokeItem(id, DecisionStatus.NotFound, null);
        }

        if (request.State == RequestState.Revoked)
        {
            return new RevokeItem(id, DecisionStatus.AlreadyDecided, request.State);
        }

        // Only a labeled request is revoked. Earlier states can still have a label write in flight that would land after the removal.
        if (request.State != RequestState.Labeled)
        {
            return new RevokeItem(id, DecisionStatus.Conflict, request.State);
        }

        KidProfile? profile = await db.KidProfiles.AsNoTracking().FirstOrDefaultAsync(row => row.Id == request.KidProfileId, ct);
        if (profile is null)
        {
            return new RevokeItem(id, DecisionStatus.NotFound, null);
        }

        IReadOnlyList<MediaItem> items;
        try
        {
            items = await library.FindAsync(new TitleKey(request.Kind, request.TmdbId), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is MediaServerException or HttpRequestException or TaskCanceledException)
        {
            this.LogRevokeFailed(ex, id);
            return new RevokeItem(id, DecisionStatus.Failed, request.State);
        }

        if (items.Count == 0)
        {
            return new RevokeItem(id, DecisionStatus.Failed, request.State);
        }

        // Once a label is removed, the request has to be marked revoked or the label put back, so the rest ignores the caller going away.
        CancellationToken none = CancellationToken.None;
        List<RemovedLabel> removed = [];
        bool notOwned = false;
        try
        {
            foreach (MediaItem item in items)
            {
                if (await labels.FindProfileLabelsAsync(item.Id, profile.Id, none) is not ProfileLabels found)
                {
                    continue;
                }

                foreach (OwnedLabel owned in found.Owned)
                {
                    LabelWriteResult result = await labels.RemoveAsync(item.Id, owned.Label, none);
                    if (result.Outcome == LabelWriteOutcome.Removed)
                    {
                        removed.Add(new RemovedLabel(item.Id, owned.Label, owned.Source));
                    }
                    else if (!IsGone(result, owned.Label))
                    {
                        return await this.UndoRevokeAsync(id, profile.Id, removed, DecisionStatus.Failed, request.State);
                    }
                }

                if (found.Item.Labels.Contains(profile.PlexLabel, LabelComparer)
                    && !found.Owned.Any(owned => LabelComparer.Equals(owned.Label, profile.PlexLabel)))
                {
                    notOwned = true;
                }
            }
        }
        catch (Exception ex) when (ex is MediaServerException or HttpRequestException or TaskCanceledException)
        {
            this.LogRevokeFailed(ex, id);
            return await this.UndoRevokeAsync(id, profile.Id, removed, DecisionStatus.Failed, request.State);
        }

        RequestResult moved;
        try
        {
            moved = await requests.TransitionAsync(id, RequestState.Revoked, actor, reason, none, RequestState.Labeled);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            this.LogRevokeFailed(ex, id);
            return await this.UndoRevokeAsync(id, profile.Id, removed, DecisionStatus.Conflict, request.State);
        }

        return moved.Outcome switch
        {
            RequestOutcome.Changed => new RevokeItem(id, DecisionStatus.Succeeded, RequestState.Revoked, notOwned),
            RequestOutcome.Unchanged => new RevokeItem(id, DecisionStatus.AlreadyDecided, RequestState.Revoked, notOwned),
            RequestOutcome.NotFound => new RevokeItem(id, DecisionStatus.NotFound, null),
            _ => await this.UndoRevokeAsync(id, profile.Id, removed, DecisionStatus.Conflict, moved.Request?.State ?? request.State),
        };
    }

    private async Task<RevokeItem> UndoRevokeAsync(
        int id,
        int profileId,
        IReadOnlyList<RemovedLabel> removed,
        DecisionStatus status,
        RequestState state)
    {
        try
        {
            foreach (RemovedLabel label in removed)
            {
                await labels.AddAsync(label.ItemId, label.Label, profileId, label.Source, CancellationToken.None);
            }

            return new RevokeItem(id, status, state);
        }
        catch (Exception ex) when (ex is MediaServerException or HttpRequestException or TaskCanceledException or DbUpdateException)
        {
            this.LogRevokeUndoFailed(ex, id);
            return new RevokeItem(id, DecisionStatus.Failed, state);
        }
    }

    private async Task<GrantItem> FinishGrantAsync(
        MediaRequest request,
        KidProfile profile,
        IReadOnlyList<MediaItem> items,
        RequestActor parent,
        CancellationToken ct)
    {
        if (request.State == RequestState.Pending)
        {
            RequestResult approved = await requests.TransitionAsync(request.Id, RequestState.Approved, parent, GrantReason, ct, RequestState.Pending);
            if (approved is not { Outcome: RequestOutcome.Changed, Request: MediaRequest next })
            {
                return Settled(approved, request);
            }

            request = next;
        }

        // The parent's decision is stored, so the move to available and the label finish even if the caller goes away.
        RequestActor system = RequestActor.System(GrantComponent);
        if (request.State is RequestState.Approved or RequestState.Failed)
        {
            RequestResult available = await requests.TransitionAsync(
                request.Id,
                RequestState.Available,
                system,
                ct: CancellationToken.None,
                expectedFrom: request.State);
            if (available is not { Outcome: RequestOutcome.Changed, Request: MediaRequest next })
            {
                return Settled(available, request);
            }

            request = next;
        }

        RequestLabelResult labeled = await labeling.LabelAsync(request.Id, items, profile, system, GrantSource);
        return labeled.Result is { Request.State: RequestState.Labeled, Request: MediaRequest done }
            ? new GrantItem(GrantStatus.Granted, done, labeled.LabelNotOwned)
            : new GrantItem(GrantStatus.Unavailable, labeled.Result.Request ?? request);
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Revoke for request {RequestId} removed a label but could neither mark the request revoked nor put the label back; revoking again finishes it")]
    private partial void LogRevokeUndoFailed(Exception ex, int requestId);

    private sealed record RemovedLabel(string ItemId, string Label, string Source);
}
