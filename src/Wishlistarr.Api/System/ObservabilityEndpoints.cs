using System.Text;

namespace Wishlistarr.Api.System;

public static class ObservabilityEndpoints
{
    public static IServiceCollection AddLogBuffer(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LogOptions>()
            .Bind(configuration.GetSection(LogOptions.SectionName))
            .Validate(options => options.IsValid(), "Logs:Capacity must be from 1 to 5000.")
            .ValidateOnStart();
        services.AddSingleton<LogBuffer>();
        return services;
    }

    public static void MapObservability(this RouteGroupBuilder v1)
    {
        v1.MapGet("/system/health", (HealthReport health, CancellationToken ct) => health.CheckAsync(ct))
            .WithTags("System")
            .WithName("GetIntegrationHealth")
            .WithSummary("Runs one health check per integration. Each check has a status, a short message and a fix hint.")
            .Produces<IReadOnlyList<HealthCheckResult>>();

        v1.MapGet("/system/logs", (string? level, LogBuffer logs) =>
        {
            if (!LogBuffer.TryParseLevel(level, out LogBufferLevel? minimum))
            {
                return LevelProblem();
            }

            return Results.Ok(logs.List(minimum));
        })
            .WithTags("System")
            .WithName("ListLogs")
            .WithSummary("Returns the recent log entries kept in memory. level keeps that level and anything more severe.")
            .Produces<IReadOnlyList<LogEntry>>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        v1.MapGet("/system/logs/download", (string? level, LogBuffer logs) =>
        {
            if (!LogBuffer.TryParseLevel(level, out LogBufferLevel? minimum))
            {
                return LevelProblem();
            }

            return Results.File(Encoding.UTF8.GetBytes(logs.Download(minimum)), "text/plain", "wishlistarr.log");
        })
            .WithTags("System")
            .WithName("DownloadLogs")
            .WithSummary("Downloads the recent in-memory log entries as text. level keeps that level and anything more severe.")
            .Produces(StatusCodes.Status200OK, contentType: "text/plain")
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    private static IResult LevelProblem() =>
        Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "The level must be trace, debug, information, warning, error or critical.");
}
