using System.Diagnostics.Metrics;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;

namespace Wishlistarr.Api.System;

public static class AppMetrics
{
    public const string Name = "Wishlistarr";

    private static readonly Meter Meter = new(Name);
    private static readonly Lock Gate = new();
    private static IReadOnlyList<HealthCheckResult> HealthSnapshot = [];

    private static readonly ObservableGauge<int> IntegrationHealth = Meter.CreateObservableGauge(
        "wishlistarr.integration.health",
        ObserveHealth,
        description: "0 when the integration check is ok, 1 when it warns and 2 when it fails.");

    private static readonly Counter<long> Jobs = Meter.CreateCounter<long>(
        "wishlistarr.jobs",
        description: "Background job runs by job name and outcome.");

    private static readonly Counter<long> Requests = Meter.CreateCounter<long>(
        "wishlistarr.requests",
        description: "Request events by kind and state.");

    public static void Start()
    {
        _ = IntegrationHealth;
        _ = Jobs;
        _ = Requests;
    }

    public static void RecordJob(string job, string outcome) =>
        Jobs.Add(1, Tag("job", job), Tag("outcome", outcome));

    public static void RecordRequest(MediaKind kind, RequestState state) =>
        Requests.Add(1, Tag("kind", kind == MediaKind.Movie ? "movie" : "show"), Tag("state", State(state)));

    public static void PublishHealth(IReadOnlyList<HealthCheckResult> results)
    {
        lock (Gate)
        {
            HealthSnapshot = results;
        }
    }

    private static IEnumerable<Measurement<int>> ObserveHealth()
    {
        IReadOnlyList<HealthCheckResult> current;
        lock (Gate)
        {
            current = HealthSnapshot;
        }

        foreach (HealthCheckResult result in current)
        {
            int score = result.Status switch
            {
                HealthCheckStatus.Ok => 0,
                HealthCheckStatus.Warning => 1,
                _ => 2,
            };
            yield return new Measurement<int>(score, Tag("integration", result.Integration));
        }
    }

    private static KeyValuePair<string, object?> Tag(string key, string value) => new(key, value);

    private static string State(RequestState state) => state switch
    {
        RequestState.Pending => "pending",
        RequestState.Approved => "approved",
        RequestState.Denied => "denied",
        RequestState.Fulfilling => "fulfilling",
        RequestState.Available => "available",
        RequestState.Labeled => "labeled",
        _ => "failed",
    };
}
