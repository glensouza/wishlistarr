using System.Globalization;
using System.Security.Claims;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Kids;

public static class KidHomeEndpoints
{
    public static void MapKidHome(this RouteGroupBuilder kid)
    {
        kid.MapGet("/home", async (HttpContext context, KidHome home, CancellationToken ct) =>
        {
            if (!int.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None, CultureInfo.InvariantCulture, out int profileId))
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Sign in to a profile first.");
            }

            try
            {
                HomeLoad load = await home.LoadAsync(profileId, ct);
                if (load.Status == HomeLoadStatus.Missing)
                {
                    return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Sign in to a profile first.");
                }

                if (load.Status == HomeLoadStatus.Unavailable)
                {
                    return Unavailable(StatusCodes.Status502BadGateway);
                }

                context.Response.Headers.CacheControl = "private, no-store";
                return Results.Ok(load.Response);
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
            .WithName("GetKidHome")
            .WithSummary("Returns the signed-in profile's home rows. Every title is allowed by that profile's content policy. Titles that need approval, are blocked, or whose rating or keywords cannot be checked are left out. Posters use the kid image proxy.")
            .Produces<KidHomeResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static IResult Unavailable(int status) =>
        Results.Problem(statusCode: status, title: "Home isn't working right now.", detail: "Try again in a little while.");
}
