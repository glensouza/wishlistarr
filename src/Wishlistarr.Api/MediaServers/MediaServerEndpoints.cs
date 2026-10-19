using System.Text.Json;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Api.MediaServers;

public static class MediaServerEndpoints
{
    private const string Tag = "Plex";

    public static void MapMediaServer(this RouteGroupBuilder v1)
    {
        v1.MapPost("/settings/plex/test", (IMediaServer server, CancellationToken ct) =>
            RunAsync(() => server.TestConnectionAsync(ct)))
            .WithTags("Settings")
            .WithName("TestPlexConnection")
            .WithSummary("Checks that the selected Plex server answers with the saved sign-in.")
            .Produces<MediaServerTestResult>()
            .ProducesProblem(StatusCodes.Status502BadGateway);

        RouteGroupBuilder plex = v1.MapGroup("/plex").WithTags(Tag);

        plex.MapGet("/status", (IMediaServerAccount account, CancellationToken ct) =>
            RunAsync(() => account.GetStatusAsync(ct)))
            .WithName("GetPlexStatus")
            .WithSummary("Reports whether Plex is linked, the client identifier and the selected server.")
            .Produces<MediaServerAccountStatus>();

        plex.MapPost("/link", (IMediaServerAccount account, CancellationToken ct) =>
            RunAsync(() => account.StartLinkAsync(ct)))
            .WithName("StartPlexLink")
            .WithSummary("Starts the plex.tv sign-in and returns the page the parent opens to approve it.")
            .Produces<MediaServerLinkStart>()
            .ProducesProblem(StatusCodes.Status502BadGateway);

        plex.MapPost("/link/{id}/check", (string id, IMediaServerAccount account, CancellationToken ct) =>
            RunAsync(() => account.CheckLinkAsync(id, ct), "Link request not found."))
            .WithName("CheckPlexLink")
            .WithSummary("Checks whether the sign-in was approved and, once it is, saves the Plex sign-in.")
            .Produces<MediaServerLinkCheck>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        plex.MapDelete("/link", async (IMediaServerAccount account, CancellationToken ct) =>
        {
            await account.UnlinkAsync(ct);
            return Results.NoContent();
        })
            .WithName("UnlinkPlex")
            .WithSummary("Forgets the Plex sign-in and the selected server.")
            .Produces(StatusCodes.Status204NoContent);

        plex.MapGet("/servers", (IMediaServerAccount account, CancellationToken ct) =>
            RunAsync(() => account.GetServersAsync(ct)))
            .WithName("ListPlexServers")
            .WithSummary("Lists the Plex servers the linked account can reach, with their advertised connections.")
            .Produces<IReadOnlyList<DiscoveredMediaServer>>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        plex.MapPut("/server", async (ServerSelection? selection, IMediaServerAccount account, CancellationToken ct) =>
        {
            if (selection?.ServerId is not { Length: > 0 and <= 100 } serverId)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A server id is required.");
            }

            return await RunAsync(() => account.SelectServerAsync(serverId, ct), "Server not found.");
        })
            .WithName("SelectPlexServer")
            .WithSummary("Selects a server and the best connection that answers as that server.")
            .Produces<SelectedMediaServer>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        plex.MapGet("/libraries", (IMediaServer server, CancellationToken ct) =>
            RunAsync(() => server.GetLibrariesAsync(ct)))
            .WithName("ListPlexLibraries")
            .WithSummary("Lists the libraries on the selected server.")
            .Produces<IReadOnlyList<MediaLibrary>>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        plex.MapGet("/users", (IMediaServer server, CancellationToken ct) =>
            RunAsync(() => server.GetUsersAsync(ct)))
            .WithName("ListPlexUsers")
            .WithSummary("Lists Plex Home users and friends the server is shared with.")
            .Produces<IReadOnlyList<MediaServerUser>>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        plex.MapGet("/lookup", (string? kind, string? source, string? id, IMediaServer server, CancellationToken ct) =>
        {
            if (!TryParseName(kind, out MediaKind mediaKind)
                || !TryParseName(source, out ExternalIdSource idSource)
                || !ExternalId.TryCreate(idSource, id, out ExternalId? externalId))
            {
                return Task.FromResult(Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "kind must be movie or show, source must be tmdb, tvdb or imdb, and id must be a valid id for that source."));
            }

            return RunAsync(() => server.FindByExternalIdAsync(mediaKind, externalId, ct));
        })
            .WithName("LookupPlexItem")
            .WithSummary("Finds items on the selected server by TMDB, TVDB or IMDb id.")
            .Produces<IReadOnlyList<MediaItem>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway);
    }

    private static bool TryParseName<TEnum>(string? value, out TEnum result)
        where TEnum : struct, Enum
    {
        result = default;
        string? name = Enum.GetNames<TEnum>().FirstOrDefault(candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase));
        return name is not null && Enum.TryParse(name, out result);
    }

    private static async Task<IResult> RunAsync<T>(Func<Task<T>> action, string? notFoundTitle = null)
    {
        try
        {
            T value = await action();
            return value is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: notFoundTitle ?? "Not found.")
                : Results.Ok(value);
        }
        catch (MediaServerNotConfiguredException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: ex.Message);
        }
        catch (MediaServerException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Plex could not be reached.");
        }
    }
}

public sealed record ServerSelection(string? ServerId);
