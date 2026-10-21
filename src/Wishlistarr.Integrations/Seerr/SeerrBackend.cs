using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Wishlistarr.Core.Fulfilment;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Integrations.Seerr;

public sealed class SeerrBackend(SeerrClient client, SeerrConnectionStore store, TimeProvider time) : ISeerrBackend
{
    public const string MovieAutoApproveWarning = "This user lacks auto-approve for movies, so movie requests made for them will wait for approval.";
    public const string ShowAutoApproveWarning = "This user lacks auto-approve for shows, so show requests made for them will wait for approval.";
    public const string MovieRequestWarning = "This user cannot request movies, so Seerr will refuse movie requests made for them.";
    public const string ShowRequestWarning = "This user cannot request shows, so Seerr will refuse show requests made for them.";

    private const int UnknownStatus = (int)SeerrAvailability.Unknown;
    private const int OverseerrDeletedStatus = 6;

    private volatile CachedFlavour? cachedFlavour;

    public static TimeSpan FlavourLifetime { get; } = TimeSpan.FromMinutes(5);

    public async Task<SeerrTestResult> TestAsync(CancellationToken ct = default)
    {
        SeerrConnection connection = await this.RequireAsync(ct);
        this.cachedFlavour = null;
        JsonNode? status = await client.SendAsync(connection, HttpMethod.Get, "api/v1/status", null, ct);
        string? version = Text(status?["version"]) ?? Text(status?["commitTag"]);
        if (version is null)
        {
            throw new SeerrException("Seerr did not report a version.");
        }

        JsonNode? user;
        try
        {
            user = await client.SendAsync(connection, HttpMethod.Get, "api/v1/user/" + Number(connection.UserId), null, ct);
        }
        catch (SeerrException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new SeerrException("Seerr has no user with that id.");
        }

        return new SeerrTestResult(version, Warnings(Permissions(user?["permissions"])));
    }

    public static IReadOnlyList<string> Warnings(long permissions)
    {
        List<string> warnings = [];
        foreach ((MediaKind kind, string cannotRequest, string waits) in new[]
        {
            (MediaKind.Movie, MovieRequestWarning, MovieAutoApproveWarning),
            (MediaKind.Show, ShowRequestWarning, ShowAutoApproveWarning),
        })
        {
            if (!SeerrPermissions.CanRequest(permissions, kind))
            {
                warnings.Add(cannotRequest);
            }
            else if (!SeerrPermissions.AutoApproves(permissions, kind))
            {
                warnings.Add(waits);
            }
        }

        return warnings;
    }

    public async Task<SeerrSubmission> SubmitAsync(SeerrSubmitRequest request, CancellationToken ct = default)
    {
        Validate(request.Kind, request.TmdbId, request.Seasons ?? []);
        int[]? seasons = request.Kind == MediaKind.Show && request.Seasons is { Count: > 0 } given ? [.. given.Distinct().Order()] : null;
        SeerrConnection connection = await this.RequireAsync(ct);
        string mediaType = request.Kind == MediaKind.Movie ? "movie" : "tv";
        ServerFlavour flavour = await this.FlavourAsync(connection, ct);
        JsonNode? title = await this.TitleAsync(connection, mediaType, request.TmdbId, ct);
        if (seasons is not null)
        {
            CheckSeasons(seasons, title, flavour);
        }

        IReadOnlyList<int>? wanted = request.Kind == MediaKind.Show ? seasons ?? AllSeasons(title) : null;
        if (Existing(title, request.Kind, wanted, connection.UserId, flavour) is { } existing)
        {
            return existing;
        }

        JsonObject body = new()
        {
            ["mediaType"] = mediaType,
            ["mediaId"] = request.TmdbId,
        };
        if (request.Kind == MediaKind.Show)
        {
            body["seasons"] = seasons is null ? "all" : new JsonArray([.. seasons.Select(season => (JsonNode)JsonValue.Create(season))]);
        }

        SeerrResponse created;
        try
        {
            created = await client.SendForResponseAsync(connection, HttpMethod.Post, "api/v1/request", body, ct, actAs: connection.UserId);
        }
        catch (SeerrException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            return await this.ReattachAsync(connection, request.Kind, mediaType, request.TmdbId, wanted, flavour, ct)
                ?? throw new SeerrException("Seerr says a request for that title already exists but did not list it.");
        }

        if (created.Status == HttpStatusCode.Accepted)
        {
            return await this.ReattachAsync(connection, request.Kind, mediaType, request.TmdbId, wanted, flavour, ct)
                ?? throw new SeerrException("Seerr already has every requested season, but no request or library season for them was found.");
        }

        if (Int(created.Body?["id"]) is not int id || id <= 0)
        {
            throw new SeerrException("Seerr did not return a request id.");
        }

        return new SeerrSubmission(SeerrSubmitOutcome.Created, id, RequestStatus(created.Body?["status"]), Availability(created.Body?["media"]?["status"], flavour), false);
    }

    public async Task<SeerrTrackedRequest> GetRequestAsync(int requestId, CancellationToken ct = default)
    {
        if (requestId <= 0)
        {
            throw new ArgumentException("The request id must be a positive number.", nameof(requestId));
        }

        SeerrConnection connection = await this.RequireAsync(ct);
        ServerFlavour flavour = await this.FlavourAsync(connection, ct);
        JsonNode? body;
        try
        {
            body = await client.SendAsync(connection, HttpMethod.Get, "api/v1/request/" + Number(requestId), null, ct);
        }
        catch (SeerrException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new SeerrException("Seerr has no request with that id.");
        }

        if (Int(body?["id"]) is not int id || id <= 0)
        {
            throw new SeerrException("Seerr did not return a request id.");
        }

        return new SeerrTrackedRequest(id, RequestStatus(body?["status"]), Availability(body?["media"]?["status"], flavour));
    }

    private async Task<SeerrConnection> RequireAsync(CancellationToken ct) =>
        await store.GetAsync(ct)
        ?? throw new SeerrNotConfiguredException("Seerr is not configured. Save its address, API key and user id first.");

    private static void Validate(MediaKind kind, int tmdbId, IReadOnlyList<int> seasons)
    {
        if (kind is not (MediaKind.Movie or MediaKind.Show))
        {
            throw new ArgumentException("Seerr only handles movies and shows.", nameof(kind));
        }

        if (tmdbId <= 0)
        {
            throw new ArgumentException("The TMDB id must be a positive number.", nameof(tmdbId));
        }

        if (kind == MediaKind.Movie && seasons.Count > 0)
        {
            throw new ArgumentException("Seasons apply to shows only.");
        }

        if (seasons.Any(season => season < 0))
        {
            throw new ArgumentException("Season numbers cannot be negative.");
        }
    }

    private static SeerrSubmission? Existing(JsonNode? title, MediaKind kind, IReadOnlyList<int>? wanted, int userId, ServerFlavour flavour)
    {
        if (title?["mediaInfo"] is not JsonObject media)
        {
            return null;
        }

        SeerrAvailability availability = Availability(media["status"], flavour);
        ExistingRequest[] requests = media["requests"] is JsonArray list
            ? [.. list.Select(ParseRequest).OfType<ExistingRequest>().Where(item => !item.Is4k && item.Status != SeerrRequestStatus.Declined)]
            : [];
        ExistingRequest[] active = [.. requests.Where(item => item.Status != SeerrRequestStatus.Completed)];
        ExistingRequest[] completed = [.. requests.Where(item => item.Status == SeerrRequestStatus.Completed)];
        SeerrSubmission? alreadyAvailable = availability == SeerrAvailability.Available ? AlreadyAvailable(availability) : null;

        if (kind == MediaKind.Movie)
        {
            ExistingRequest? movie = Preferred(active, userId) ?? (alreadyAvailable is not null ? Preferred(completed, userId) : null);
            return movie is not null ? Attach(movie, availability) : alreadyAvailable;
        }

        if (wanted is null)
        {
            return alreadyAvailable;
        }

        HashSet<int> covered = [.. active.SelectMany(item => item.Seasons)];
        if (media["seasons"] is JsonArray mediaSeasons)
        {
            covered.UnionWith(mediaSeasons
                .Where(season => Int(season?["status"]) is int status && status != UnknownStatus && status != flavour.DeletedStatus)
                .Select(season => Int(season?["seasonNumber"]))
                .OfType<int>());
        }

        if (!wanted.All(covered.Contains))
        {
            return null;
        }

        ExistingRequest? show = Preferred(active.Where(item => item.Seasons.Overlaps(wanted)), userId)
            ?? Preferred(completed.Where(item => item.Seasons.Overlaps(wanted)), userId);
        return show is not null ? Attach(show, availability) : AlreadyAvailable(availability);
    }

    private static ExistingRequest? Preferred(IEnumerable<ExistingRequest> requests, int userId) =>
        requests.OrderBy(item => item.RequestedBy == userId ? 0 : 1).ThenBy(item => item.Id).FirstOrDefault();

    private static SeerrSubmission Attach(ExistingRequest request, SeerrAvailability availability) =>
        new(SeerrSubmitOutcome.Attached, request.Id, request.Status, availability, true);

    private static SeerrSubmission AlreadyAvailable(SeerrAvailability availability) =>
        new(SeerrSubmitOutcome.AlreadyAvailable, null, null, availability, false);

    private static ExistingRequest? ParseRequest(JsonNode? node)
    {
        if (Int(node?["id"]) is not int id || id <= 0)
        {
            return null;
        }

        HashSet<int> seasons = node?["seasons"] is JsonArray list
            ? [.. list.Select(season => Int(season?["seasonNumber"])).OfType<int>()]
            : [];
        bool is4k = node?["is4k"] is JsonValue flag && flag.TryGetValue(out bool value) && value;
        return new ExistingRequest(id, RequestStatus(node?["status"]), is4k, seasons, Int(node?["requestedBy"]?["id"]));
    }

    private static void CheckSeasons(int[] seasons, JsonNode? title, ServerFlavour flavour)
    {
        if (!flavour.SpecialsAllowed && seasons.Contains(0))
        {
            throw new ArgumentException("Seerr has special episodes turned off, so season 0 cannot be requested.");
        }

        int[] known = title?["seasons"] is JsonArray list ? [.. list.Select(season => Int(season?["seasonNumber"])).OfType<int>()] : [];
        int[] unknown = [.. seasons.Where(season => !known.Contains(season))];
        if (known.Length > 0 && unknown.Length > 0)
        {
            throw new ArgumentException(unknown.Length == 1
                ? $"That show has no season {Number(unknown[0])}."
                : $"That show has no seasons {string.Join(", ", unknown.Select(Number))}.");
        }
    }

    private static int[]? AllSeasons(JsonNode? title)
    {
        if (title?["seasons"] is not JsonArray list)
        {
            return null;
        }

        int[] seasons = [.. list
            .Where(season => Int(season?["seasonNumber"]) > 0 && Int(season?["episodeCount"]) > 0)
            .Select(season => Int(season?["seasonNumber"])!.Value)
            .Distinct()
            .Order()];
        return seasons.Length > 0 ? seasons : null;
    }

    private async Task<SeerrSubmission?> ReattachAsync(
        SeerrConnection connection,
        MediaKind kind,
        string mediaType,
        int tmdbId,
        IReadOnlyList<int>? wanted,
        ServerFlavour flavour,
        CancellationToken ct)
    {
        JsonNode? title = await this.TitleAsync(connection, mediaType, tmdbId, ct);
        IReadOnlyList<int>? seasons = kind == MediaKind.Show ? wanted ?? AllSeasons(title) : null;
        return Existing(title, kind, seasons, connection.UserId, flavour);
    }

    private async Task<ServerFlavour> FlavourAsync(SeerrConnection connection, CancellationToken ct)
    {
        long revision = store.Revision;
        DateTimeOffset now = time.GetUtcNow();
        if (this.cachedFlavour is { } cached && cached.Url == connection.Url && cached.Revision == revision && cached.Expires > now)
        {
            return cached.Flavour;
        }

        JsonNode? settings;
        try
        {
            settings = await client.SendAsync(connection, HttpMethod.Get, "api/v1/settings/public", null, ct);
        }
        catch (SeerrException)
        {
            return ServerFlavour.Fallback;
        }

        // Only Jellyseerr and Seerr report mediaServerType. Across that line 6 is blacklisted or blocklisted, and 7 is deleted
        // from Jellyseerr 2.6 on, while Overseerr uses 6 for deleted. Version strings cannot tell them apart on develop builds.
        bool blocklist = settings?["mediaServerType"] is not null;
        bool specials = settings?["enableSpecialEpisodes"] is not JsonValue flag || !flag.TryGetValue(out bool enabled) || enabled;
        ServerFlavour flavour = new(blocklist, specials);
        this.cachedFlavour = new CachedFlavour(connection.Url, revision, now + FlavourLifetime, flavour);
        return flavour;
    }

    private async Task<JsonNode?> TitleAsync(SeerrConnection connection, string mediaType, int tmdbId, CancellationToken ct)
    {
        try
        {
            return await client.SendAsync(connection, HttpMethod.Get, $"api/v1/{mediaType}/{Number(tmdbId)}", null, ct);
        }
        catch (SeerrException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new SeerrException("Seerr could not find that title.");
        }
    }

    private static SeerrRequestStatus RequestStatus(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue(out int code))
        {
            return Enum.IsDefined((SeerrRequestStatus)code) ? (SeerrRequestStatus)code : SeerrRequestStatus.Unknown;
        }

        throw new SeerrException("Seerr returned something unexpected.");
    }

    private static SeerrAvailability Availability(JsonNode? node, ServerFlavour flavour)
    {
        if (node is not JsonValue value || !value.TryGetValue(out int code))
        {
            return SeerrAvailability.Unknown;
        }

        if (!flavour.Blocklist)
        {
            return code == OverseerrDeletedStatus ? SeerrAvailability.Deleted
                : code < OverseerrDeletedStatus && Enum.IsDefined((SeerrAvailability)code) ? (SeerrAvailability)code
                : SeerrAvailability.Unknown;
        }

        return Enum.IsDefined((SeerrAvailability)code) ? (SeerrAvailability)code : SeerrAvailability.Unknown;
    }

    private static long Permissions(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return 0;
        }

        if (value.TryGetValue(out long number))
        {
            return number;
        }

        if (value.TryGetValue(out int integer))
        {
            return integer;
        }

        return 0;
    }

    private static int? Int(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out int number) ? number : null;

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed record ServerFlavour(bool Blocklist, bool SpecialsAllowed)
    {
        public static ServerFlavour Fallback { get; } = new(true, true);

        public int DeletedStatus => this.Blocklist ? (int)SeerrAvailability.Deleted : OverseerrDeletedStatus;
    }

    private sealed record CachedFlavour(Uri Url, long Revision, DateTimeOffset Expires, ServerFlavour Flavour);

    private sealed record ExistingRequest(int Id, SeerrRequestStatus Status, bool Is4k, HashSet<int> Seasons, int? RequestedBy);
}
