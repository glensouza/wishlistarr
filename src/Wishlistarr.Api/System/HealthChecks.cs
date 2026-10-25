using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Kids;
using Wishlistarr.Api.Webhooks;
using Wishlistarr.Core.Fulfilment;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Arr;
using Wishlistarr.Integrations.Seerr;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.System;

public enum HealthCheckStatus
{
    Ok,
    Warning,
    Error,
}

public sealed record HealthCheckResult(string Integration, HealthCheckStatus Status, string Message, string Fix);

public interface IHealthCheck
{
    string Name { get; }

    string DisplayName { get; }

    string Fix { get; }

    Task<HealthCheckResult> CheckAsync(IServiceProvider services, CancellationToken ct);
}

public sealed class HealthOptions
{
    public const string SectionName = "Health";

    public static readonly TimeSpan DefaultCacheDuration = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan DefaultCheckTimeout = TimeSpan.FromSeconds(8);

    public TimeSpan CacheDuration { get; set; } = DefaultCacheDuration;

    public TimeSpan CheckTimeout { get; set; } = DefaultCheckTimeout;

    public bool IsValid() =>
        this.CacheDuration >= TimeSpan.Zero
        && this.CacheDuration <= TimeSpan.FromMinutes(10)
        && this.CheckTimeout > TimeSpan.Zero
        && this.CheckTimeout <= TimeSpan.FromMinutes(1);
}

public sealed class HealthReport(
    IEnumerable<IHealthCheck> checks,
    IServiceScopeFactory scopes,
    IOptions<HealthOptions> options,
    TimeProvider time)
{
    private static readonly string[] Order = ["plex", "tmdb", "seerr", "sonarr", "radarr", "webhooks", "jobs", "database"];

    private readonly Lock gate = new();
    private Task<IReadOnlyList<HealthCheckResult>>? running;
    private IReadOnlyList<HealthCheckResult>? cached;
    private DateTimeOffset expires;

    public Task<IReadOnlyList<HealthCheckResult>> CheckAsync(CancellationToken ct = default)
    {
        Task<IReadOnlyList<HealthCheckResult>> run;
        lock (this.gate)
        {
            if (this.cached is not null && time.GetUtcNow() < this.expires)
            {
                return Task.FromResult(this.cached);
            }

            run = this.running ??= Task.Run(this.RunAllAsync, CancellationToken.None);
        }

        return run.WaitAsync(ct);
    }

    private async Task<IReadOnlyList<HealthCheckResult>> RunAllAsync()
    {
        try
        {
            Dictionary<string, IHealthCheck> byName = checks.ToDictionary(check => check.Name, StringComparer.Ordinal);
            HealthCheckResult[] results = await Task.WhenAll(Order.Select(name => this.RunAsync(byName[name], CancellationToken.None)));
            AppMetrics.PublishHealth(results);
            lock (this.gate)
            {
                this.cached = results;
                this.expires = time.GetUtcNow() + options.Value.CacheDuration;
            }

            return results;
        }
        finally
        {
            lock (this.gate)
            {
                this.running = null;
            }
        }
    }

    private async Task<HealthCheckResult> RunAsync(IHealthCheck check, CancellationToken ct)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Value.CheckTimeout);
        try
        {
            using IServiceScope scope = scopes.CreateScope();
            return await check.CheckAsync(scope.ServiceProvider, timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Checked(check.Name, HealthCheckStatus.Error, $"{check.DisplayName} did not answer in time.", check.Fix);
        }
        catch (Exception)
        {
            return Checked(check.Name, HealthCheckStatus.Error, $"{check.DisplayName} could not be checked.", check.Fix);
        }
    }

    internal static HealthCheckResult Checked(string integration, HealthCheckStatus status, string message, string fix) =>
        new(integration, status, LogRedactor.Redact(message), fix);
}

public sealed class PlexHealthCheck : IHealthCheck
{
    public string Name => "plex";

    public string DisplayName => "Plex";

    public string Fix => "Link a Plex account, choose a server, and add each profile label under Allow only in that user's Plex restrictions.";

    public async Task<HealthCheckResult> CheckAsync(IServiceProvider services, CancellationToken ct)
    {
        MediaServerTestResult test = await services.GetRequiredService<IMediaServer>().TestConnectionAsync(ct);
        bool setup = test.Code is MediaServerTestCode.NotLinked or MediaServerTestCode.NoServerSelected;
        HealthCheckStatus status = test.Valid
            ? HealthCheckStatus.Ok
            : setup ? HealthCheckStatus.Warning : HealthCheckStatus.Error;
        string message = test.Message;
        string fix = !test.Valid && setup
            ? "Link a Plex account and choose a server."
            : !test.Valid
                ? "Check that the selected Plex server is running and that Wishlistarr can reach it."
                : "In Plex, open the mapped user's restrictions and add the profile label under Allow only for movies and for TV. Do not use Exclude.";

        IReadOnlyList<HealthWarning> warnings = await HealthWarnings.GetAsync(
            services.GetRequiredService<KidProfiles>(),
            services.GetRequiredService<PlexEnforcement>(),
            ct);
        if (warnings.Count > 0 && status != HealthCheckStatus.Error)
        {
            status = HealthCheckStatus.Warning;
            message = string.Join(" ", warnings.Select(warning => warning.Message));
            fix = "In Plex, open the mapped user's restrictions and add the profile label under Allow only for movies and for TV. Do not use Exclude.";
        }

        return HealthReport.Checked(this.Name, status, message, fix);
    }
}

public sealed class TmdbHealthCheck : IHealthCheck
{
    public string Name => "tmdb";

    public string DisplayName => "TMDB";

    public string Fix => "Save a TMDB v3 API key as tmdb.api_key. Wishlistarr uses it for search and title details.";

    public async Task<HealthCheckResult> CheckAsync(IServiceProvider services, CancellationToken ct)
    {
        try
        {
            bool valid = await services.GetRequiredService<ITmdbClient>().TestApiKeyAsync(ct);
            return valid
                ? HealthReport.Checked(this.Name, HealthCheckStatus.Ok, "The TMDB API key works.", this.Fix)
                : HealthReport.Checked(this.Name, HealthCheckStatus.Error, "TMDB rejected the API key.", "Replace tmdb.api_key with a valid v3 key from themoviedb.org.");
        }
        catch (TmdbNotConfiguredException)
        {
            return HealthReport.Checked(this.Name, HealthCheckStatus.Warning, "The TMDB API key is not set.", this.Fix);
        }
        catch (Exception ex) when (ex is TmdbException or HttpRequestException or TaskCanceledException)
        {
            return HealthReport.Checked(this.Name, HealthCheckStatus.Error, "TMDB could not be reached.", "Check that Wishlistarr can reach api.themoviedb.org.");
        }
    }
}

public sealed class SeerrHealthCheck : IHealthCheck
{
    public string Name => "seerr";

    public string DisplayName => "Seerr";

    public string Fix => "Save the Seerr address, API key and user id, and give that user permission to request movies and shows.";

    public async Task<HealthCheckResult> CheckAsync(IServiceProvider services, CancellationToken ct)
    {
        try
        {
            SeerrTestResult result = await services.GetRequiredService<ISeerrBackend>().TestAsync(ct);
            if (result.Warnings.Count == 0)
            {
                return HealthReport.Checked(this.Name, HealthCheckStatus.Ok, $"Seerr {result.Version} accepted the API key.", this.Fix);
            }

            return HealthReport.Checked(this.Name, HealthCheckStatus.Warning, string.Join(" ", result.Warnings), this.Fix);
        }
        catch (SeerrNotConfiguredException)
        {
            return HealthReport.Checked(this.Name, HealthCheckStatus.Ok, "Seerr is not configured.", this.Fix);
        }
        catch (SeerrException ex)
        {
            return HealthReport.Checked(this.Name, HealthCheckStatus.Error, ex.Message, "Check the Seerr address and API key, and that Seerr is running.");
        }
    }
}

public sealed class ArrHealthCheck(ArrService service) : IHealthCheck
{
    public string Name => service.ToString().ToLowerInvariant();

    public string DisplayName => service.ToString();

    public string Fix => $"Save the {this.DisplayName} address and API key if Wishlistarr should send requests there. {this.DisplayName} must answer GET /api/v3/system/status as itself.";

    public async Task<HealthCheckResult> CheckAsync(IServiceProvider services, CancellationToken ct)
    {
        ArrTestResult result = await services.GetRequiredService<ArrBackends>().Get(service).TestAsync(ct);
        if (result.Valid)
        {
            return HealthReport.Checked(this.Name, HealthCheckStatus.Ok, result.Message, this.Fix);
        }

        if (result.Code == ArrTestCode.NotConfigured)
        {
            return HealthReport.Checked(this.Name, HealthCheckStatus.Ok, $"{this.DisplayName} is not configured.", this.Fix);
        }

        return HealthReport.Checked(this.Name, HealthCheckStatus.Error, result.Message, $"Check the {this.DisplayName} address and API key. A wrong service at that address is rejected.");
    }
}

public sealed class WebhookHealthCheck : IHealthCheck
{
    public string Name => "webhooks";

    public string DisplayName => "Inbound webhooks";

    public string Fix => "Confirm the webhook-events job is enabled. If a sender is rejected, regenerate that source's token and update the address in the sender.";

    public async Task<HealthCheckResult> CheckAsync(IServiceProvider services, CancellationToken ct)
    {
        WishlistarrDbContext db = services.GetRequiredService<WishlistarrDbContext>();
        TimeProvider time = services.GetRequiredService<TimeProvider>();
        DateTimeOffset cutoff = time.GetUtcNow() - TimeSpan.FromMinutes(2);
        DateTimeOffset? oldest = await db.WebhookEvents.AsNoTracking()
            .Where(item => item.ProcessedAt == null)
            .OrderBy(item => item.Id)
            .Select(item => (DateTimeOffset?)item.ReceivedAt)
            .FirstOrDefaultAsync(ct);
        bool stuck = oldest < cutoff;
        JobRun? last = await services.GetRequiredService<JobRunner>().GetLastRunAsync(WebhookEventProcessor.JobName, ct);
        if (last?.Outcome == JobRunOutcome.Failed)
        {
            return HealthReport.Checked(this.Name, HealthCheckStatus.Error, "The webhook-events job failed on its last run.", this.Fix);
        }

        if (stuck)
        {
            return HealthReport.Checked(this.Name, HealthCheckStatus.Warning, "Webhook events are still waiting to be processed.", this.Fix);
        }

        return HealthReport.Checked(this.Name, HealthCheckStatus.Ok, "Inbound webhooks are ready.", this.Fix);
    }
}

public sealed class JobsHealthCheck : IHealthCheck
{
    public string Name => "jobs";

    public string DisplayName => "Background jobs";

    public string Fix => "Open the failed job's recent runs and the log, fix the cause, then start the job again.";

    public async Task<HealthCheckResult> CheckAsync(IServiceProvider services, CancellationToken ct)
    {
        JobRunner runner = services.GetRequiredService<JobRunner>();
        List<JobRun> failed = [];
        foreach (JobDefinition definition in runner.Definitions.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            JobRun? last = await runner.GetLastRunAsync(definition.Name, ct);
            if (last?.Outcome == JobRunOutcome.Failed)
            {
                failed.Add(last);
            }
        }

        if (failed.Count == 0)
        {
            return HealthReport.Checked(this.Name, HealthCheckStatus.Ok, "No background job has failed.", this.Fix);
        }

        if (failed.Count == 1)
        {
            string error = failed[0].Error is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text) ? text : "The run failed.";
            return HealthReport.Checked(this.Name, HealthCheckStatus.Error, $"The last run of {failed[0].JobName} failed: {error}", this.Fix);
        }

        string names = string.Join(", ", failed.Select(run => run.JobName));
        return HealthReport.Checked(this.Name, HealthCheckStatus.Error, $"The last run failed for {names}.", this.Fix);
    }
}

public sealed class DatabaseHealthCheck : IHealthCheck
{
    public string Name => "database";

    public string DisplayName => "Database";

    public string Fix => "Check that the data directory is writable and that wishlistarr.db is not locked or damaged. The directory comes from WISHLISTARR_DATA_DIR.";

    public async Task<HealthCheckResult> CheckAsync(IServiceProvider services, CancellationToken ct)
    {
        try
        {
            bool connected = await services.GetRequiredService<WishlistarrDbContext>().Database.CanConnectAsync(ct);
            return connected
                ? HealthReport.Checked(this.Name, HealthCheckStatus.Ok, "The database is reachable.", this.Fix)
                : HealthReport.Checked(this.Name, HealthCheckStatus.Error, "The database could not be opened.", this.Fix);
        }
        catch (Exception)
        {
            return HealthReport.Checked(this.Name, HealthCheckStatus.Error, "The database could not be opened.", this.Fix);
        }
    }
}

public static class HealthCheckRegistration
{
    public static IServiceCollection AddIntegrationHealthChecks(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<HealthOptions>()
            .Bind(configuration.GetSection(HealthOptions.SectionName))
            .Validate(options => options.IsValid(), "Health:CacheDuration must be from 0 to 10 minutes and Health:CheckTimeout above 0 and up to 1 minute.")
            .ValidateOnStart();
        services.AddSingleton<IHealthCheck, PlexHealthCheck>();
        services.AddSingleton<IHealthCheck, TmdbHealthCheck>();
        services.AddSingleton<IHealthCheck, SeerrHealthCheck>();
        services.AddSingleton<IHealthCheck>(new ArrHealthCheck(ArrService.Sonarr));
        services.AddSingleton<IHealthCheck>(new ArrHealthCheck(ArrService.Radarr));
        services.AddSingleton<IHealthCheck, WebhookHealthCheck>();
        services.AddSingleton<IHealthCheck, JobsHealthCheck>();
        services.AddSingleton<IHealthCheck, DatabaseHealthCheck>();
        services.AddSingleton<HealthReport>();
        return services;
    }
}
