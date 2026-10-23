using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Api.Labels;
using Wishlistarr.Api.Library;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Requests;

public enum DecisionStatus
{
    Succeeded,
    AlreadyDecided,
    NotFound,
    Failed,
    Conflict,
}

public sealed record DecisionItem(int Id, DecisionStatus Status, RequestState? State);

public sealed partial class RequestApproval(
    WishlistarrDbContext db,
    MediaRequests requests,
    LibraryLookup library,
    LabelWriter labels,
    ILogger<RequestApproval> logger)
{
    public const int MaxBatch = 50;
    public const string ApprovalComponent = "approval";
    public const string AutoApproveReason = "Auto-approved.";

    public static RequestActor ParentFrom(ClaimsPrincipal user)
    {
        string name = user.Identity?.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > RequestActor.MaxNameLength)
        {
            name = "Parent";
        }

        string? idText = user.FindFirstValue(ClaimTypes.NameIdentifier);
        int? id = int.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
        return RequestActor.Parent(id, name);
    }

    public async Task<IReadOnlyList<DecisionItem>> ApproveManyAsync(
        IReadOnlyList<int> ids,
        RequestActor actor,
        string? reason,
        CancellationToken ct = default)
    {
        List<DecisionItem> results = new(ids.Count);
        foreach (int id in ids)
        {
            results.Add(await this.DecideOneAsync(id, actor, reason, approving: true, ct));
        }

        return results;
    }

    public async Task<IReadOnlyList<DecisionItem>> DenyManyAsync(
        IReadOnlyList<int> ids,
        RequestActor actor,
        string? reason,
        CancellationToken ct = default)
    {
        List<DecisionItem> results = new(ids.Count);
        foreach (int id in ids)
        {
            results.Add(await this.DecideOneAsync(id, actor, reason, approving: false, ct));
        }

        return results;
    }

    public Task<DecisionItem> ApproveAsync(int requestId, RequestActor actor, string? reason = null, CancellationToken ct = default) =>
        this.DecideOneAsync(requestId, actor, reason, approving: true, ct);

    private async Task<DecisionItem> DecideOneAsync(int id, RequestActor actor, string? reason, bool approving, CancellationToken ct)
    {
        try
        {
            return approving
                ? await this.ApproveOneAsync(id, actor, reason, ct)
                : await this.DenyOneAsync(id, actor, reason, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.LogDecideFailed(ex, id);
            return new DecisionItem(id, DecisionStatus.Failed, null);
        }
    }

    private async Task<DecisionItem> DenyOneAsync(int id, RequestActor actor, string? reason, CancellationToken ct)
    {
        MediaRequest? request = await requests.GetAsync(id, ct);
        if (request is null)
        {
            return new DecisionItem(id, DecisionStatus.NotFound, null);
        }

        if (request.State != RequestState.Pending)
        {
            return Settled(id, request.State, approving: false);
        }

        RequestResult result = await requests.TransitionAsync(id, RequestState.Denied, actor, reason, ct);
        return result.Outcome == RequestOutcome.Changed
            ? new DecisionItem(id, DecisionStatus.Succeeded, RequestState.Denied)
            : FromResult(id, result, approving: false);
    }

    private async Task<DecisionItem> ApproveOneAsync(int id, RequestActor actor, string? reason, CancellationToken ct)
    {
        MediaRequest? request = await requests.GetAsync(id, ct);
        if (request is null)
        {
            return new DecisionItem(id, DecisionStatus.NotFound, null);
        }

        if (request.State != RequestState.Pending)
        {
            return Settled(id, request.State, approving: true);
        }

        LibraryCheck libraryCheck = await this.FindAsync(request, ct);
        if (!libraryCheck.Known)
        {
            return new DecisionItem(id, DecisionStatus.Failed, request.State);
        }

        KidProfile? profile = null;
        if (libraryCheck.Items.Count > 0)
        {
            profile = await db.KidProfiles.AsNoTracking()
                .FirstOrDefaultAsync(row => row.Id == request.KidProfileId && row.ArchivedAt == null, ct);
            if (profile is null)
            {
                return new DecisionItem(id, DecisionStatus.Failed, request.State);
            }
        }

        RequestResult approved = await requests.TransitionAsync(id, RequestState.Approved, actor, reason, ct);
        if (approved.Outcome != RequestOutcome.Changed)
        {
            return FromResult(id, approved, approving: true);
        }

        if (libraryCheck.Items.Count == 0 || profile is null)
        {
            return new DecisionItem(id, DecisionStatus.Succeeded, RequestState.Approved);
        }

        return await this.LabelAsync(approved.Request!, libraryCheck.Items, profile, actor, ct);
    }

    private async Task<DecisionItem> LabelAsync(
        MediaRequest request,
        IReadOnlyList<MediaItem> matches,
        KidProfile profile,
        RequestActor actor,
        CancellationToken ct)
    {
        RequestActor system = actor.Kind == RequestActorKind.System ? actor : RequestActor.System(ApprovalComponent);
        RequestResult available = await requests.TransitionAsync(request.Id, RequestState.Available, system, ct: ct);
        if (available.Outcome is not (RequestOutcome.Changed or RequestOutcome.Unchanged) || available.Request is null)
        {
            return FromResult(request.Id, available, approving: true);
        }

        // The request is already stored as available, so labeling finishes even if the caller disconnects.
        CancellationToken labelCt = CancellationToken.None;
        string? failure = null;
        try
        {
            int labeled = 0;
            foreach (MediaItem match in matches)
            {
                LabelWriteResult written = await labels.AddAsync(match.Id, profile.PlexLabel, profile.Id, KidRequestFlow.LabelSource, labelCt);
                if (written.Outcome is LabelWriteOutcome.Added or LabelWriteOutcome.AlreadyPresent)
                {
                    labeled++;
                }
            }

            if (labeled == 0)
            {
                failure = "The title is no longer in the library.";
            }
        }
        catch (Exception ex) when (ex is MediaServerException or HttpRequestException or TaskCanceledException)
        {
            this.LogLabelFailed(ex, request.Id);
            failure = "The Plex label could not be written.";
        }

        RequestState target = failure is null ? RequestState.Labeled : RequestState.Failed;
        RequestResult moved = await requests.TransitionAsync(request.Id, target, system, failure, labelCt);
        return failure is null
            ? new DecisionItem(request.Id, DecisionStatus.Succeeded, moved.Request?.State ?? RequestState.Labeled)
            : new DecisionItem(request.Id, DecisionStatus.Failed, moved.Request?.State ?? RequestState.Failed);
    }

    private async Task<LibraryCheck> FindAsync(MediaRequest request, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<MediaItem> items = await library.FindAsync(new TitleKey(request.Kind, request.TmdbId), ct);
            return new LibraryCheck(true, items);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is MediaServerException or HttpRequestException or TaskCanceledException)
        {
            this.LogLookupFailed(ex, request.Id);
            return new LibraryCheck(false, []);
        }
    }

    private static DecisionItem FromResult(int id, RequestResult result, bool approving)
    {
        if (result.Outcome == RequestOutcome.NotFound || result.Request is null)
        {
            return new DecisionItem(id, DecisionStatus.NotFound, null);
        }

        if (result.Outcome == RequestOutcome.Changed)
        {
            return new DecisionItem(id, DecisionStatus.Succeeded, result.Request.State);
        }

        return Settled(id, result.Request.State, approving);
    }

    private static DecisionItem Settled(int id, RequestState state, bool approving)
    {
        bool same = approving
            ? state is RequestState.Approved or RequestState.Fulfilling or RequestState.Available or RequestState.Labeled
            : state == RequestState.Denied;
        return new DecisionItem(id, same ? DecisionStatus.AlreadyDecided : DecisionStatus.Conflict, state);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Library lookup for request {RequestId} failed; the decision was not applied")]
    private partial void LogLookupFailed(Exception ex, int requestId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Label for request {RequestId} failed")]
    private partial void LogLabelFailed(Exception ex, int requestId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Decision for request {RequestId} failed")]
    private partial void LogDecideFailed(Exception ex, int requestId);

    private sealed record LibraryCheck(bool Known, IReadOnlyList<MediaItem> Items);
}
