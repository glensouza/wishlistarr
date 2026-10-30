using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Requests;

public static class KidRequestEndpoints
{
    public const string NotAllowedCode = "notAllowed";
    public const string AllowanceUsedCode = "allowanceUsed";
    public const int DefaultHistoryPage = RequestEndpoints.DefaultPage;
    public const int MaxHistoryPage = RequestEndpoints.MaxPage;

    public static void MapKidRequests(this RouteGroupBuilder kid)
    {
        kid.MapGet("/requests/history", async (int? limit, int? offset, HttpContext context, WishlistarrDbContext db, RequestTitles titles, CancellationToken ct) =>
        {
            if (!TryProfile(context, out int profileId))
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Sign in to a profile first.");
            }

            if (!RequestEndpoints.TryPage(limit, offset, out int take, out int skip, out string? pageError))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: pageError);
            }

            return Results.Ok(await RequestViews.KidHistoryAsync(db, profileId, titles, take, skip, ct));
        })
            .WithName("ListKidRequestHistory")
            .WithSummary("Lists what happened to the signed-in profile's requests, newest first and in kid-friendly terms. limit (default 50, at most 200) and offset choose the page. Parent account details are not included.")
            .Produces<IReadOnlyList<KidHistoryEntry>>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        kid.MapGet("/requests", async (HttpContext context, WishlistarrDbContext db, RequestTitles titles, CancellationToken ct) =>
        {
            if (!TryProfile(context, out int profileId))
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Sign in to a profile first.");
            }

            List<MediaRequest> rows = await db.MediaRequests.AsNoTracking().Include(request => request.Seasons)
                .Where(request => request.KidProfileId == profileId)
                .OrderByDescending(request => request.Id)
                .ToListAsync(ct);
            Dictionary<int, string> reasons = await RequestViews.DenialReasonsAsync(db, [.. rows.Select(request => request.Id)], ct);
            IReadOnlyDictionary<int, string> names = await titles.GetManyAsync(rows, ct);
            List<KidRequestResponse> body = [];
            foreach (MediaRequest request in rows)
            {
                string? denial = request.State == RequestState.Denied && reasons.TryGetValue(request.Id, out string? reason)
                    ? reason
                    : null;
                body.Add(KidRequestResponse.From(request, names[request.Id], denial));
            }

            return Results.Ok(body);
        })
            .WithName("ListKidRequests")
            .WithSummary("Lists the signed-in profile's own requests. A denial reason is plain text for that kid and is left out for every other state.")
            .Produces<IReadOnlyList<KidRequestResponse>>();

        kid.MapGet("/wishlist", async (HttpContext context, KidWishlist wishlist, CancellationToken ct) =>
        {
            if (!TryProfile(context, out int profileId))
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Sign in to a profile first.");
            }

            KidWishlistResponse? response = await wishlist.ListAsync(profileId, ct);

            if (response is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Sign in to a profile first.");
            }

            context.Response.Headers.CacheControl = "private, no-store";
            return Results.Ok(response);
        })
            .WithTags("Kid")
            .WithName("GetKidWishlist")
            .WithSummary("Lists the signed-in profile's own requests with the weekly allowance left. States are kid-friendly, a denial includes only the parent's note, and a ready title includes a Plex link when the library index can name the item.")
            .Produces<KidWishlistResponse>();

        kid.MapPost("/requests", async (KidRequestBody? body, HttpContext context, KidRequestFlow flow, RequestTitles titles, CancellationToken ct) =>
        {
            if (body is not { Kind: MediaKind kind, TmdbId: > 0 and int tmdbId })
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A kind and a TMDB id are required.");
            }

            if (!TryProfile(context, out int profileId))
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Sign in to a profile first.");
            }

            if (body.Seasons is { } seasons && (kind != MediaKind.Show || seasons.Count is < 1 or > 200
                || seasons.Any(number => number < 0) || seasons.Distinct().Count() != seasons.Count))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Choose distinct TV season numbers.");
            }

            KidRequestResult result;
            try
            {
                result = await flow.SubmitAsync(profileId, new TitleKey(kind, tmdbId), ct, body.Seasons);
            }
            catch (TmdbNotConfiguredException)
            {
                return Unavailable(StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is TmdbException or HttpRequestException or TaskCanceledException)
            {
                return Unavailable(StatusCodes.Status502BadGateway);
            }

            return result.Outcome switch
            {
                KidRequestOutcome.Created => Results.Json(KidRequestResponse.From(result.Request!, await titles.GetAsync(result.Request!, ct), null), statusCode: StatusCodes.Status201Created),
                KidRequestOutcome.Merged => Results.Ok(KidRequestResponse.From(result.Request!, await titles.GetAsync(result.Request!, ct), null)),
                KidRequestOutcome.NotAllowed => Results.Problem(
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "That one isn't available.",
                    detail: "Try picking something else.",
                    extensions: new Dictionary<string, object?> { ["code"] = NotAllowedCode }),
                KidRequestOutcome.AllowanceUsed => Results.Problem(
                    statusCode: StatusCodes.Status429TooManyRequests,
                    title: "You've used all your requests for this week.",
                    detail: "You can ask for more when the new week starts.",
                    extensions: new Dictionary<string, object?> { ["code"] = AllowanceUsedCode, ["resetsAt"] = result.AllowanceResetsAt }),
                KidRequestOutcome.InvalidSeasons => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Choose seasons listed for this show."),
                KidRequestOutcome.TitleNotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "We couldn't find that one."),
                _ => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Sign in to a profile first."),
            };
        })
            .WithTags("Kid")
            .WithName("CreateKidRequest")
            .WithSummary("Asks for a title for the signed-in profile. Allowed titles already in Plex are labeled right away. A profile auto-approve rule can approve the rest. Anything left waits for a parent.")
            .Produces<KidRequestResponse>(StatusCodes.Status201Created)
            .Produces<KidRequestResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static bool TryProfile(HttpContext context, out int profileId) =>
        int.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None, CultureInfo.InvariantCulture, out profileId);

    private static IResult Unavailable(int status) =>
        Results.Problem(statusCode: status, title: "Requests aren't working right now.", detail: "Try again in a little while.");
}

public sealed record KidRequestBody(MediaKind? Kind, int? TmdbId, IReadOnlyList<int>? Seasons = null);

public sealed record KidRequestResponse(int Id, MediaKind Kind, int TmdbId, string Title, RequestState State, int RequestCount, string? DenialReason, IReadOnlyList<RequestSeasonResponse> Seasons)
{
    public static KidRequestResponse From(MediaRequest request, string title, string? denialReason) =>
        new(request.Id, request.Kind, request.TmdbId, title, request.State, request.RequestCount, denialReason, RequestSeasonResponse.From(request));
}

public sealed record RequestSeasonResponse(int Number, SeasonAvailability Availability, int AvailableEpisodes, bool Requested)
{
    public static IReadOnlyList<RequestSeasonResponse> From(MediaRequest request) =>
        [.. request.Seasons.OrderBy(season => season.Number).Select(season => new RequestSeasonResponse(season.Number, season.Availability, season.AvailableEpisodes, season.Requested))];
}
