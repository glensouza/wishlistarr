using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Requests;

public static class RequestEndpoints
{
    public const int DefaultPage = 50;
    public const int MaxPage = 200;

    public static void MapRequests(this RouteGroupBuilder parent)
    {
        RouteGroupBuilder requests = parent.MapGroup("/requests").WithTags("Requests");

        requests.MapGet("/", async (string? state, int? limit, int? offset, WishlistarrDbContext db, RequestTitles titles, CancellationToken ct) =>
        {
            if (!TryState(state, out RequestState? parsed))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The state is not a request state.");
            }

            int take = limit ?? DefaultPage;
            int skip = offset ?? 0;
            if (take is < 1 or > MaxPage || skip < 0)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: $"The limit is 1 to {MaxPage.ToString(CultureInfo.InvariantCulture)} and the offset is not negative.");
            }

            IQueryable<MediaRequest> matching = db.MediaRequests.AsNoTracking()
                .Where(request => parsed == null || request.State == parsed);
            int total = await matching.CountAsync(ct);
            List<MediaRequest> rows = await matching
                .OrderBy(request => request.Id)
                .Skip(skip)
                .Take(take)
                .ToListAsync(ct);
            IReadOnlyDictionary<int, string> names = await titles.GetManyAsync(rows, ct);
            int[] profileIds = [.. rows.Select(request => request.KidProfileId).Distinct()];
            Dictionary<int, string> kids = profileIds.Length == 0
                ? []
                : await db.KidProfiles.AsNoTracking()
                    .Where(profile => profileIds.Contains(profile.Id))
                    .ToDictionaryAsync(profile => profile.Id, profile => profile.Name, ct);
            Dictionary<int, string> reasons = await RequestViews.DenialReasonsAsync(db, [.. rows.Select(request => request.Id)], ct);
            List<ParentRequestResponse> body = [];
            foreach (MediaRequest request in rows)
            {
                if (!kids.TryGetValue(request.KidProfileId, out string? kid))
                {
                    continue;
                }

                body.Add(new ParentRequestResponse(
                    request.Id,
                    request.KidProfileId,
                    kid,
                    request.Kind,
                    request.TmdbId,
                    names[request.Id],
                    request.State,
                    request.RequestCount,
                    request.CreatedAt,
                    request.State == RequestState.Denied && reasons.TryGetValue(request.Id, out string? reason) ? reason : null));
            }

            return Results.Ok(new ParentRequestPage(body, total, take, skip));
        })
            .WithName("ListRequests")
            .WithSummary("Lists requests in id order, one page at a time. An optional state query limits the list, limit (default 50, at most 200) and offset choose the page. Denial reasons are plain text.")
            .Produces<ParentRequestPage>()
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

public sealed record ParentRequestPage(IReadOnlyList<ParentRequestResponse> Items, int Total, int Limit, int Offset);

public sealed record DecisionBodyResponse(IReadOnlyList<DecisionItem> Results);

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
