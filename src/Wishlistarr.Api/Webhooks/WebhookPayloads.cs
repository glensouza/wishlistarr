using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Webhooks;

public sealed record ParsedWebhook(string EventType, string Payload);

public static class WebhookPayloads
{
    public const string PlexPayloadPart = "payload";
    public const string UnknownEventType = "unknown";

    public static async Task<ParsedWebhook?> ParseAsync(
        WebhookSource source,
        string? contentType,
        byte[] body,
        CancellationToken ct = default)
    {
        string? payload = IsMultipart(contentType, out string? boundary)
            ? await ReadMultipartPayloadAsync(boundary, body, ct)
            : Encoding.UTF8.GetString(body);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new ParsedWebhook(FindEventType(document.RootElement, source.EventTypeProperty()), payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsMultipart(string? contentType, out string? boundary)
    {
        boundary = null;
        if (!MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? media)
            || !"multipart/form-data".Equals(media.MediaType.Value, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        boundary = HeaderUtilities.RemoveQuotes(media.Boundary).ToString();
        return true;
    }

    private static async Task<string?> ReadMultipartPayloadAsync(string? boundary, byte[] body, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 200)
        {
            return null;
        }

        try
        {
            MultipartReader reader = new(boundary, new MemoryStream(body));
            while (await reader.ReadNextSectionAsync(ct) is { } section)
            {
                if (ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out ContentDispositionHeaderValue? disposition)
                    && string.Equals(HeaderUtilities.RemoveQuotes(disposition.Name).ToString(), PlexPayloadPart, StringComparison.Ordinal))
                {
                    using StreamReader text = new(section.Body, Encoding.UTF8);
                    return await text.ReadToEndAsync(ct);
                }
            }
        }
        catch (InvalidDataException)
        {
            return null;
        }

        return null;
    }

    private static string FindEventType(JsonElement root, string property)
    {
        foreach (JsonProperty item in root.EnumerateObject())
        {
            if (item.Value.ValueKind == JsonValueKind.String
                && item.Name.Equals(property, StringComparison.OrdinalIgnoreCase)
                && item.Value.GetString() is { Length: > 0 } value)
            {
                return value.Length > WebhookEvent.MaxEventTypeLength ? value[..WebhookEvent.MaxEventTypeLength] : value;
            }
        }

        return UnknownEventType;
    }
}
