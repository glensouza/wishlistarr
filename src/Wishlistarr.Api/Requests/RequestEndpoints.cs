using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Requests;

public static class RequestEndpoints
{
    public const int DefaultPage = 50;
    public const int MaxPage = 200;

    public static void MapRequests(this RouteGroupBuilder parent)
    {
        RouteGroupBuilder requests = parent.MapGroup("/requests").WithTags("Requests");

        requests.MapGet("/", async (string? state, int? limit, int? offset, WishlistarrDbContext db, RequestTitles titles, RequestAccess access, CancellationToken ct) =>
        {
            if (!TryState(state, out RequestState? parsed))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The state is not a request state.");
            }

            if (!TryPage(limit, offset, out int take, out int skip, out string? pageError))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: pageError);
            }

            IQueryable<ListedRequest> matching = db.MediaRequests.AsNoTracking()
                .Where(request => parsed == null || request.State == parsed)
                .Join(
                    db.KidProfiles.AsNoTracking(),
                    request => request.KidProfileId,
                    profile => profile.Id,
                    (request, profile) => new ListedRequest { Request = request, KidName = profile.Name });
            int total;
            int unfinished;
            List<ListedRequest> listed;
            await db.Database.OpenConnectionAsync(ct);
            await using (SqliteTransaction snapshot = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true))
            await using (await db.Database.UseTransactionAsync(snapshot, ct))
            {
                total = await matching.CountAsync(ct);
                listed = await matching
                    .OrderBy(item => item.Request.Id)
                    .Skip(skip)
                    .Take(take)
                    .ToListAsync(ct);
                unfinished = await access.CountUnfinishedAsync(ct);
            }

            List<MediaRequest> rows = [.. listed.Select(item => item.Request)];
            IReadOnlyDictionary<int, string> names = await titles.GetManyAsync(rows, ct);
            Dictionary<int, string> reasons = await RequestViews.DenialReasonsAsync(db, [.. rows.Select(request => request.Id)], ct);
            IReadOnlySet<int> revocable = await access.RevocableAsync(rows, ct);
            HashSet<int> mayRemain = await RequestViews.LabelMayRemainAsync(
                db,
                [.. rows.Where(request => request.State == RequestState.Failed).Select(request => request.Id)],
                ct);
            Dictionary<int, MediaRequestEvent> failures = await RequestViews.LastFailuresAsync(
                db,
                [.. rows.Where(request => request.State == RequestState.Failed).Select(request => request.Id)],
                ct);
            List<ParentRequestResponse> body = [];
            foreach (ListedRequest item in listed)
            {
                MediaRequest request = item.Request;
                body.Add(new ParentRequestResponse(
                    request.Id,
                    request.KidProfileId,
                    item.KidName,
                    request.Kind,
                    request.TmdbId,
                    names[request.Id],
                    request.State,
                    request.RequestCount,
                    request.CreatedAt,
                    request.State == RequestState.Denied && reasons.TryGetValue(request.Id, out string? reason) ? reason : null,
                    revocable.Contains(request.Id),
                    mayRemain.Contains(request.Id) || (request.State == RequestState.Available && revocable.Contains(request.Id)),
                    failures.TryGetValue(request.Id, out MediaRequestEvent? failure) && RequestDispatch.IsRetryable(failure),
                    failure is not null && RequestDispatch.IsRetryable(failure) ? RequestDispatch.FailureCode(failure) : null));
            }

            return Results.Ok(new ParentRequestPage(body, total, take, skip, unfinished));
        })
            .WithName("ListRequests")
            .WithSummary("Lists requests in id order, one page at a time. An optional state query limits the list, limit (default 50, at most 200) and offset choose the page. Denial reasons are plain text. canRevoke says whether a parent can revoke the request now, labelMayRemain whether a label may have been left in Plex, canRetry whether a failed request can be sent again, dispatchFailure why sending it failed (notConfigured or unavailable), and unfinished counts the failed and stuck requests.")
            .Produces<ParentRequestPage>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        requests.MapGet("/queue", async (int? kidProfileId, int? limit, int? offset, HttpContext context, RequestQueue queue, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (kidProfileId is <= 0)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The kid profile id must be a positive number.");
            }

            if (!TryPage(limit, offset, out int take, out int skip, out string? pageError))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: pageError);
            }

            ApprovalQueuePage? page = await queue.GetAsync(kidProfileId, take, skip, ct);
            return page is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The kid profile does not exist.")
                : Results.Ok(page);
        })
            .WithName("GetApprovalQueue")
            .WithSummary("Lists pending requests for a parent, with the synopsis, US rating and descriptors, US streaming providers, trailer key and the current reason a parent has to decide. A kid session cannot call it. Title details that cannot be read are left out and named in gaps; the request can still be approved or denied. tmdbConfigured is false when no TMDB key is saved, and then no title details are read.")
            .Produces<ApprovalQueuePage>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        requests.MapPost("/approve", (DecisionBody? body, HttpContext context, RequestApproval approval, CancellationToken ct) =>
            DecideAsync(body, context, approval, approving: true, ct))
            .WithName("ApproveRequests")
            .WithSummary("Approves pending requests. A title the library index already has is labeled and becomes available. Each id is reported on its own.")
            .Produces<DecisionBodyResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        requests.MapPost("/deny", (DecisionBody? body, HttpContext context, RequestApproval approval, CancellationToken ct) =>
            DecideAsync(body, context, approval, approving: false, ct))
            .WithName("DenyRequests")
            .WithSummary("Denies pending requests. An optional reason is stored as plain text and shown only to that kid.")
            .Produces<DecisionBodyResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        requests.MapPost("/retry", (DecisionBody? body, HttpContext context, RequestDispatch dispatch, CancellationToken ct) =>
            RetryAsync(body, context, dispatch, ct))
            .WithName("RetryRequests")
            .WithSummary("Sends failed requests to their download service again. A request keeps the service it was approved with. Retrying never adds a title that the service already has. Requests whose Plex label failed are not retried here and are revoked instead.")
            .Produces<DecisionBodyResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        requests.MapPost("/grant", (GrantRequest? body, HttpContext context, RequestAccess access, RequestTitles titleStore, CancellationToken ct) =>
            GrantAsync(body, context, access, titleStore, ct))
            .WithName("GrantRequest")
            .WithSummary("Gives a kid an in-library title without a request. The profile label is applied and the parent is recorded. A title the content policy would block is still granted.")
            .Produces<GrantResponse>(StatusCodes.Status201Created)
            .Produces<GrantResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        requests.MapGet("/grant/preview", (string? kind, int? tmdbId, RequestAccess access, CancellationToken ct) =>
            PreviewAsync(kind, tmdbId, access, ct))
            .WithName("PreviewGrant")
            .WithSummary("Looks up a title by kind and TMDB id before it is given, so the parent can check the name. No request or label is changed, but the title may be stored in the title cache. inLibrary is null when the library could not be checked.")
            .Produces<GrantPreviewResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        requests.MapPost("/revoke", (DecisionBody? body, HttpContext context, RequestAccess access, CancellationToken ct) =>
            RevokeAsync(body, context, access, ct))
            .WithName("RevokeRequests")
            .WithSummary("Removes the app-owned Plex labels for each labeled or failed request, or one stuck as available, and marks it revoked. A label the app did not apply is left in place and reported with labelNotOwned; a label that is now another profile's is left in place and reported with labelInUse. If Plex cannot be reached, the request stays as it was.")
            .Produces<RevokeBodyResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    private static IResult? ReadBatch(DecisionBody? body, string verb, out IReadOnlyList<int> ids, out string? reason)
    {
        ids = [];
        reason = null;
        if (body is not { Ids: { Count: > 0 } given })
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A list of request ids is required.");
        }

        if (given.Count > RequestApproval.MaxBatch)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: $"At most {RequestApproval.MaxBatch.ToString(CultureInfo.InvariantCulture)} requests can be {verb} at once.");
        }

        if (given.Any(id => id <= 0))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Request ids must be positive.");
        }

        if (!RequestViews.TryNormalizeNote(body.Reason, out reason, out string? error))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: error);
        }

        ids = given;
        return null;
    }

    private static async Task<IResult> RetryAsync(DecisionBody? body, HttpContext context, RequestDispatch dispatch, CancellationToken ct)
    {
        if (ReadBatch(body, "retried", out IReadOnlyList<int> ids, out string? reason) is IResult problem)
        {
            return problem;
        }

        IReadOnlyList<DecisionItem> results = await dispatch.RetryManyAsync(ids, RequestApproval.ParentFrom(context.User), reason, ct);
        return Results.Ok(new DecisionBodyResponse(results));
    }

    private static async Task<IResult> DecideAsync(
        DecisionBody? body,
        HttpContext context,
        RequestApproval approval,
        bool approving,
        CancellationToken ct)
    {
        if (ReadBatch(body, "decided", out IReadOnlyList<int> ids, out string? reason) is IResult problem)
        {
            return problem;
        }

        RequestActor actor = RequestApproval.ParentFrom(context.User);
        IReadOnlyList<DecisionItem> results = approving
            ? await approval.ApproveManyAsync(ids, actor, reason, ct)
            : await approval.DenyManyAsync(ids, actor, reason, ct);
        return Results.Ok(new DecisionBodyResponse(results));
    }

    private static async Task<IResult> GrantAsync(
        GrantRequest? body,
        HttpContext context,
        RequestAccess access,
        RequestTitles titleStore,
        CancellationToken ct)
    {
        if (body is not { KidProfileId: > 0 and int profileId, Kind: MediaKind kind, TmdbId: > 0 and int tmdbId })
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A kid profile, a kind and a TMDB id are required.");
        }

        GrantItem result;
        try
        {
            result = await access.GrantAsync(profileId, new TitleKey(kind, tmdbId), RequestApproval.ParentFrom(context.User), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is TmdbException or HttpRequestException or TaskCanceledException)
        {
            return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The title could not be given.");
        }

        return result.Status switch
        {
            GrantStatus.Granted => Results.Json(await GrantResponse.FromAsync(result, titleStore, ct), statusCode: StatusCodes.Status201Created),
            GrantStatus.AlreadyGranted => Results.Ok(await GrantResponse.FromAsync(result, titleStore, ct)),
            GrantStatus.ProfileNotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The kid profile does not exist."),
            GrantStatus.TitleNotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "That title could not be found."),
            GrantStatus.NotInLibrary => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "That title is not in the library."),
            GrantStatus.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "That profile already has a request for this title."),
            GrantStatus.InProgress => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "That title is still being given. Try again in a moment."),
            _ => Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The title could not be given."),
        };
    }

    private static async Task<IResult> RevokeAsync(DecisionBody? body, HttpContext context, RequestAccess access, CancellationToken ct)
    {
        if (ReadBatch(body, "revoked", out IReadOnlyList<int> ids, out string? reason) is IResult problem)
        {
            return problem;
        }

        IReadOnlyList<RevokeItem> results = await access.RevokeManyAsync(ids, RequestApproval.ParentFrom(context.User), reason, ct);
        return Results.Ok(new RevokeBodyResponse(results));
    }

    private static async Task<IResult> PreviewAsync(string? kind, int? tmdbId, RequestAccess access, CancellationToken ct)
    {
        MediaKind? parsed = kind?.Trim().ToLowerInvariant() switch
        {
            "movie" => MediaKind.Movie,
            "show" => MediaKind.Show,
            _ => null,
        };
        if (parsed is not MediaKind mediaKind || tmdbId is not (> 0 and int id))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A kind (movie or show) and a positive TMDB id are required.");
        }

        GrantPreview preview;
        try
        {
            preview = await access.PreviewAsync(new TitleKey(mediaKind, id), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is TmdbException or HttpRequestException or TaskCanceledException)
        {
            return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The title could not be looked up.");
        }

        return preview.Status == GrantPreviewStatus.Found
            ? Results.Ok(new GrantPreviewResponse(mediaKind, id, preview.Title!, preview.InLibrary))
            : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "That title could not be found.");
    }

    internal static bool TryPage(int? limit, int? offset, out int take, out int skip, out string? error)
    {
        take = limit ?? DefaultPage;
        skip = offset ?? 0;
        if (take is < 1 or > MaxPage || skip < 0)
        {
            error = $"The limit is 1 to {MaxPage.ToString(CultureInfo.InvariantCulture)} and the offset is not negative.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryState(string? state, out RequestState? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(state))
        {
            return true;
        }

        string text = state.Trim();
        if (text.All(char.IsAsciiDigit)
            || !Enum.TryParse(text, ignoreCase: true, out RequestState value)
            || !Enum.IsDefined(value))
        {
            return false;
        }

        parsed = value;
        return true;
    }
}

file sealed class ListedRequest
{
    public required MediaRequest Request { get; init; }

    public required string KidName { get; init; }
}

public sealed record DecisionBody(IReadOnlyList<int>? Ids, string? Reason);

public sealed record ParentRequestPage(IReadOnlyList<ParentRequestResponse> Items, int Total, int Limit, int Offset, int Unfinished);

public sealed record DecisionBodyResponse(IReadOnlyList<DecisionItem> Results);

public sealed record GrantRequest(int? KidProfileId, MediaKind? Kind, int? TmdbId);

public sealed record RevokeBodyResponse(IReadOnlyList<RevokeItem> Results);

public sealed record GrantPreviewResponse(MediaKind Kind, int TmdbId, string Title, bool? InLibrary);

public sealed record GrantResponse(int Id, int KidProfileId, MediaKind Kind, int TmdbId, string Title, RequestState State, bool LabelNotOwned)
{
    public static async Task<GrantResponse> FromAsync(GrantItem item, RequestTitles titles, CancellationToken ct)
    {
        MediaRequest request = item.Request!;
        return new(request.Id, request.KidProfileId, request.Kind, request.TmdbId, await titles.GetAsync(request, ct), request.State, item.LabelNotOwned);
    }
}

public sealed record ParentRequestResponse(
    int Id,
    int KidProfileId,
    string KidName,
    MediaKind Kind,
    int TmdbId,
    string Title,
    RequestState State,
    int RequestCount,
    DateTimeOffset CreatedAt,
    string? DenialReason,
    bool CanRevoke,
    bool LabelMayRemain,
    bool CanRetry,
    string? DispatchFailure);
