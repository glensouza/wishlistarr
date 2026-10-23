using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Wishlistarr.Api.Security;

namespace Wishlistarr.Api.System;

public sealed class MetricsOptions
{
    public const string SectionName = "Metrics";

    public bool Enabled { get; set; }
}

public static class MetricsSetup
{
    public const string Path = "/metrics";

    public static void AddWishlistarrMetrics(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<MetricsOptions>()
            .Bind(builder.Configuration.GetSection(MetricsOptions.SectionName));
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService("wishlistarr", autoGenerateServiceInstanceId: false))
                .AddMeter(AppMetrics.Name)
                .AddPrometheusExporter());
        AppMetrics.Start();
    }

    public static void MapWishlistarrMetrics(this WebApplication app)
    {
        if (app.Configuration.GetValue<bool>($"{MetricsOptions.SectionName}:Enabled"))
        {
            app.MapPrometheusScrapingEndpoint(Path)
                .RequireAuthorization(Policies.Parent)
                .ExcludeFromDescription();
            return;
        }

        app.MapGet(Path, () => Results.NotFound())
            .RequireAuthorization(Policies.Parent)
            .ExcludeFromDescription();
    }
}
