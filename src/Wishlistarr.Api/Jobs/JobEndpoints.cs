using Microsoft.Extensions.Options;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Jobs;

public static class JobEndpoints
{
    public const int DefaultRunLimit = 20;
    public const int MaxRunLimit = 100;

    public static IServiceCollection AddBackgroundJobs(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JobOptions>()
            .Bind(configuration.GetSection(JobOptions.SectionName))
            .Validate(options => options.IsValid(), "The Jobs configuration section is invalid.")
            .ValidateOnStart();
        services.AddSingleton<JobRunner>();
        services.AddSingleton<JobScheduler>();
        services.AddHostedService(provider => provider.GetRequiredService<JobScheduler>());
        services.AddBackgroundJob<JobHistoryCleanup>(
            JobHistoryCleanup.JobName,
            "Removes job runs older than the configured retention.",
            TimeSpan.FromDays(1));
        return services;
    }

    public static IServiceCollection AddBackgroundJob<TJob>(
        this IServiceCollection services,
        string name,
        string description,
        TimeSpan? defaultInterval)
        where TJob : class, IBackgroundJob
    {
        services.AddScoped<TJob>();
        services.AddSingleton(new JobDefinition(name, description, defaultInterval, typeof(TJob)));
        return services;
    }

    public static async Task<IReadOnlyList<JobSummary>> ListJobsAsync(
        this JobRunner runner,
        JobOptions options,
        CancellationToken ct)
    {
        List<JobSummary> jobs = [];
        foreach (JobDefinition definition in runner.Definitions.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            JobSchedule schedule = options.Resolve(definition);
            JobRun? last = await runner.GetLastRunAsync(definition.Name, ct);
            jobs.Add(new JobSummary(
                definition.Name,
                definition.Description,
                schedule.Enabled,
                (long?)schedule.Interval?.TotalSeconds,
                runner.IsRunning(definition.Name),
                last is null ? null : JobRunSummary.From(last)));
        }

        return jobs;
    }

    public static void MapJobs(this RouteGroupBuilder v1)
    {
        RouteGroupBuilder jobs = v1.MapGroup("/jobs").WithTags("Jobs");

        jobs.MapGet("/", async (JobRunner runner, IOptions<JobOptions> options, CancellationToken ct) =>
            await runner.ListJobsAsync(options.Value, ct))
            .WithName("ListJobs")
            .WithSummary("Lists registered jobs with their schedule and last run.")
            .Produces<IReadOnlyList<JobSummary>>();

        jobs.MapGet("/{name}/runs", async (string name, int? limit, JobRunner runner, CancellationToken ct) =>
        {
            if (runner.Find(name) is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The job does not exist.");
            }

            int take = Math.Clamp(limit ?? DefaultRunLimit, 1, MaxRunLimit);
            IReadOnlyList<JobRun> runs = await runner.ListRunsAsync(name, take, ct);
            return Results.Ok(runs.Select(JobRunSummary.From));
        })
            .WithName("ListJobRuns")
            .WithSummary("Lists the most recent runs of a job, newest first.")
            .Produces<IEnumerable<JobRunSummary>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        jobs.MapPost("/{name}/run", async (string name, JobRunner runner, CancellationToken ct) =>
        {
            JobStartResult result = await runner.StartAsync(name, JobRunTrigger.Manual, ct);
            return result.Status switch
            {
                JobStartStatus.Started => Results.Accepted($"/api/v1/jobs/{name}/runs", new JobTriggered(result.RunId!.Value)),
                JobStartStatus.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "The job does not exist."),
                JobStartStatus.AlreadyRunning => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "The job is already running."),
                _ => Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "The application is shutting down."),
            };
        })
            .WithName("RunJob")
            .WithSummary("Starts a job now unless it is already running.")
            .Produces<JobTriggered>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }
}

public sealed record JobSummary(
    string Name,
    string Description,
    bool Enabled,
    long? IntervalSeconds,
    bool IsRunning,
    JobRunSummary? LastRun);

public sealed record JobRunSummary(
    int Id,
    JobRunTrigger Trigger,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    JobRunOutcome Outcome,
    string? Error)
{
    public static JobRunSummary From(JobRun run) =>
        new(run.Id, run.Trigger, run.StartedAt, run.FinishedAt, run.Outcome, run.Error);
}

public sealed record JobTriggered(int RunId);
