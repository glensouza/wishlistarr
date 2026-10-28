using Wishlistarr.Integrations.Dtdd;

namespace Wishlistarr.Api.Tmdb;

public static class DtddEndpoints
{
    public static void MapDtdd(this RouteGroupBuilder v1)
    {
        v1.MapPost("/settings/dtdd/test", async (IDtddClient dtdd, CancellationToken ct) =>
        {
            try
            {
                bool valid = await dtdd.TestApiKeyAsync(ct);
                return Results.Ok(new DtddTestResult(valid, valid ? "The DoesTheDogDie API key works." : "DoesTheDogDie rejected the API key."));
            }
            catch (DtddNotConfiguredException)
            {
                return Results.Ok(new DtddTestResult(false, "The DoesTheDogDie API key is not configured."));
            }
            catch (Exception ex) when (ex is DtddException or HttpRequestException or TaskCanceledException)
            {
                return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "DoesTheDogDie could not be reached.");
            }
        })
            .WithTags("Settings")
            .WithName("TestDtddApiKey")
            .WithSummary("Checks the configured DoesTheDogDie API key with one item lookup. No key is sent when none is saved.")
            .Produces<DtddTestResult>()
            .ProducesProblem(StatusCodes.Status502BadGateway);
    }
}

public sealed record DtddTestResult(bool Valid, string Message);
