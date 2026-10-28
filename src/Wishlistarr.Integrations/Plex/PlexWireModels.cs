using System.Text.Json.Serialization;

namespace Wishlistarr.Integrations.Plex;

internal sealed record PlexPin(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("expiresIn")] int? ExpiresIn,
    [property: JsonPropertyName("authToken")] string? AuthToken);

internal sealed record PlexAccount(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("title")] string? Title);

internal sealed record PlexResource(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("clientIdentifier")] string? ClientIdentifier,
    [property: JsonPropertyName("provides")] string? Provides,
    [property: JsonPropertyName("productVersion")] string? ProductVersion,
    [property: JsonPropertyName("owned")] bool Owned,
    [property: JsonPropertyName("accessToken")] string? AccessToken,
    [property: JsonPropertyName("httpsRequired")] bool HttpsRequired,
    [property: JsonPropertyName("connections")] IReadOnlyList<PlexResourceConnection>? Connections)
{
    public bool IsServer =>
        !string.IsNullOrWhiteSpace(this.ClientIdentifier)
        && (this.Provides ?? string.Empty).Split(',').Any(part => part.Trim() == "server");
}

internal sealed record PlexResourceConnection(
    [property: JsonPropertyName("uri")] string? Uri,
    [property: JsonPropertyName("local")] bool Local,
    [property: JsonPropertyName("relay")] bool Relay,
    [property: JsonPropertyName("IPv6")] bool IPv6);

internal sealed record PlexFriend(long Id, string? Title, string? Username, bool Restricted, bool Home);

internal sealed record PlexHomeUser(long Id, string? Title, string? Username, bool Restricted, bool Admin);

internal sealed record PlexEnvelope(
    [property: JsonPropertyName("MediaContainer")] PlexContainer? MediaContainer);

internal sealed record PlexContainer(
    [property: JsonPropertyName("machineIdentifier")] string? MachineIdentifier,
    [property: JsonPropertyName("friendlyName")] string? FriendlyName,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("totalSize")] int? TotalSize,
    [property: JsonPropertyName("Directory")] IReadOnlyList<PlexSection>? Directory,
    [property: JsonPropertyName("Metadata")] IReadOnlyList<PlexMetadata>? Metadata);

internal sealed record PlexSection(
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("agent")] string? Agent);

internal sealed record PlexMetadata(
    [property: JsonPropertyName("ratingKey")] string? RatingKey,
    [property: JsonPropertyName("guid")] string? Guid,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("year")] int? Year,
    [property: JsonPropertyName("Guid")] IReadOnlyList<PlexGuidEntry>? Guids)
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("updatedAt")]
    public long? UpdatedAt { get; init; }

    [JsonPropertyName("addedAt")]
    public long? AddedAt { get; init; }

    [JsonPropertyName("librarySectionID")]
    public long? LibrarySectionId { get; init; }

    [JsonPropertyName("parentRatingKey")]
    public string? ParentRatingKey { get; init; }

    [JsonPropertyName("grandparentRatingKey")]
    public string? GrandparentRatingKey { get; init; }

    [JsonPropertyName("Label")]
    public IReadOnlyList<PlexTag>? Labels { get; init; }

    [JsonPropertyName("Field")]
    public IReadOnlyList<PlexField>? Fields { get; init; }

    [JsonPropertyName("subtype")]
    public string? Subtype { get; init; }

    [JsonPropertyName("Media")]
    public IReadOnlyList<PlexVideoMedia>? Media { get; init; }
}

internal sealed record PlexVideoMedia(
    [property: JsonPropertyName("container")] string? Container,
    [property: JsonPropertyName("videoCodec")] string? VideoCodec,
    [property: JsonPropertyName("audioCodec")] string? AudioCodec,
    [property: JsonPropertyName("Part")] IReadOnlyList<PlexVideoPart>? Parts);

internal sealed record PlexVideoPart([property: JsonPropertyName("key")] string? Key);

internal sealed record PlexGuidEntry(
    [property: JsonPropertyName("id")] string? Id);

internal sealed record PlexTag(
    [property: JsonPropertyName("tag")] string? Tag);

internal sealed record PlexField(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("locked")] bool Locked);

internal sealed record PlexServerContext(string ServerId, Uri BaseUri, string Token, string ClientIdentifier);
