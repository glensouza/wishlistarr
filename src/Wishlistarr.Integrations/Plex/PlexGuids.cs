using System.Diagnostics.CodeAnalysis;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Integrations.Plex;

internal static class PlexGuids
{
    private static readonly Dictionary<string, ExternalIdSource> Schemes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tmdb"] = ExternalIdSource.Tmdb,
        ["tvdb"] = ExternalIdSource.Tvdb,
        ["imdb"] = ExternalIdSource.Imdb,
        ["com.plexapp.agents.themoviedb"] = ExternalIdSource.Tmdb,
        ["com.plexapp.agents.thetvdb"] = ExternalIdSource.Tvdb,
        ["com.plexapp.agents.imdb"] = ExternalIdSource.Imdb,
    };

    public static bool TryParse(string? guid, [NotNullWhen(true)] out ExternalId? id)
    {
        id = null;
        if (string.IsNullOrWhiteSpace(guid))
        {
            return false;
        }

        int separator = guid.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0 || !Schemes.TryGetValue(guid[..separator], out ExternalIdSource source))
        {
            return false;
        }

        string rest = guid[(separator + 3)..];
        int end = rest.IndexOfAny(['?', '/', '#']);
        return ExternalId.TryCreate(source, end < 0 ? rest : rest[..end], out id);
    }

    public static IReadOnlyList<ExternalId> Collect(PlexMetadata item)
    {
        List<ExternalId> ids = [];
        foreach (string? candidate in (item.Guids ?? []).Select(entry => entry.Id).Append(item.Guid))
        {
            if (TryParse(candidate, out ExternalId? id) && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}
