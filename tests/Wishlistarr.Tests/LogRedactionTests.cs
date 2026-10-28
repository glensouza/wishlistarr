using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Wishlistarr.Api.System;

namespace Wishlistarr.Tests;

public sealed class LogRedactionTests
{
    private const string Secret = "zz-secret-qq";

    [Theory]
    [InlineData("PlexToken")]
    [InlineData("AuthToken")]
    [InlineData("authToken")]
    [InlineData("KidPin")]
    [InlineData("ApiKeyHeader")]
    [InlineData("Cookie")]
    [InlineData("SessionSecret")]
    [InlineData("Password")]
    [InlineData("Authorization")]
    public void Secret_property_names_are_masked_in_the_ring(string name)
    {
        LogBuffer buffer = NewBuffer();
        using Logger logger = new LoggerConfiguration().WriteTo.Sink(buffer).CreateLogger();

        logger.Information("value {" + name + "}", Secret);

        Assert.DoesNotContain(Secret, Assert.Single(buffer.List(null)).Message, StringComparison.Ordinal);
        Assert.True(LogRedactor.IsSecretName(name));
    }

    [Theory]
    [InlineData("message")]
    [InlineData("Mapping")]
    [InlineData("SourceContext")]
    public void Ordinary_property_names_are_kept(string name) => Assert.False(LogRedactor.IsSecretName(name));

    [Fact]
    public void Structures_sequences_and_dictionaries_are_walked()
    {
        LogBuffer buffer = NewBuffer();
        using Logger logger = new LoggerConfiguration().WriteTo.Sink(buffer).CreateLogger();

        logger.Information("shape {@Body}", new { Name = "kept-name", Inner = new { KidPin = Secret, Note = "token=" + Secret } });
        logger.Information("shape {@Items}", new[] { "ok", "apiKey=" + Secret });
        logger.Information("shape {@Map}", new Dictionary<string, string> { ["plexToken"] = Secret, ["label"] = "x" });
        logger.Information("shape {@Nested}", new Dictionary<string, object> { ["rows"] = new[] { new { AuthToken = Secret } } });
        logger.Information("shape {Tokens}", new[] { Secret });

        IReadOnlyList<LogEntry> entries = buffer.List(null);
        Assert.Equal(5, entries.Count);
        Assert.All(entries, entry => Assert.DoesNotContain(Secret, entry.Message, StringComparison.Ordinal));
        Assert.Contains("kept-name", entries[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Uris_and_exception_messages_are_redacted_before_they_enter_the_ring()
    {
        LogBuffer buffer = NewBuffer();
        using Logger logger = new LoggerConfiguration().WriteTo.Sink(buffer).CreateLogger();

        logger.Information("called {Callback}", new Uri("https://plex.tv/api/v2/user?X-Plex-Token=" + Secret + "&x=1"));
        logger.Error(new InvalidOperationException("failed with authToken=" + Secret), "save failed");

        Assert.All(buffer.List(null), entry => Assert.DoesNotContain(Secret, entry.Message, StringComparison.Ordinal));
        Assert.Contains("x=1", buffer.List(null)[0].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"authToken\":\"" + Secret + "\"}")]
    [InlineData("{\"plexAuthToken\": \"" + Secret + "\"}")]
    [InlineData("{\"kidPin\":\"" + Secret + "\"}")]
    [InlineData("{\"pin\":" + "123456" + "}")]
    [InlineData("<user authToken=\"" + Secret + "\" id=\"1\">")]
    [InlineData("Cookie: wishlistarr.auth=" + Secret + "; theme=dark")]
    [InlineData("Set-Cookie: wishlistarr.kid=" + Secret + "; path=/; httponly")]
    [InlineData("cookie: other=" + Secret)]
    [InlineData("sent wishlistarr.auth=" + Secret + " done")]
    [InlineData("sent wishlistarr.kid=" + Secret)]
    [InlineData("Proxy-Authorization: Basic " + Secret)]
    [InlineData("X-API-KEY: " + Secret)]
    [InlineData("{\"dtdd.api_key\":\"" + Secret + "\"}")]
    public void Redact_covers_the_text_shapes(string text)
    {
        string redacted = LogRedactor.Redact(text);

        Assert.DoesNotContain(Secret, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("123456", redacted, StringComparison.Ordinal);
        Assert.Contains("***", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CancellationToken")]
    [InlineData("CacheKey")]
    [InlineData("Keyword")]
    [InlineData("Keywords")]
    [InlineData("Author")]
    [InlineData("Monkey")]
    public void Common_harmless_names_are_kept(string name) => Assert.False(LogRedactor.IsSecretName(name));

    [Theory]
    [InlineData("ApiKey")]
    [InlineData("api_key")]
    [InlineData("X-Api-Key")]
    [InlineData("PlexToken")]
    [InlineData("AuthToken")]
    [InlineData("KidPin")]
    [InlineData("pin")]
    [InlineData("Cookie")]
    [InlineData("Authorization")]
    [InlineData("auth")]
    [InlineData("SigningKey")]
    public void Real_secret_names_stay_masked(string name) => Assert.True(LogRedactor.IsSecretName(name));

    [Theory]
    [InlineData("{\"token\":\"abc\\u002Bdef" + Secret + "\"}")]
    [InlineData("{\"password\":\"a\\\\b" + Secret + "\"}")]
    [InlineData("{\"authToken\":\"a\\\"b" + Secret + "c\",\"x\":1}")]
    [InlineData("{\"token\":\"" + Secret)]
    [InlineData("{\"token\":\"abc\\u002B" + Secret)]
    [InlineData("{\"token\":\"" + Secret + "\\")]
    [InlineData("{\"kid\": {\"pin\": \"" + Secret + "\"}}")]
    public void Json_values_with_escapes_or_no_closing_quote_are_redacted(string text)
    {
        string redacted = LogRedactor.Redact(text);

        Assert.DoesNotContain(Secret, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("u002B", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_written_by_the_serializer_is_redacted()
    {
        string json = JsonSerializer.Serialize(new { token = "ab+/=" + Secret, note = "keep" });

        string redacted = LogRedactor.Redact(json);

        Assert.DoesNotContain(Secret, redacted, StringComparison.Ordinal);
        Assert.Contains("keep", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("token: " + Secret)]
    [InlineData("PIN: " + Secret)]
    [InlineData("failed (apiKey: " + Secret + ")")]
    [InlineData("<authToken>" + Secret + "</authToken>")]
    [InlineData("<Plex><kidPin id=\"1\">" + Secret + "</kidPin></Plex>")]
    [InlineData("<authToken>" + Secret)]
    public void Colon_and_xml_element_forms_are_redacted(string text)
    {
        string redacted = LogRedactor.Redact(text);

        Assert.DoesNotContain(Secret, redacted, StringComparison.Ordinal);
        Assert.Contains("***", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Colon_and_xml_forms_leave_ordinary_names_alone() =>
        Assert.Equal("status: ok <title>Keyword monkey</title> cancellationToken: none", LogRedactor.Redact("status: ok <title>Keyword monkey</title> cancellationToken: none"));

    [Fact]
    public void A_large_adversarial_input_is_processed_quickly_without_leaking()
    {
        string[] inputs =
        [
            "?" + string.Concat(Enumerable.Repeat("key", 21_845)),
            string.Concat(Enumerable.Repeat("&token", 10_000)),
            "<" + string.Concat(Enumerable.Repeat("a<", 32_000)),
            "{\"token\":\"" + new string('\\', 65_000),
            new string(' ', 65_000) + "x",
            "Cookie" + new string(' ', 65_000),
        ];

        foreach (string input in inputs)
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            string redacted = LogRedactor.Redact(input);
            watch.Stop();

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
            Assert.DoesNotContain("zz", redacted, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_regex_timeout_redacts_the_whole_value() =>
        Assert.Equal(LogRedactor.Mask, LogRedactor.FailClosed(() => throw new RegexMatchTimeoutException()));

    [Theory]
    [InlineData(4070)]
    [InlineData(4096)]
    [InlineData(65_500)]
    [InlineData(65_536 - 12)]
    public void A_secret_that_straddles_a_clipping_boundary_is_still_masked(int offset)
    {
        LogBuffer buffer = NewBuffer();
        using Logger logger = new LoggerConfiguration().WriteTo.Sink(buffer).CreateLogger();
        string line = new string('a', offset) + "{\"token\":\"" + Secret + "\"}" + new string('b', 10);

        logger.Information("{Text}", line);

        string message = Assert.Single(buffer.List(null)).Message;
        Assert.DoesNotContain("zz", message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-qq", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_leaves_ordinary_text_alone() =>
        Assert.Equal("the token word and id=1", LogRedactor.Redact("the token word and id=1"));

    [Fact]
    public void The_ring_does_not_change_the_event_later_sinks_see()
    {
        LogBuffer buffer = NewBuffer();
        CaptureSink later = new();
        using Logger logger = new LoggerConfiguration().WriteTo.Sink(buffer).WriteTo.Sink(later).CreateLogger();

        logger.Information("value {PlexToken}", Secret);

        Assert.DoesNotContain(Secret, buffer.List(null)[0].Message, StringComparison.Ordinal);
        Assert.Equal(Secret, ((ScalarValue)later.Events.Single().Properties["PlexToken"]).Value);
    }

    [Fact]
    public void The_enricher_masks_properties_before_any_sink_sees_them()
    {
        CaptureSink sink = new();
        using Logger logger = new LoggerConfiguration().Enrich.With<LogEventRedactor>().WriteTo.Sink(sink).CreateLogger();

        logger.Information("a {@Body} {Callback} {Cookie}", new { Inner = new { KidPin = Secret } }, new Uri("https://x/?token=" + Secret), Secret);

        Assert.DoesNotContain(Secret, sink.Events.Single().RenderMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void Long_messages_are_cut_with_a_marker()
    {
        LogBuffer buffer = NewBuffer();
        using Logger logger = new LoggerConfiguration().WriteTo.Sink(buffer).CreateLogger();

        logger.Information("{Text}", new string('a', 200_000));

        string message = Assert.Single(buffer.List(null)).Message;
        Assert.EndsWith(" [truncated]", message, StringComparison.Ordinal);
        Assert.True(message.Length <= LogOptions.MaxMessageLength + " [truncated]".Length);
    }

    [Fact]
    public async Task The_download_streams_plain_utf8_lines_without_a_byte_order_mark()
    {
        LogBuffer buffer = NewBuffer();
        using Logger logger = new LoggerConfiguration().WriteTo.Sink(buffer).CreateLogger();
        logger.Information("first line");
        logger.Warning("second {Token}", Secret);
        using MemoryStream stream = new();

        await buffer.WriteDownloadAsync(stream, null);

        byte[] bytes = stream.ToArray();
        string text = Encoding.UTF8.GetString(bytes);
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("information Wishlistarr first line", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', text);
    }

    [Fact]
    public void The_ring_stays_consistent_under_concurrent_writes()
    {
        LogBuffer buffer = new(Options.Create(new LogOptions { Capacity = 50 }));
        using Logger logger = new LoggerConfiguration().WriteTo.Sink(buffer).CreateLogger();
        List<int> sizes = [];

        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, worker =>
        {
            for (int index = 0; index < 500; index++)
            {
                logger.Information("line-{Worker}-{Index}", worker, index);
                if (index % 50 == 0)
                {
                    int size = buffer.List(null).Count;
                    lock (sizes)
                    {
                        sizes.Add(size);
                    }
                }
            }
        });

        IReadOnlyList<LogEntry> entries = buffer.List(null);
        Assert.Equal(50, entries.Count);
        Assert.All(entries, entry => Assert.StartsWith("line-", entry.Message, StringComparison.Ordinal));
        Assert.All(sizes, size => Assert.InRange(size, 1, 50));
        Assert.Equal(50, entries.Select(entry => entry.Message).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Every_shape_is_hidden_in_the_listing_and_in_the_download()
    {
        using ObservabilityApiFactory factory = new();
        HttpClient client = await factory.CreateAuthorizedClientAsync();
        Microsoft.Extensions.Logging.ILogger logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Wishlistarr.Tests.Shapes");

        logger.LogInformation("structure {@Body}", new { Inner = new { KidPin = Secret } });
        logger.LogInformation("sequence {@Items}", new[] { "apiKey=" + Secret });
        logger.LogInformation("dictionary {@Map}", new Dictionary<string, string> { ["authToken"] = Secret });
        logger.LogInformation("name {AuthToken} {Cookie} {ApiKeyHeader} {KidPin} {PlexToken}", Secret, Secret, Secret, Secret, Secret);
        logger.LogInformation("uri {Callback}", new Uri("https://plex.tv/?X-Plex-Token=" + Secret));
        logger.LogError(new InvalidOperationException("Cookie: wishlistarr.auth=" + Secret), "cookie failed");
        logger.LogInformation("headers Set-Cookie: wishlistarr.kid=" + Secret + "; path=/");

        string listed = await client.GetStringAsync("/api/v1/system/logs");
        string downloaded = await client.GetStringAsync("/api/v1/system/logs/download");

        using JsonDocument document = JsonDocument.Parse(listed);
        Assert.Contains(document.RootElement.EnumerateArray(), entry => entry.GetProperty("message").GetString()!.Contains("cookie failed", StringComparison.Ordinal));
        Assert.DoesNotContain(Secret, listed, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, downloaded, StringComparison.Ordinal);
    }

    private static LogBuffer NewBuffer() => new(Options.Create(new LogOptions { Capacity = 100 }));

    private sealed class CaptureSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => this.Events.Add(logEvent);
    }
}
