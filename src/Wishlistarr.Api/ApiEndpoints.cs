using Wishlistarr.Api.Security;
using Wishlistarr.Api.Settings;
using Wishlistarr.Api.System;

namespace Wishlistarr.Api;

public static class ApiEndpoints
{
    public const int MaxKeyLength = 200;

    public static void MapApiV1(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder v1 = app.MapGroup("/api/v1").AddEndpointFilter<ApiKeyEndpointFilter>();

        v1.MapGet("/system/status", (SystemStatus status) => status.Get());

        v1.MapGet("/settings", async (SettingsStore store, CancellationToken ct) => await store.ListAsync(ct));

        v1.MapPut("/settings/{key}", async (string key, SettingUpdate? update, SettingsStore store, CancellationToken ct) =>
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
        });
    }
}

public sealed record SettingUpdate(string? Value, bool IsSecret);
