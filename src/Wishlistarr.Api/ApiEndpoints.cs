using Wishlistarr.Api.Security;
using Wishlistarr.Api.Settings;
using Wishlistarr.Api.System;

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

        v1.MapGet("/system/status", (SystemStatus status) => status.Get())
            .WithTags("System")
            .WithName("GetSystemStatus")
            .WithSummary("Returns the application version and uptime.")
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

            await store.SetAsync(key, update.Value, update.IsSecret, ct);
            return Results.NoContent();
        })
            .WithName("PutSetting")
            .WithSummary("Creates or replaces a setting, encrypting it when flagged as secret.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }
}

public sealed record SettingUpdate(string? Value, bool IsSecret = false);
