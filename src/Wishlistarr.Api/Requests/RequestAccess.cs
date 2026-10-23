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

public sealed record GrantItem(GrantStatus Status, MediaRequest? Request);

public sealed partial class RequestAccess(
    WishlistarrDbContext db,
    MediaRequests requests,
    RequestTitles titles,
    LibraryLookup library,
    LabelWriter labels,
    ILogger<RequestAccess> logger)
{
    public const string GrantReason = "Parent grant";
    public const string GrantSource = "grant";
    public const string GrantComponent = "grant";

    public async Task<GrantItem> GrantAsync(int kidProfileId, TitleKey title, RequestActor actor, CancellationToken ct = default)
    {
        if (actor.Kind != RequestActorKind.Parent)
        {
            return new GrantItem(GrantStatus.Conflict, null);
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
        if (open is not null)
        {
            if (open.State == RequestState.Labeled)
            {
                return new GrantItem(GrantStatus.AlreadyGranted, open);
            }

            if (!await this.IsParentGrantAsync(open.Id, ct) || open.State is not (RequestState.Pending or RequestState.Approved or RequestState.Available or RequestState.Failed))
            {
                return new GrantItem(GrantStatus.Conflict, open);
            }
        }

        if (!await this.ApplyLabelAsync(items, profile, ct))
        {
            return new GrantItem(GrantStatus.Unavailable, open);
        }

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
            if (created.Outcome == RequestOutcome.Created)
            {
                open = created.Request;
            }
            else if (created.Outcome == RequestOutcome.Merged && created.Request is not null && await this.IsParentGrantAsync(created.Request.Id, ct))
            {
                open = created.Request.State == RequestState.Labeled
                    ? null
                    : created.Request;
                if (created.Request.State == RequestState.Labeled)
                {
                    return new GrantItem(GrantStatus.AlreadyGranted, created.Request);
                }
            }

            if (open is null)
            {
                return new GrantItem(created.Outcome == RequestOutcome.Merged ? GrantStatus.Conflict : GrantStatus.Unavailable, created.Request);
            }
        }

        MediaRequest? finished = await this.FinishAsync(open, actor, ct);
        return finished is null
            ? new GrantItem(GrantStatus.Unavailable, open)
            : new GrantItem(GrantStatus.Granted, finished);
    }

    public async Task<IReadOnlyList<DecisionItem>> RevokeManyAsync(
        IReadOnlyList<int> ids,
        RequestActor actor,
        string? reason,
        CancellationToken ct = default)
    {
        List<DecisionItem> results = new(ids.Count);
        foreach (int id in ids)
        {
            results.Add(await this.RevokeOneAsync(id, actor, reason, ct));
        }

        return results;
    }

    private async Task<DecisionItem> RevokeOneAsync(int id, RequestActor actor, string? reason, CancellationToken ct)
    {
        MediaRequest? request = await requests.GetAsync(id, ct);
        if (request is null)
        {
            return new DecisionItem(id, DecisionStatus.NotFound, null);
        }

        if (actor.Kind != RequestActorKind.Parent)
        {
            return new DecisionItem(id, DecisionStatus.Failed, request.State);
        }

        if (request.State == RequestState.Revoked)
        {
            return new DecisionItem(id, DecisionStatus.AlreadyDecided, request.State);
        }

        if (request.State is not (RequestState.Labeled or RequestState.Available))
        {
            return new DecisionItem(id, DecisionStatus.Conflict, request.State);
        }

        KidProfile? profile = await db.KidProfiles.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == request.KidProfileId && row.ArchivedAt == null, ct);
        if (profile is null)
        {
            return new DecisionItem(id, DecisionStatus.Failed, request.State);
        }

        IReadOnlyList<MediaItem> items;
        try
        {
            items = await library.FindAsync(new TitleKey(request.Kind, request.TmdbId), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is MediaServerException or HttpRequestException or TaskCanceledException)
        {
            this.LogRevokeFailed(ex, id);
            return new DecisionItem(id, DecisionStatus.Failed, request.State);
        }

        if (items.Count == 0)
        {
            return new DecisionItem(id, DecisionStatus.Failed, request.State);
        }

        // The label is already gone in Plex before the request is marked revoked, so a dropped call cannot leave access in place.
        CancellationToken revokeCt = CancellationToken.None;
        try
        {
            if (!await this.RemoveOwnedAsync(items, profile.Id, revokeCt))
            {
                return new DecisionItem(id, DecisionStatus.Failed, request.State);
            }
        }
        catch (Exception ex) when (ex is MediaServerException or HttpRequestException or TaskCanceledException)
        {
            this.LogRevokeFailed(ex, id);
            return new DecisionItem(id, DecisionStatus.Failed, request.State);
        }

        RequestResult moved = await requests.TransitionAsync(id, RequestState.Revoked, actor, reason, revokeCt);
        return moved.Outcome switch
        {
            RequestOutcome.Changed => new DecisionItem(id, DecisionStatus.Succeeded, RequestState.Revoked),
            RequestOutcome.Unchanged => new DecisionItem(id, DecisionStatus.AlreadyDecided, RequestState.Revoked),
            RequestOutcome.NotFound => new DecisionItem(id, DecisionStatus.NotFound, null),
            _ => new DecisionItem(id, DecisionStatus.Conflict, moved.Request?.State ?? request.State),
        };
    }

    private async Task<bool> ApplyLabelAsync(IReadOnlyList<MediaItem> items, KidProfile profile, CancellationToken ct)
    {
        try
        {
            int labeled = 0;
            foreach (MediaItem item in items)
            {
                LabelWriteResult written = await labels.AddAsync(item.Id, profile.PlexLabel, profile.Id, GrantSource, ct);
                if (written.Outcome is LabelWriteOutcome.Added or LabelWriteOutcome.AlreadyPresent)
                {
                    labeled++;
                }
            }

            return labeled > 0;
        }
        catch (Exception ex) when (ex is MediaServerException or HttpRequestException or TaskCanceledException)
        {
            this.LogGrantLabelFailed(ex, profile.Id);
            return false;
        }
    }

    private async Task<bool> RemoveOwnedAsync(IReadOnlyList<MediaItem> items, int profileId, CancellationToken ct)
    {
        List<OwnedLabel> owned = await db.OwnedLabels.AsNoTracking()
            .Where(row => row.KidProfileId == profileId)
            .ToListAsync(ct);
        foreach (MediaItem item in items)
        {
            foreach (OwnedLabel row in owned.Where(label =>
                string.Equals(label.Guid, item.Guid, StringComparison.Ordinal)
                || string.Equals(label.RatingKey, item.Id, StringComparison.Ordinal)))
            {
                LabelWriteResult removed = await labels.RemoveAsync(item.Id, row.Label, ct);
                if (removed.Outcome is not (LabelWriteOutcome.Removed or LabelWriteOutcome.NotPresent or LabelWriteOutcome.ItemNotFound))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private async Task<MediaRequest?> FinishAsync(MediaRequest request, RequestActor parent, CancellationToken ct)
    {
        if (request.State == RequestState.Pending)
        {
            RequestResult approved = await requests.TransitionAsync(request.Id, RequestState.Approved, parent, GrantReason, ct);
            if (approved.Outcome != RequestOutcome.Changed || approved.Request is null)
            {
                return null;
            }

            request = approved.Request;
        }

        RequestActor system = RequestActor.System(GrantComponent);
        if (request.State == RequestState.Failed)
        {
            RequestResult retried = await requests.TransitionAsync(request.Id, RequestState.Available, system, ct: ct);
            if (retried.Request is null || retried.Outcome is not (RequestOutcome.Changed or RequestOutcome.Unchanged))
            {
                return null;
            }

            request = retried.Request;
        }

        if (request.State == RequestState.Approved)
        {
            RequestResult available = await requests.TransitionAsync(request.Id, RequestState.Available, system, ct: ct);
            if (available.Outcome != RequestOutcome.Changed || available.Request is null)
            {
                return null;
            }

            request = available.Request;
        }

        if (request.State == RequestState.Available)
        {
            RequestResult labeled = await requests.TransitionAsync(request.Id, RequestState.Labeled, system, ct: ct);
            if (labeled.Outcome != RequestOutcome.Changed || labeled.Request is null)
            {
                return null;
            }

            request = labeled.Request;
        }

        return request.State == RequestState.Labeled ? request : null;
    }

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Grant label for profile {ProfileId} failed")]
    private partial void LogGrantLabelFailed(Exception ex, int profileId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Revoke for request {RequestId} failed before the request was marked revoked")]
    private partial void LogRevokeFailed(Exception ex, int requestId);
}
