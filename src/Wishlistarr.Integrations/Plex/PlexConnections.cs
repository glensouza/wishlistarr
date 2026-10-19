using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Integrations.Plex;

internal static class PlexConnections
{
    public static bool IsAllowed(Uri? uri) =>
        uri is { IsAbsoluteUri: true }
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && uri.UserInfo.Length == 0
        && uri.Host.Length > 0
        && uri.AbsolutePath == "/"
        && uri.Query.Length == 0
        && uri.Fragment.Length == 0;

    public static IReadOnlyList<MediaServerConnection> Describe(PlexResource server) =>
        [.. Candidates(server).Select(candidate => candidate.Connection)];

    public static IReadOnlyList<MediaServerConnection> Order(PlexResource server) =>
        [.. Candidates(server)
            .Where(candidate => candidate.Connection.Secure || !server.HttpsRequired)
            .OrderBy(candidate => Rank(candidate.Connection))
            .ThenBy(candidate => candidate.IPv6 ? 1 : 0)
            .ThenBy(candidate => candidate.Index)
            .Select(candidate => candidate.Connection)];

    // Plain HTTP on the LAN is preferred over the relay because the relay is bandwidth limited, but the token never crosses the internet unencrypted unless nothing else answers.
    private static int Rank(MediaServerConnection connection) => connection switch
    {
        { Relay: false, Local: true, Secure: true } => 0,
        { Relay: false, Local: false, Secure: true } => 1,
        { Relay: false, Local: true, Secure: false } => 2,
        { Relay: true, Secure: true } => 3,
        { Relay: false, Local: false, Secure: false } => 4,
        _ => 5,
    };

    private static IEnumerable<Candidate> Candidates(PlexResource server)
    {
        int index = 0;
        foreach (PlexResourceConnection connection in server.Connections ?? [])
        {
            if (Uri.TryCreate(connection.Uri, UriKind.Absolute, out Uri? uri) && IsAllowed(uri))
            {
                yield return new Candidate(
                    new MediaServerConnection(uri, connection.Local, connection.Relay, uri.Scheme == Uri.UriSchemeHttps),
                    connection.IPv6,
                    index);
            }

            index++;
        }
    }

    private sealed record Candidate(MediaServerConnection Connection, bool IPv6, int Index);
}
