using Wishlistarr.Api.Backup;
using Wishlistarr.Api.Fulfilment;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Kids;
using Wishlistarr.Api.Labels;
using Wishlistarr.Api.MediaServers;
using Wishlistarr.Api.Notifications;
using Wishlistarr.Api.Requests;
using Wishlistarr.Api.Security;
using Wishlistarr.Api.Settings;
using Wishlistarr.Api.System;
using Wishlistarr.Api.Titles;
using Wishlistarr.Api.Tmdb;
using Wishlistarr.Api.Updates;
using Wishlistarr.Api.Webhooks;
using Wishlistarr.Core.Fulfilment;
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
        v1.MapParentKidDevices();
        v1.MapRequests();
        v1.MapTmdb();
        v1.MapMediaServer();
        v1.MapArr();
        v1.MapSeerr();
        v1.MapJobs();
        v1.MapLabelGuardian();
        v1.MapBackups();
        v1.MapUpdates();
        v1.MapWebhookSettings();
        v1.MapObservability();
        v1.MapNotificationSettings();

        v1.MapGet("/system/status", (SystemStatus status, CancellationToken ct) => status.GetAsync(ct))
            .WithTags("System")
            .WithName("GetSystemStatus")
            .WithSummary("Returns the application version, uptime, background jobs and library index status.")
            .Produces<StatusResponse>();

        RouteGroupBuilder settings = v1.MapGroup("/settings").WithTags("Settings");

        settings.MapGet("/", async (SettingsStore store, CancellationToken ct) => await store.ListAsync(ct))
            .WithName("ListSettings")
            .WithSummary("Lists settings. Secret values are never returned.")
            .Produces<IReadOnlyList<SettingSummary>>();

        settings.MapPut("/{key}", async (string key, SettingUpdate? update, SettingsStore store, TmdbPurgeRunner purger, CancellationToken ct) =>
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

            if (key == UpdateChecker.CheckSettingKey)
            {
                if (!UpdateChecker.TryParseCheckEnabled(update.Value, out bool enabled))
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "The update check must be true or false.");
                }

                await store.SetAsync(key, enabled ? "true" : "false", false, ct);
                return Results.NoContent();
            }

            if (key == KidRequestFlow.TimeZoneSetting && !KidRequestFlow.TryFindTimeZone(update.Value, out _))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "The time zone is not recognized. Use an IANA name such as America/Chicago.");
            }

            if (key == FulfilmentModes.SettingKey && !FulfilmentModes.TryParse(update.Value, out _))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "The fulfilment mode is seerr, arr or none.");
            }

            if (key == TmdbRetention.SettingKey && !TmdbRetention.TryParse(update.Value, out _))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: $"The retention is a whole number of days from 1 to {TmdbRetention.MaxDays}.");
            }

            if (key == YoutubeTrailers.SettingKey)
            {
                if (!YoutubeTrailers.TryParse(update.Value, out bool trailers))
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "YouTube trailers are either true or false.");
                }

                await store.SetAsync(key, trailers ? "true" : "false", false, ct);
                return Results.NoContent();
            }

            if (key == TmdbOptions.ApiKeySetting && string.IsNullOrWhiteSpace(update.Value))
            {
                await store.DeleteAsync(key, ct);
                TmdbPurgeOutcome purge = await purger.PurgeAllAsync();
                if (purge.Failed)
                {
                    return TmdbEndpoints.PurgeFailed();
                }

                return purge.Completed ? Results.NoContent() : Results.Accepted();
            }

            await store.SetAsync(key, update.Value, update.IsSecret || key == TmdbOptions.ApiKeySetting, ct);
            return Results.NoContent();
        })
            .WithName("PutSetting")
            .WithSummary("Creates or replaces a setting, encrypting it when flagged as secret. An empty tmdb.api_key removes the key and purges stored TMDB data; tmdb.retention_days accepts 1 to 180; kids.youtube_trailers accepts true or false; fulfilment.mode accepts seerr, arr or none.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
    }
}

public sealed record SettingUpdate(string? Value, bool IsSecret = false);
