using Wishlistarr.Core.Fulfilment;
using Wishlistarr.Integrations.Arr;

namespace Wishlistarr.Api.Fulfilment;

public static class ArrEndpoints
{
    public const int MaxApiKeyLength = 200;
    public const int MaxRootFolderLength = 500;
    public const int MaxTags = 50;

    public static void MapArr(this RouteGroupBuilder v1)
    {
        MapService(v1, ArrService.Sonarr);
        MapService(v1, ArrService.Radarr);
    }

    private static void MapService(RouteGroupBuilder v1, ArrService service)
    {
        string name = service.ToString();
        string route = name.ToLowerInvariant();

        v1.MapGet($"/settings/{route}", async (ArrConnectionStore store, CancellationToken ct) =>
            Results.Ok(await store.GetViewAsync(service, ct)))
            .WithTags("Settings")
            .WithName($"Get{name}Settings")
            .WithSummary($"Returns the saved {name} connection. The API key is never returned.")
            .Produces<ArrSettingsView>();

        v1.MapPut($"/settings/{route}", async (ArrSettingsUpdate? update, ArrConnectionStore store, CancellationToken ct) =>
        {
            if (update is null || !ArrSettings.TryNormalizeUrl(update.Url, out Uri url))
            {
                return Problem("The address must be a full http or https URL without a username, query or fragment.");
            }

            ArrSettingsView current = await store.GetViewAsync(service, ct);
            bool sameUrl = ArrSettings.TryNormalizeUrl(current.Url, out Uri saved) && saved == url;
            string? apiKey = string.IsNullOrWhiteSpace(update.ApiKey) ? null : update.ApiKey.Trim();
            if (apiKey is { Length: > MaxApiKeyLength })
            {
                return Problem($"API keys are limited to {MaxApiKeyLength} characters.");
            }

            if (apiKey is null && !(current.HasApiKey && sameUrl))
            {
                return Problem(current.HasApiKey
                    ? "Enter the API key again when changing the address."
                    : "An API key is required.");
            }

            if (update.QualityProfileId is <= 0)
            {
                return Problem("The quality profile id is not valid.");
            }

            string? rootFolder = string.IsNullOrWhiteSpace(update.RootFolderPath) ? null : update.RootFolderPath.Trim();
            if (rootFolder is { Length: > MaxRootFolderLength })
            {
                return Problem($"Root folders are limited to {MaxRootFolderLength} characters.");
            }

            int[] tagIds = [.. (update.TagIds ?? []).Distinct()];
            if (tagIds.Length > MaxTags || tagIds.Any(id => id <= 0))
            {
                return Problem("The tag ids are not valid.");
            }

            await store.SaveAsync(service, url, apiKey, update.QualityProfileId, rootFolder, tagIds, ct);
            return Results.Ok(await store.GetViewAsync(service, ct));
        })
            .WithTags("Settings")
            .WithName($"Put{name}Settings")
            .WithSummary($"Saves the {name} connection, encrypting the API key. Leave the key out to keep the saved one.")
            .Produces<ArrSettingsView>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        v1.MapDelete($"/settings/{route}", async (ArrConnectionStore store, CancellationToken ct) =>
        {
            await store.DeleteAsync(service, ct);
            return Results.NoContent();
        })
            .WithTags("Settings")
            .WithName($"Delete{name}Settings")
            .WithSummary($"Forgets the saved {name} connection and API key.")
            .Produces(StatusCodes.Status204NoContent);

        v1.MapPost($"/settings/{route}/test", async (ArrBackends backends, CancellationToken ct) =>
            Results.Ok(await backends.Get(service).TestAsync(ct)))
            .WithTags("Settings")
            .WithName($"Test{name}Connection")
            .WithSummary($"Checks that {name} answers with the saved address and API key.")
            .Produces<ArrTestResult>();

        RouteGroupBuilder group = v1.MapGroup($"/{route}").WithTags(name);

        group.MapGet("/profiles", (ArrBackends backends, CancellationToken ct) =>
            RunAsync(async () => await backends.Get(service).GetQualityProfilesAsync(ct)))
            .WithName($"List{name}QualityProfiles")
            .WithSummary($"Lists the quality profiles configured in {name}.")
            .Produces<IReadOnlyList<ArrQualityProfile>>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        group.MapGet("/rootfolders", (ArrBackends backends, CancellationToken ct) =>
            RunAsync(async () => await backends.Get(service).GetRootFoldersAsync(ct)))
            .WithName($"List{name}RootFolders")
            .WithSummary($"Lists the root folders configured in {name}.")
            .Produces<IReadOnlyList<ArrRootFolder>>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        group.MapGet("/tags", (ArrBackends backends, CancellationToken ct) =>
            RunAsync(async () => await backends.Get(service).GetTagsAsync(ct)))
            .WithName($"List{name}Tags")
            .WithSummary($"Lists the tags defined in {name}.")
            .Produces<IReadOnlyList<ArrTag>>()
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
        catch (ArrNotConfiguredException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: ex.Message);
        }
        catch (ArrException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: ex.Message);
        }
    }
}

public sealed record ArrSettingsUpdate(
    string? Url,
    string? ApiKey,
    int? QualityProfileId,
    string? RootFolderPath,
    IReadOnlyList<int>? TagIds);
