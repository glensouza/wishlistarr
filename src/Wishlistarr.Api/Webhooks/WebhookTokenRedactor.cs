using Serilog.Core;
using Serilog.Events;

namespace Wishlistarr.Api.Webhooks;

public sealed class WebhookTokenRedactor : ILogEventEnricher
{
    public const string RoutePrefix = "/api/v1/webhooks/";
    private static readonly string[] PathProperties = ["RequestPath", "Path"];

    public static string Redact(string path)
    {
        if (!path.StartsWith(RoutePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        int token = path.IndexOf('/', RoutePrefix.Length);
        return token < 0 ? path : string.Concat(path.AsSpan(0, token + 1), "***");
    }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (string name in PathProperties)
        {
            if (logEvent.Properties.TryGetValue(name, out LogEventPropertyValue? value)
                && value is ScalarValue { Value: string path }
                && Redact(path) is { } redacted
                && redacted != path)
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, new ScalarValue(redacted)));
            }
        }
    }
}
