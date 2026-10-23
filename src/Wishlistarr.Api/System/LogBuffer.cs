using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Events;

namespace Wishlistarr.Api.System;

public sealed class LogOptions
{
    public const string SectionName = "Logs";

    public const int DefaultCapacity = 1000;

    public const int MaxCapacity = 5000;

    public int Capacity { get; set; } = DefaultCapacity;

    public bool IsValid() => this.Capacity is >= 1 and <= MaxCapacity;
}

public enum LogBufferLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical,
}

public sealed record LogEntry(DateTimeOffset At, LogBufferLevel Level, string Category, string Message);

public sealed class LogBuffer(IOptions<LogOptions> options) : ILogEventSink
{
    private readonly LogEntry[] slots = new LogEntry[options.Value.Capacity];
    private readonly Lock gate = new();
    private int next;
    private int count;

    public void Emit(LogEvent logEvent)
    {
        foreach (KeyValuePair<string, LogEventPropertyValue> property in logEvent.Properties.ToArray())
        {
            if (property.Value is not ScalarValue { Value: string text })
            {
                continue;
            }

            string redacted = LogRedactor.IsSecretName(property.Key) ? "***" : LogRedactor.Redact(text);
            if (!string.Equals(redacted, text, StringComparison.Ordinal))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key, new ScalarValue(redacted)));
            }
        }

        string message = LogRedactor.Redact(logEvent.RenderMessage());
        if (logEvent.Exception is not null)
        {
            message = LogRedactor.Redact(message + " " + logEvent.Exception.Message);
        }

        LogEntry entry = new(logEvent.Timestamp.ToUniversalTime(), Map(logEvent.Level), Category(logEvent), message);
        lock (this.gate)
        {
            this.slots[this.next] = entry;
            this.next = (this.next + 1) % this.slots.Length;
            if (this.count < this.slots.Length)
            {
                this.count++;
            }
        }
    }

    public IReadOnlyList<LogEntry> List(LogBufferLevel? minimum)
    {
        lock (this.gate)
        {
            List<LogEntry> items = new(this.count);
            int start = this.count == 0 ? 0 : (this.next - this.count + this.slots.Length) % this.slots.Length;
            for (int index = 0; index < this.count; index++)
            {
                LogEntry entry = this.slots[(start + index) % this.slots.Length];
                if (minimum is null || entry.Level >= minimum)
                {
                    items.Add(entry);
                }
            }

            return items;
        }
    }

    public string Download(LogBufferLevel? minimum)
    {
        StringBuilder text = new();
        foreach (LogEntry entry in this.List(minimum))
        {
            text.Append(entry.At.ToString("O", CultureInfo.InvariantCulture));
            text.Append(' ');
            text.Append(entry.Level.ToString().ToLowerInvariant());
            text.Append(' ');
            text.Append(entry.Category);
            text.Append(' ');
            text.Append(entry.Message);
            text.Append('\n');
        }

        return text.ToString();
    }

    public static bool TryParseLevel(string? value, out LogBufferLevel? level)
    {
        level = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!Enum.TryParse(value, ignoreCase: true, out LogBufferLevel parsed) || !Enum.IsDefined(parsed))
        {
            return false;
        }

        level = parsed;
        return true;
    }

    private static string Category(LogEvent logEvent)
    {
        if (logEvent.Properties.TryGetValue("SourceContext", out LogEventPropertyValue? source)
            && source is ScalarValue { Value: string name }
            && name.Length > 0)
        {
            return name;
        }

        return "Wishlistarr";
    }

    private static LogBufferLevel Map(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => LogBufferLevel.Trace,
        LogEventLevel.Debug => LogBufferLevel.Debug,
        LogEventLevel.Information => LogBufferLevel.Information,
        LogEventLevel.Warning => LogBufferLevel.Warning,
        LogEventLevel.Error => LogBufferLevel.Error,
        _ => LogBufferLevel.Critical,
    };
}
