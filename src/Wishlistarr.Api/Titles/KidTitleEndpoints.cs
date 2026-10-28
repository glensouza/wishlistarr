using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
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

        kid.MapGet("/titles/{kind}/{tmdbId:int}/trailer", async (string kind, int tmdbId, HttpContext context, KidTitleDetails titles, CancellationToken ct) =>
        {
            if (!TryKind(kind, out MediaKind mediaKind) || tmdbId <= 0) return Results.BadRequest();
            if (!int.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None, CultureInfo.InvariantCulture, out int profileId)) return Results.Forbid();
            string? range = context.Request.Headers.Range.FirstOrDefault();
            if (range is not null && (!RangeHeaderValue.TryParse(range, out RangeHeaderValue? parsedRange) || parsedRange.Unit != "bytes" || parsedRange.Ranges.Count != 1)) return Results.BadRequest();
            try
            {
                using HttpResponseMessage? response = await titles.OpenPlexTrailerAsync(profileId, mediaKind, tmdbId, range, ct);
                if (response is null) return Results.NotFound();
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
                if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
                    return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The trailer could not be played.");
                context.Response.StatusCode = (int)response.StatusCode;
                context.Response.ContentType = "video/mp4";
                context.Response.Headers.CacheControl = "private, no-store";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                context.Response.Headers.AcceptRanges = "bytes";
                if (response.Content.Headers.ContentLength is long length) context.Response.ContentLength = length;
                if (response.Content.Headers.ContentRange is not null) context.Response.Headers.ContentRange = response.Content.Headers.ContentRange.ToString();
                await response.Content.CopyToAsync(context.Response.Body, ct);
                return Results.Empty;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or MediaServerException or TmdbException or JsonException)
            {
                return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The trailer could not be played.");
            }
        })
            .WithTags("Kid")
            .WithName("GetKidPlexTrailer")
            .WithSummary("Streams a trailer for an allowed title from its Plex library extras.")
            .Produces(StatusCodes.Status200OK, contentType: "video/mp4")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway);
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
