using Wishlistarr.Core.Fulfilment;
using Wishlistarr.Integrations.Seerr;

namespace Wishlistarr.Api.Fulfilment;

public static class SeerrEndpoints
{
    public const int MaxApiKeyLength = 200;

    public static void MapSeerr(this RouteGroupBuilder v1)
    {
        v1.MapGet("/settings/seerr", async (SeerrConnectionStore store, CancellationToken ct) =>
            Results.Ok(await store.GetViewAsync(ct)))
            .WithTags("Settings")
            .WithName("GetSeerrSettings")
            .WithSummary("Returns the saved Seerr connection. The API key is never returned.")
            .Produces<SeerrSettingsView>();

        v1.MapPut("/settings/seerr", async (SeerrSettingsUpdate? update, SeerrConnectionStore store, CancellationToken ct) =>
        {
            if (update is null || !SeerrSettings.TryNormalizeUrl(update.Url, out Uri url))
            {
                return Problem("The address must be a full http or https URL without a username, query or fragment.");
            }

            SeerrSettingsView current = await store.GetViewAsync(ct);
            bool sameUrl = SeerrSettings.TryNormalizeUrl(current.Url, out Uri saved) && saved == url;
            string? apiKey = string.IsNullOrWhiteSpace(update.ApiKey) ? null : update.ApiKey.Trim();
            if (apiKey is { Length: > MaxApiKeyLength })
            {
                return Problem($"API keys are limited to {MaxApiKeyLength} characters.");
            }

            if (apiKey is not null && apiKey.Any(char.IsControl))
            {
                return Problem("The API key contains characters that are not allowed.");
            }

            if (apiKey is null && !(current.HasApiKey && sameUrl))
            {
                return Problem(current.HasApiKey
                    ? "Enter the API key again when changing the address."
                    : "An API key is required.");
            }

            if (update.UserId <= 0)
            {
                return Problem("The Seerr user id must be a positive number.");
            }

            await store.SaveAsync(url, apiKey, update.UserId, ct);
            return Results.Ok(await store.GetViewAsync(ct));
        })
            .WithTags("Settings")
            .WithName("PutSeerrSettings")
            .WithSummary("Saves the Seerr connection, encrypting the API key. Leave the key out to keep the saved one.")
            .Produces<SeerrSettingsView>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        v1.MapDelete("/settings/seerr", async (SeerrConnectionStore store, CancellationToken ct) =>
        {
            await store.DeleteAsync(ct);
            return Results.NoContent();
        })
            .WithTags("Settings")
            .WithName("DeleteSeerrSettings")
            .WithSummary("Forgets the saved Seerr connection and API key.")
            .Produces(StatusCodes.Status204NoContent);

        v1.MapPost("/settings/seerr/test", (ISeerrBackend seerr, CancellationToken ct) =>
            RunAsync(() => seerr.TestAsync(ct)))
            .WithTags("Settings")
            .WithName("TestSeerrConnection")
            .WithSummary("Checks the saved Seerr address and API key, and warns when the user lacks auto-approve.")
            .Produces<SeerrTestResult>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        RouteGroupBuilder group = v1.MapGroup("/seerr").WithTags("Seerr");

        group.MapPost("/requests", (SeerrSubmitRequest? request, ISeerrBackend seerr, CancellationToken ct) =>
            RunAsync(() =>
            {
                if (request is null)
                {
                    throw new ArgumentException("A request body is required.");
                }

                return seerr.SubmitAsync(request, ct);
            }))
            .WithName("SubmitSeerrRequest")
            .WithSummary("Attaches to an existing Seerr request for the title, or creates one as the configured user.")
            .Produces<SeerrSubmission>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        group.MapGet("/requests/{id}", (int id, ISeerrBackend seerr, CancellationToken ct) =>
            RunAsync(() => seerr.GetRequestAsync(id, ct)))
            .WithName("GetSeerrRequest")
            .WithSummary("Reads the status of a Seerr request.")
            .Produces<SeerrTrackedRequest>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway);
    }

    private static IResult Problem(string title) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);

    private static async Task<IResult> RunAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: ex.Message);
        }
        catch (SeerrNotConfiguredException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: ex.Message);
        }
        catch (SeerrException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: ex.Message);
        }
    }
}

public sealed record SeerrSettingsUpdate(string? Url, string? ApiKey, int UserId);
