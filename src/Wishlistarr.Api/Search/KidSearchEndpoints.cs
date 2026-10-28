using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Wishlistarr.Api.Kids;
using Wishlistarr.Api.Tmdb;
using Wishlistarr.Integrations.Fanart;
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
            .WithSummary("Searches movies and shows for the signed-in profile. Titles the profile's content policy blocks, or whose rating or keywords cannot be checked in time, are never returned. Unrated titles come back masked, without poster or overview and flagged needsParent. The response carries no totals.")
            .Produces<KidSearchResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        kid.MapGet("/images/tmdb/{size}/{file}", (string size, string file, TmdbImageProxy proxy, HttpContext http, CancellationToken ct) =>
            KidSearch.IsImageSize(size)
                ? TmdbEndpoints.ServeImageAsync(size, file, proxy, http, ct)
                : Task.FromResult<IResult>(Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The image size or path is not allowed.")))
            .WithTags("Images")
            .WithName("GetKidTmdbImage")
            .WithSummary("Proxies a TMDB image for a signed-in profile. Kids can request w185, w342 and w1280; any other size is rejected.")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        kid.MapGet("/images/fanart/{size}/{token}", async (string size, string token, FanartClient fanart, HttpContext http, CancellationToken ct) =>
        {
            if (token.Length > 800 || size is not ("full" or "preview" or "bigpreview")) return Results.BadRequest();
            string url;
            try { url = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token)); }
            catch (FormatException) { return Results.BadRequest(); }
            if (!FanartClient.IsAllowedUrl(url)) return Results.BadRequest();
            try
            {
                TmdbImageContent? image = await fanart.GetImageAsync(url, size, ct);
                if (image is null) return Results.NotFound();
                http.Response.Headers.CacheControl = "private, max-age=86400";
                http.Response.Headers.XContentTypeOptions = "nosniff";
                return Results.Bytes(image.Bytes, image.ContentType);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
            {
                return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The image could not be retrieved.");
            }
        })
            .WithTags("Images")
            .WithName("GetKidFanartImage")
            .WithSummary("Proxies fanart.tv artwork for a signed-in profile.")
            .Produces(StatusCodes.Status200OK, contentType: "image/png")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status502BadGateway);
    }

    private static IResult Unavailable(int status) =>
        Results.Problem(statusCode: status, title: "Search isn't working right now.", detail: "Try again in a little while.");
}
