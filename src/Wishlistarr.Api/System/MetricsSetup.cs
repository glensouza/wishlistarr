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
        MetricsOptions options = builder.Configuration.GetSection(MetricsOptions.SectionName).Get<MetricsOptions>() ?? new MetricsOptions();
        builder.Services.AddSingleton(options);
        AppMetrics.Start();
        if (!options.Enabled)
        {
            return;
        }

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService("wishlistarr", autoGenerateServiceInstanceId: false))
                .AddMeter(AppMetrics.Name)
                .AddPrometheusExporter());
    }

    public static void MapWishlistarrMetrics(this WebApplication app)
    {
        if (app.Services.GetRequiredService<MetricsOptions>().Enabled)
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
