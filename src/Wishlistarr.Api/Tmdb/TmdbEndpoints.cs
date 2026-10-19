using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Tmdb;

public static class TmdbEndpoints
{
    public static void MapTmdb(this RouteGroupBuilder v1)
    {
        v1.MapPost("/settings/tmdb/test", async (ITmdbClient tmdb, CancellationToken ct) =>
        {
            try
            {
                bool valid = await tmdb.TestApiKeyAsync(ct);
                return Results.Ok(new TmdbTestResult(valid, valid ? "The TMDB API key works." : "TMDB rejected the API key."));
            }
            catch (TmdbNotConfiguredException)
            {
                return Results.Ok(new TmdbTestResult(false, "The TMDB API key is not configured."));
            }
            catch (Exception ex) when (ex is TmdbException or HttpRequestException or TaskCanceledException)
            {
                return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "TMDB could not be reached.");
            }
        })
            .WithTags("Settings")
            .WithName("TestTmdbApiKey")
            .WithSummary("Checks the configured TMDB API key against TMDB.")
            .Produces<TmdbTestResult>()
            .ProducesProblem(StatusCodes.Status502BadGateway);

        v1.MapGet("/images/tmdb/{size}/{file}", async (string size, string file, TmdbImageProxy proxy, HttpContext http, CancellationToken ct) =>
        {
            if (!TmdbImageProxy.IsAllowed(size, file))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The image size or path is not allowed.");
            }

            try
            {
                TmdbImageContent? image = await proxy.FetchAsync(size, file, ct);
                if (image is null)
                {
                    return Results.NotFound();
                }

                http.Response.Headers.CacheControl = "private, max-age=86400";
                http.Response.Headers.XContentTypeOptions = "nosniff";
                return Results.Bytes(image.Bytes, image.ContentType, enableRangeProcessing: false);
            }
            catch (Exception ex) when (ex is TmdbException or HttpRequestException or TaskCanceledException)
            {
                return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The image could not be retrieved.");
            }
        })
            .WithTags("Images")
            .WithName("GetTmdbImage")
            .WithSummary("Proxies a poster, backdrop or profile image from image.tmdb.org.")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway);
    }
}

public sealed record TmdbTestResult(bool Valid, string Message);
