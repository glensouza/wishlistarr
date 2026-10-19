using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Wishlistarr.Core.MediaServers;

public static class MediaServerSettings
{
    public const string KeyPrefix = "plex.";
}

public enum MediaKind
{
    Movie,
    Show,
}

public enum ExternalIdSource
{
    Tmdb,
    Tvdb,
    Imdb,
}

public sealed record ExternalId(ExternalIdSource Source, string Value)
{
    public static bool TryCreate(ExternalIdSource source, string? value, [NotNullWhen(true)] out ExternalId? id)
    {
        id = null;
        string trimmed = value?.Trim() ?? string.Empty;
        if (source == ExternalIdSource.Imdb)
        {
            string lower = trimmed.ToLowerInvariant();
            if (lower.Length > 2 && lower.Length <= 20 && lower.StartsWith("tt", StringComparison.Ordinal) && lower[2..].All(char.IsAsciiDigit))
            {
                id = new ExternalId(source, lower);
            }
        }
        else if (trimmed.Length is > 0 and <= 18
            && trimmed.All(char.IsAsciiDigit)
            && long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out long number)
            && number > 0)
        {
            id = new ExternalId(source, number.ToString(CultureInfo.InvariantCulture));
        }

        return id is not null;
    }
}

public enum MediaLibraryType
{
    Movie,
    Show,
    Music,
    Photo,
    Other,
}

public sealed record MediaLibrary(string Id, string Title, MediaLibraryType Type, string? Agent);

public sealed record MediaServerUser(string Id, string Name, string? Username, bool IsHomeUser, bool IsManaged, bool IsAdmin);

public sealed record MediaItem(
    string Id,
    string Guid,
    string Title,
    int? Year,
    MediaKind Kind,
    string LibraryId,
    IReadOnlyList<ExternalId> ExternalIds);

public sealed record MediaItemLabels(
    string ServerId,
    string Id,
    string Guid,
    string Title,
    MediaKind Kind,
    string LibraryId,
    IReadOnlyList<string> Labels,
    bool Locked);

public sealed record MediaServerTestResult(bool Valid, string Message, string? ServerName = null, string? Version = null);

public sealed record MediaServerLinkStart(string Id, string Code, Uri AuthUrl, DateTimeOffset ExpiresAt);

public enum MediaServerLinkState
{
    Pending,
    Linked,
    Expired,
}

public sealed record MediaServerLinkCheck(MediaServerLinkState State, string? AccountName = null);

public sealed record MediaServerConnection(Uri Uri, bool Local, bool Relay, bool Secure);

public sealed record DiscoveredMediaServer(
    string Id,
    string Name,
    bool Owned,
    string? Version,
    IReadOnlyList<MediaServerConnection> Connections);

public sealed record SelectedMediaServer(string Id, string Name, MediaServerConnection Connection);

public sealed record MediaServerAccountStatus(bool Linked, string ClientIdentifier, string? AccountName, SelectedMediaServer? Server);
