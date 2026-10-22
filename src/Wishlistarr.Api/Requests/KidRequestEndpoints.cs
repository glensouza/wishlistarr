using System.Globalization;
using System.Security.Claims;
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

    public static void MapKidRequests(this RouteGroupBuilder kid)
    {
        kid.MapPost("/requests", async (KidRequestBody? body, HttpContext context, KidRequestFlow flow, RequestTitles titles, CancellationToken ct) =>
        {
            if (body is not { Kind: MediaKind kind, TmdbId: > 0 and int tmdbId })
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A kind and a TMDB id are required.");
            }

            if (!int.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None, CultureInfo.InvariantCulture, out int profileId))
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Sign in to a profile first.");
            }

            KidRequestResult result;
            try
            {
                result = await flow.SubmitAsync(profileId, new TitleKey(kind, tmdbId), ct);
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
                KidRequestOutcome.Created => Results.Json(KidRequestResponse.From(result.Request!, await titles.GetAsync(result.Request!, ct)), statusCode: StatusCodes.Status201Created),
                KidRequestOutcome.Merged => Results.Ok(KidRequestResponse.From(result.Request!, await titles.GetAsync(result.Request!, ct))),
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
                KidRequestOutcome.TitleNotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "We couldn't find that one."),
                _ => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Sign in to a profile first."),
            };
        })
            .WithTags("Kid")
            .WithName("CreateKidRequest")
            .WithSummary("Asks for a title for the signed-in profile. Allowed titles already in Plex are labeled right away; anything else waits for a parent.")
            .Produces<KidRequestResponse>(StatusCodes.Status201Created)
            .Produces<KidRequestResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static IResult Unavailable(int status) =>
        Results.Problem(statusCode: status, title: "Requests aren't working right now.", detail: "Try again in a little while.");
}

public sealed record KidRequestBody(MediaKind? Kind, int? TmdbId);

public sealed record KidRequestResponse(int Id, MediaKind Kind, int TmdbId, string Title, RequestState State, int RequestCount)
{
    public static KidRequestResponse From(MediaRequest request, string title) =>
        new(request.Id, request.Kind, request.TmdbId, title, request.State, request.RequestCount);
}
