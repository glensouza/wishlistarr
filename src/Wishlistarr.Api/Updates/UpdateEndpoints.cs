using System.Net;
using System.Net.Http.Headers;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Settings;
using Wishlistarr.Api.System;

namespace Wishlistarr.Api.Updates;

public static class UpdateEndpoints
{
    public static IServiceCollection AddUpdateCheck(this IServiceCollection services)
    {
        services.AddHttpClient(UpdateChecker.HttpClientName, client =>
            {
                client.Timeout = UpdateChecker.Timeout;
                client.MaxResponseContentBufferSize = UpdateChecker.MaxResponseBytes;
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Wishlistarr", AppVersionHeader()));
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", UpdateChecker.AcceptMediaType);
                client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", UpdateChecker.ApiVersion);
            })
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None,

                // Trace and request ids would identify this server to GitHub.
                ActivityHeadersPropagator = null,
            });
        services.AddScoped<UpdateChecker>();
        services.AddBackgroundJob<UpdateCheckJob>(
            UpdateCheckJob.JobName,
            "Checks GitHub for a newer release.",
            UpdateChecker.Interval);
        return services;
    }

    public static void MapUpdates(this RouteGroupBuilder v1)
    {
        v1.MapGet("/updates", (UpdateChecker updates, CancellationToken ct) => updates.GetAsync(ct))
            .WithTags("Updates")
            .WithName("GetUpdateNotice")
            .WithSummary("Returns whether a newer GitHub release is available for a signed-in parent.")
            .Produces<UpdateNoticeResponse>();

        v1.MapPut("/updates", async (UpdateCheckUpdate? update, SettingsStore store, CancellationToken ct) =>
            {
                if (update?.CheckEnabled is not bool enabled)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "The update check must be true or false.");
                }

                await store.SetAsync(UpdateChecker.CheckSettingKey, enabled ? "true" : "false", false, ct);
                return Results.NoContent();
            })
            .WithTags("Updates")
            .WithName("PutUpdateCheck")
            .WithSummary("Turns the daily GitHub release check on or off.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    private static string AppVersionHeader()
    {
        string version = AppVersion.Current;
        return ProductInfoHeaderValue.TryParse("Wishlistarr/" + version, out ProductInfoHeaderValue? parsed)
            && parsed.Product?.Name == "Wishlistarr"
            && parsed.Product.Version == version
            && parsed.Comment is null
            ? version
            : "0.0.0";
    }
}

public sealed record UpdateCheckUpdate(bool? CheckEnabled);

public sealed record UpdateNoticeResponse(bool CheckEnabled, AvailableUpdate? Available);

public sealed record AvailableUpdate(string Version, string ReleaseNotesUrl, string UpgradeNotesUrl);
