using System.Globalization;
using System.Security.Claims;
using Wishlistarr.Api.Tmdb;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Search;

public static class KidSearchEndpoints
{
    public static void MapKidSearch(this RouteGroupBuilder kid)
    {
        kid.MapGet("/search", async (string? q, int? page, HttpContext context, KidSearch search, CancellationToken ct) =>
        {
            if (!KidSearch.TryNormalize(q, out string query))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: $"Searches are {KidSearch.MinQueryLength} to {KidSearch.MaxQueryLength} characters.");
            }

            int pageNumber = page ?? 1;
            if (pageNumber is < 1 or > KidSearch.MaxPage)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: $"The page must be 1 to {KidSearch.MaxPage}.");
            }

            if (!int.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None, CultureInfo.InvariantCulture, out int profileId))
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Sign in to a profile first.");
            }

            try
            {
                KidSearchResponse? response = await search.SearchAsync(profileId, query, pageNumber, ct);
                if (response is null)
                {
                    return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Sign in to a profile first.");
                }

                context.Response.Headers.CacheControl = "private, no-store";
                return Results.Ok(response);
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
            .WithName("SearchKidTitles")
            .WithSummary("Searches movies and shows for the signed-in profile. Titles the profile's content policy blocks, or whose rating cannot be checked in time, are never returned, and the response carries no totals.")
            .Produces<KidSearchResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        kid.MapGet("/images/tmdb/{size}/{file}", TmdbEndpoints.ServeImageAsync)
            .WithTags("Images")
            .WithName("GetKidTmdbImage")
            .WithSummary("Proxies a poster from image.tmdb.org for a signed-in profile.")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway);
    }

    private static IResult Unavailable(int status) =>
        Results.Problem(statusCode: status, title: "Search isn't working right now.", detail: "Try again in a little while.");
}
