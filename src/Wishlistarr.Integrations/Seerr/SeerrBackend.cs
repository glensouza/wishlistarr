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
    public const string SettingsWarning = "Seerr's public settings could not be read, so Seerr's status numbers are assumed and specials are not refused.";

    private const int UnknownStatus = (int)SeerrAvailability.Unknown;
    private const int OverseerrDeletedStatus = 6;

    private static readonly Version EmptySeasonsDroppedFrom = new(3, 5, 0);
    private static readonly Version SpecialsInAllFrom = new(2, 2, 0);
    private static readonly Version SpecialsInAllUntil = new(2, 6, 0);

    private volatile CachedFlavour? cachedFlavour;

    public static TimeSpan FlavourLifetime { get; } = TimeSpan.FromMinutes(5);

    public static TimeSpan FallbackLifetime { get; } = TimeSpan.FromMinutes(1);

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

        ServerFlavour flavour = await this.FlavourAsync(connection, ct, status);

        JsonNode? user;
        try
        {
            user = await client.SendAsync(connection, HttpMethod.Get, "api/v1/user/" + Number(connection.UserId), null, ct);
        }
        catch (SeerrException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new SeerrException("Seerr has no user with that id.");
        }

        IReadOnlyList<string> warnings = Warnings(Permissions(user?["permissions"]));
        return new SeerrTestResult(version, flavour.SettingsRead ? warnings : [.. warnings, SettingsWarning], flavour.Describe());
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

        IReadOnlyList<int>? wanted = request.Kind == MediaKind.Show ? seasons ?? AllSeasons(title, flavour) : null;
        if (Existing(title, request.Kind, wanted, connection.UserId, flavour, false) is { } existing)
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
            return await this.ReattachAsync(connection, request.Kind, mediaType, request.TmdbId, wanted, flavour, false, ct)
                ?? throw new SeerrException("Seerr says a request for that title already exists but did not list it.");
        }

        if (created.Status == HttpStatusCode.Accepted)
        {
            return await this.ReattachAsync(connection, request.Kind, mediaType, request.TmdbId, wanted, flavour, true, ct)
                ?? throw new SeerrException("Seerr already has every requested season, but no request or library season for them was found.");
        }

        if (Int(created.Body?["id"]) is not int id || id <= 0)
        {
            throw new SeerrException("Seerr did not return a request id.");
        }

        JsonNode? createdMedia = created.Body?["media"] ?? title?["mediaInfo"];
        SeerrAvailability? createdSeasons = null;
        if (request.Kind == MediaKind.Show && ParseRequest(created.Body) is { } made)
        {
            ExistingRequest[] holders = [made, .. Requests(title?["mediaInfo"])];
            createdSeasons = SeasonsAvailability(wanted ?? made.Seasons, createdMedia, holders, flavour);
        }

        return new SeerrSubmission(
            SeerrSubmitOutcome.Created,
            id,
            RequestStatus(created.Body?["status"]),
            Availability(created.Body?["media"]?["status"], flavour),
            createdSeasons,
            false);
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

        SeerrAvailability? seasons = Text(body?["type"]) == "tv" && ParseRequest(body) is { } tracked
            ? SeasonsAvailability(tracked.Seasons, body?["media"], [tracked], flavour)
            : null;
        return new SeerrTrackedRequest(id, RequestStatus(body?["status"]), Availability(body?["media"]?["status"], flavour), seasons);
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

    private static SeerrSubmission? Existing(
        JsonNode? title,
        MediaKind kind,
        IReadOnlyList<int>? wanted,
        int userId,
        ServerFlavour flavour,
        bool seerrSaysCovered)
    {
        if (title?["mediaInfo"] is not JsonObject media)
        {
            return null;
        }

        SeerrAvailability availability = Availability(media["status"], flavour);
        ExistingRequest[] requests = Requests(media);
        ExistingRequest[] active = [.. requests.Where(item => item.Status != SeerrRequestStatus.Completed)];
        ExistingRequest[] completed = [.. requests.Where(item => item.Status == SeerrRequestStatus.Completed)];

        if (kind == MediaKind.Movie)
        {
            ExistingRequest? movie = Preferred(active, userId)
                ?? (availability == SeerrAvailability.Available ? Preferred(completed, userId) : null);
            return movie is not null ? new SeerrSubmission(SeerrSubmitOutcome.Attached, movie.Id, movie.Status, availability, null, true)
                : availability == SeerrAvailability.Available ? new SeerrSubmission(SeerrSubmitOutcome.AlreadyAvailable, null, null, availability, null, false)
                : null;
        }

        if (wanted is null && !seerrSaysCovered)
        {
            return availability == SeerrAvailability.Available
                ? new SeerrSubmission(SeerrSubmitOutcome.AlreadyAvailable, null, null, availability, SeerrAvailability.Available, false)
                : null;
        }

        HashSet<int> covered = [.. active.SelectMany(item => item.Seasons)];
        if (media["seasons"] is JsonArray mediaSeasons)
        {
            covered.UnionWith(mediaSeasons
                .Where(season => Int(season?["status"]) is int status && status != UnknownStatus && status != flavour.DeletedStatus)
                .Select(season => Int(season?["seasonNumber"]))
                .OfType<int>());
        }

        if (!seerrSaysCovered && !wanted!.All(covered.Contains))
        {
            return null;
        }

        ExistingRequest? show = Preferred(active.Where(item => wanted is null || wanted.Any(item.SeasonStatus.ContainsKey)), userId)
            ?? Preferred(completed.Where(item => wanted is null || wanted.Any(item.SeasonStatus.ContainsKey)), userId);
        IEnumerable<int> asked = wanted ?? show?.Seasons ?? [.. LibrarySeasons(media, flavour).Keys.Where(season => season > 0)];
        SeerrAvailability seasons = SeasonsAvailability(asked, media, requests, flavour);
        if (show is not null)
        {
            return new SeerrSubmission(SeerrSubmitOutcome.Attached, show.Id, show.Status, availability, seasons, true);
        }

        bool available = seasons == SeerrAvailability.Available || (wanted is null && availability == SeerrAvailability.Available);
        return new SeerrSubmission(
            available ? SeerrSubmitOutcome.AlreadyAvailable : SeerrSubmitOutcome.AlreadyCovered,
            null,
            null,
            availability,
            seasons,
            false);
    }

    private static ExistingRequest[] Requests(JsonNode? media) =>
        media?["requests"] is JsonArray list
            ? [.. list.Select(ParseRequest).OfType<ExistingRequest>().Where(item => !item.Is4k && item.Status != SeerrRequestStatus.Declined)]
            : [];

    private static Dictionary<int, SeerrAvailability> LibrarySeasons(JsonNode? media, ServerFlavour flavour)
    {
        Dictionary<int, SeerrAvailability> library = [];
        if (media?["seasons"] is JsonArray seasons)
        {
            foreach (JsonNode? season in seasons)
            {
                if (Int(season?["seasonNumber"]) is int number)
                {
                    library[number] = Availability(season?["status"], flavour);
                }
            }
        }

        return library;
    }

    private static SeerrAvailability SeasonsAvailability(
        IEnumerable<int> seasons,
        JsonNode? media,
        IReadOnlyCollection<ExistingRequest> requests,
        ServerFlavour flavour)
    {
        Dictionary<int, SeerrAvailability> library = LibrarySeasons(media, flavour);
        SeerrAvailability[] statuses = [.. seasons.Distinct().Select(season =>
            library.TryGetValue(season, out SeerrAvailability known) && known != SeerrAvailability.Unknown
                ? known
                : requests
                    .Where(item => item.SeasonStatus.ContainsKey(season))
                    .Select(item => item.SeasonStatus[season] switch
                    {
                        SeerrRequestStatus.Completed => SeerrAvailability.Available,
                        SeerrRequestStatus.Approved => SeerrAvailability.Processing,
                        SeerrRequestStatus.Pending => SeerrAvailability.Pending,
                        _ => SeerrAvailability.Unknown,
                    })
                    .DefaultIfEmpty(SeerrAvailability.Unknown)
                    .Max())];

        if (statuses.Length > 0 && statuses.All(status => status == SeerrAvailability.Available))
        {
            return SeerrAvailability.Available;
        }

        return statuses.Any(status => status is SeerrAvailability.Available or SeerrAvailability.PartiallyAvailable) ? SeerrAvailability.PartiallyAvailable
            : statuses.Contains(SeerrAvailability.Processing) ? SeerrAvailability.Processing
            : statuses.Contains(SeerrAvailability.Pending) ? SeerrAvailability.Pending
            : statuses.Contains(SeerrAvailability.Blocklisted) ? SeerrAvailability.Blocklisted
            : statuses.Contains(SeerrAvailability.Deleted) ? SeerrAvailability.Deleted
            : SeerrAvailability.Unknown;
    }

    private static ExistingRequest? Preferred(IEnumerable<ExistingRequest> requests, int userId) =>
        requests.OrderBy(item => item.RequestedBy == userId ? 0 : 1).ThenBy(item => item.Id).FirstOrDefault();

    private static ExistingRequest? ParseRequest(JsonNode? node)
    {
        if (Int(node?["id"]) is not int id || id <= 0)
        {
            return null;
        }

        Dictionary<int, SeerrRequestStatus> seasons = [];
        if (node?["seasons"] is JsonArray list)
        {
            foreach (JsonNode? season in list)
            {
                if (Int(season?["seasonNumber"]) is int number)
                {
                    seasons[number] = Int(season?["status"]) is int code && Enum.IsDefined((SeerrRequestStatus)code)
                        ? (SeerrRequestStatus)code
                        : SeerrRequestStatus.Unknown;
                }
            }
        }

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

    private static int[]? AllSeasons(JsonNode? title, ServerFlavour flavour)
    {
        if (title?["seasons"] is not JsonArray list)
        {
            return null;
        }

        int[] seasons = [.. list
            .Where(season => Int(season?["seasonNumber"]) switch
            {
                0 => flavour.AllIncludesSpecials,
                > 0 => !flavour.DropsEmptySeasons || Int(season?["episodeCount"]) > 0,
                _ => false,
            })
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
        bool seerrSaysCovered,
        CancellationToken ct)
    {
        JsonNode? title = await this.TitleAsync(connection, mediaType, tmdbId, ct);
        IReadOnlyList<int>? seasons = kind == MediaKind.Show ? wanted ?? AllSeasons(title, flavour) : null;
        return Existing(title, kind, seasons, connection.UserId, flavour, seerrSaysCovered);
    }

    private async Task<ServerFlavour> FlavourAsync(SeerrConnection connection, CancellationToken ct, JsonNode? status = null)
    {
        long revision = store.Revision;
        DateTimeOffset now = time.GetUtcNow();
        if (status is null && this.cachedFlavour is { } cached && cached.Url == connection.Url && cached.Revision == revision && cached.Expires > now)
        {
            return cached.Flavour;
        }

        JsonNode? settings;
        try
        {
            status ??= await client.SendAsync(connection, HttpMethod.Get, "api/v1/status", null, ct);
            settings = await client.SendAsync(connection, HttpMethod.Get, "api/v1/settings/public", null, ct);
        }
        catch (SeerrException)
        {
            this.cachedFlavour = new CachedFlavour(connection.Url, revision, now + FallbackLifetime, ServerFlavour.Fallback);
            return ServerFlavour.Fallback;
        }

        // Only Jellyseerr and Seerr report mediaServerType. Across that line 6 is blacklisted or blocklisted, and 7 is deleted
        // from Jellyseerr 2.6 on, while Overseerr uses 6 for deleted. Version strings cannot tell them apart on develop builds.
        bool blocklist = settings?["mediaServerType"] is not null;
        bool? specialsSetting = settings?["enableSpecialEpisodes"] is JsonValue flag && flag.TryGetValue(out bool enabled) ? enabled : null;
        Version? version = blocklist ? ReleaseVersion(status) : null;
        bool dropsEmptySeasons = version is not null && version >= EmptySeasonsDroppedFrom;
        bool allIncludesSpecials = specialsSetting == true && version is not null && version >= SpecialsInAllFrom && version < SpecialsInAllUntil;
        ServerFlavour flavour = new(blocklist, specialsSetting != false, dropsEmptySeasons, allIncludesSpecials, true);
        this.cachedFlavour = new CachedFlavour(connection.Url, revision, now + FlavourLifetime, flavour);
        return flavour;
    }

    private static Version? ReleaseVersion(JsonNode? status)
    {
        string? text = Text(status?["version"])?.TrimStart('v');
        int suffix = text?.IndexOf('-', StringComparison.Ordinal) ?? -1;
        string? release = suffix >= 0 ? text![..suffix] : text;
        if (release is not null && !release.Contains('.', StringComparison.Ordinal))
        {
            release += ".0";
        }

        return Version.TryParse(release, out Version? version)
            ? new Version(version.Major, version.Minor, Math.Max(version.Build, 0))
            : null;
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

    private sealed record ServerFlavour(bool Blocklist, bool SpecialsAllowed, bool DropsEmptySeasons, bool AllIncludesSpecials, bool SettingsRead)
    {
        public static ServerFlavour Fallback { get; } = new(true, true, false, false, false);

        public SeerrServerFlavour Describe() => new(
            this.Blocklist ? SeerrServerKind.Seerr : SeerrServerKind.Overseerr,
            this.SettingsRead,
            this.SpecialsAllowed,
            this.AllIncludesSpecials,
            this.DropsEmptySeasons);

        public int DeletedStatus => this.Blocklist ? (int)SeerrAvailability.Deleted : OverseerrDeletedStatus;
    }

    private sealed record CachedFlavour(Uri Url, long Revision, DateTimeOffset Expires, ServerFlavour Flavour);

    private sealed record ExistingRequest(
        int Id,
        SeerrRequestStatus Status,
        bool Is4k,
        IReadOnlyDictionary<int, SeerrRequestStatus> SeasonStatus,
        int? RequestedBy)
    {
        public IReadOnlyCollection<int> Seasons => [.. this.SeasonStatus.Keys];
    }
}
