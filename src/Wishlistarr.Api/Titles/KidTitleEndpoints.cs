using System.Globalization;
using System.Security.Claims;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Titles;

public static class KidTitleEndpoints
{
    public static void MapKidTitles(this RouteGroupBuilder kid)
    {
        kid.MapGet("/titles/{kind}/{tmdbId:int}", async (string kind, int tmdbId, HttpContext context, KidTitleDetails titles, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (!TryKind(kind, out MediaKind mediaKind) || tmdbId <= 0)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A kind and a TMDB id are required.");
            }

            if (!int.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None, CultureInfo.InvariantCulture, out int profileId))
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Sign in to a profile first.");
            }

            try
            {
                KidTitleResult result = await titles.GetAsync(profileId, mediaKind, tmdbId, ct);
                return result.Outcome switch
                {
                    KidTitleOutcome.Ok => Results.Ok(result.Title),
                    KidTitleOutcome.ProfileNotFound => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Sign in to a profile first."),
                    _ => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "We couldn't find that one."),
                };
            }
            catch (TmdbNotConfiguredException)
            {
                return Unavailable(StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is TmdbException or HttpRequestException or TaskCanceledException)
            {
                return Unavailable(StatusCodes.Status502BadGateway);
            }
        })
            .WithTags("Kid")
            .WithName("GetKidTitle")
            .WithSummary("Returns one movie or show for the signed-in profile. A title the profile's content policy blocks, or whose rating or keywords cannot be checked, is not found and the response does not say why. YouTube trailers are included only when a parent has turned them on.")
            .Produces<KidTitleResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static bool TryKind(string? value, out MediaKind kind)
    {
        if (string.Equals(value, "movie", StringComparison.OrdinalIgnoreCase))
        {
            kind = MediaKind.Movie;
            return true;
        }

        if (string.Equals(value, "show", StringComparison.OrdinalIgnoreCase))
        {
            kind = MediaKind.Show;
            return true;
        }

        kind = default;
        return false;
    }

    private static IResult Unavailable(int status) =>
        Results.Problem(statusCode: status, title: "That page isn't working right now.", detail: "Try again in a little while.");
}
