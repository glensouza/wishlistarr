using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Requests;

public static class RequestEndpoints
{
    public static void MapRequests(this RouteGroupBuilder parent)
    {
        RouteGroupBuilder requests = parent.MapGroup("/requests").WithTags("Requests");

        requests.MapGet("/", async (string? state, WishlistarrDbContext db, RequestTitles titles, CancellationToken ct) =>
        {
            if (!TryState(state, out RequestState? parsed))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The state is not a request state.");
            }

            List<MediaRequest> rows = await db.MediaRequests.AsNoTracking()
                .Where(request => parsed == null || request.State == parsed)
                .OrderBy(request => request.Id)
                .ToListAsync(ct);
            int[] profileIds = [.. rows.Select(request => request.KidProfileId).Distinct()];
            Dictionary<int, string> names = profileIds.Length == 0
                ? []
                : await db.KidProfiles.AsNoTracking()
                    .Where(profile => profileIds.Contains(profile.Id))
                    .ToDictionaryAsync(profile => profile.Id, profile => profile.Name, ct);
            Dictionary<int, string> reasons = await RequestViews.DenialReasonsAsync(db, [.. rows.Select(request => request.Id)], ct);
            List<ParentRequestResponse> body = [];
            foreach (MediaRequest request in rows)
            {
                if (!names.TryGetValue(request.KidProfileId, out string? kid))
                {
                    continue;
                }

                body.Add(new ParentRequestResponse(
                    request.Id,
                    request.KidProfileId,
                    kid,
                    request.Kind,
                    request.TmdbId,
                    await titles.GetAsync(request, ct),
                    request.State,
                    request.RequestCount,
                    request.CreatedAt,
                    request.State == RequestState.Denied && reasons.TryGetValue(request.Id, out string? reason) ? reason : null));
            }

            return Results.Ok(body);
        })
            .WithName("ListRequests")
            .WithSummary("Lists requests. An optional state query limits the list. Denial reasons are plain text.")
            .Produces<IReadOnlyList<ParentRequestResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

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

        requests.MapPost("/revoke", (DecisionBody? body, HttpContext context, RequestAccess access, CancellationToken ct) =>
            RevokeAsync(body, context, access, ct))
            .WithName("RevokeRequests")
            .WithSummary("Removes the app-owned Plex label for each request and marks it revoked. A label the app did not apply is left in place. If Plex cannot be reached, the request stays as it was.")
            .Produces<DecisionBodyResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> DecideAsync(
        DecisionBody? body,
        HttpContext context,
        RequestApproval approval,
        bool approving,
        CancellationToken ct)
    {
        if (body is not { Ids: { Count: > 0 } ids })
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A list of request ids is required.");
        }

        if (ids.Count > RequestApproval.MaxBatch)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: $"At most {RequestApproval.MaxBatch.ToString(CultureInfo.InvariantCulture)} requests can be decided at once.");
        }

        if (ids.Any(id => id <= 0))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Request ids must be positive.");
        }

        if (!RequestViews.TryNormalizeNote(body.Reason, out string? reason, out string? error))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: error);
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
            GrantStatus.Granted => Results.Json(await GrantResponse.FromAsync(result.Request!, titleStore, ct), statusCode: StatusCodes.Status201Created),
            GrantStatus.AlreadyGranted => Results.Ok(await GrantResponse.FromAsync(result.Request!, titleStore, ct)),
            GrantStatus.ProfileNotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The kid profile does not exist."),
            GrantStatus.TitleNotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "That title could not be found."),
            GrantStatus.NotInLibrary => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "That title is not in the library."),
            GrantStatus.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "That profile already has a request for this title."),
            _ => Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The title could not be given."),
        };
    }

    private static async Task<IResult> RevokeAsync(DecisionBody? body, HttpContext context, RequestAccess access, CancellationToken ct)
    {
        if (body is not { Ids: { Count: > 0 } ids })
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A list of request ids is required.");
        }

        if (ids.Count > RequestApproval.MaxBatch)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: $"At most {RequestApproval.MaxBatch.ToString(CultureInfo.InvariantCulture)} requests can be revoked at once.");
        }

        if (ids.Any(id => id <= 0))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Request ids must be positive.");
        }

        if (!RequestViews.TryNormalizeNote(body.Reason, out string? reason, out string? error))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: error);
        }

        IReadOnlyList<DecisionItem> results = await access.RevokeManyAsync(ids, RequestApproval.ParentFrom(context.User), reason, ct);
        return Results.Ok(new DecisionBodyResponse(results));
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

public sealed record DecisionBody(IReadOnlyList<int>? Ids, string? Reason);

public sealed record DecisionBodyResponse(IReadOnlyList<DecisionItem> Results);

public sealed record GrantRequest(int? KidProfileId, MediaKind? Kind, int? TmdbId);

public sealed record GrantResponse(int Id, int KidProfileId, MediaKind Kind, int TmdbId, string Title, RequestState State)
{
    public static async Task<GrantResponse> FromAsync(MediaRequest request, RequestTitles titles, CancellationToken ct) =>
        new(request.Id, request.KidProfileId, request.Kind, request.TmdbId, await titles.GetAsync(request, ct), request.State);
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
    string? DenialReason);
