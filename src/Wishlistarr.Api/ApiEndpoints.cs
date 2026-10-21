using Wishlistarr.Api.Fulfilment;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Kids;
using Wishlistarr.Api.MediaServers;
using Wishlistarr.Api.Requests;
using Wishlistarr.Api.Security;
using Wishlistarr.Api.Settings;
using Wishlistarr.Api.System;
using Wishlistarr.Api.Tmdb;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Integrations.Arr;
using Wishlistarr.Integrations.Seerr;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api;

public static class ApiEndpoints
{
    public const int MaxKeyLength = 200;

    public static void MapApiV1(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder v1 = app.MapGroup("/api/v1")
            .RequireParent()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        v1.MapParentAuth();
        v1.MapParentKidProfiles();
        v1.MapTmdb();
        v1.MapMediaServer();
        v1.MapArr();
        v1.MapSeerr();
        v1.MapJobs();

        v1.MapGet("/system/status", (SystemStatus status, CancellationToken ct) => status.GetAsync(ct))
            .WithTags("System")
            .WithName("GetSystemStatus")
            .WithSummary("Returns the application version, uptime and background jobs.")
            .Produces<StatusResponse>();

        RouteGroupBuilder settings = v1.MapGroup("/settings").WithTags("Settings");

        settings.MapGet("/", async (SettingsStore store, CancellationToken ct) => await store.ListAsync(ct))
            .WithName("ListSettings")
            .WithSummary("Lists settings. Secret values are never returned.")
            .Produces<IReadOnlyList<SettingSummary>>();

        settings.MapPut("/{key}", async (string key, SettingUpdate? update, SettingsStore store, CancellationToken ct) =>
        {
            if (update?.Value is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A value is required.");
            }

            if (key.Length > MaxKeyLength)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: $"Keys are limited to {MaxKeyLength} characters.");
            }

            if (SettingsStore.IsReserved(key))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: $"Keys starting with '{SettingsStore.ReservedPrefix}' are reserved.");
            }

            if (key.StartsWith(MediaServerSettings.KeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: $"Keys starting with '{MediaServerSettings.KeyPrefix}' are managed by the Plex endpoints.");
            }

            if (ArrSettings.IsManagedKey(key))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Keys starting with 'sonarr.' or 'radarr.' are managed by the Sonarr and Radarr endpoints.");
            }

            if (SeerrSettings.IsManagedKey(key))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Keys starting with 'seerr.' are managed by the Seerr endpoints.");
            }

            if (key == KidRequestFlow.TimeZoneSetting && !KidRequestFlow.TryFindTimeZone(update.Value, out _))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "The time zone is not recognized. Use an IANA name such as America/Chicago.");
            }

            await store.SetAsync(key, update.Value, update.IsSecret || key == TmdbOptions.ApiKeySetting, ct);
            return Results.NoContent();
        })
            .WithName("PutSetting")
            .WithSummary("Creates or replaces a setting, encrypting it when flagged as secret.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }
}

public sealed record SettingUpdate(string? Value, bool IsSecret = false);
