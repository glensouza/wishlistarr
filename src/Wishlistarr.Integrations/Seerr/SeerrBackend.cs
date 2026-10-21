using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Wishlistarr.Core.Fulfilment;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Integrations.Seerr;

public sealed class SeerrBackend(SeerrClient client, SeerrConnectionStore store) : ISeerrBackend
{
    public const string AutoApproveWarning = "This user lacks auto-approve, so requests made for them will wait for approval.";

    public async Task<SeerrTestResult> TestAsync(CancellationToken ct = default)
    {
        SeerrConnection connection = await this.RequireAsync(ct);
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

        string? warning = SeerrPermissions.HasAutoApprove(Permissions(user?["permissions"])) ? null : AutoApproveWarning;
        return new SeerrTestResult(version, warning);
    }

    public async Task<SeerrSubmission> SubmitAsync(SeerrSubmitRequest request, CancellationToken ct = default)
    {
        IReadOnlyList<int> seasons = request.Seasons ?? [];
        Validate(request.Kind, request.TmdbId, seasons);
        SeerrConnection connection = await this.RequireAsync(ct);
        string mediaType = request.Kind == MediaKind.Movie ? "movie" : "tv";
        JsonNode? title;
        try
        {
            title = await client.SendAsync(connection, HttpMethod.Get, $"api/v1/{mediaType}/{Number(request.TmdbId)}", null, ct);
        }
        catch (SeerrException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new SeerrException("Seerr could not find that title.");
        }

        if (ExistingRequest(title) is { } existing)
        {
            return existing;
        }

        JsonObject body = new()
        {
            ["mediaType"] = mediaType,
            ["mediaId"] = request.TmdbId,
            ["userId"] = connection.UserId,
        };
        if (seasons.Count > 0)
        {
            body["seasons"] = new JsonArray([.. seasons.Select(season => (JsonNode)JsonValue.Create(season))]);
        }

        JsonNode? created = await client.SendAsync(connection, HttpMethod.Post, "api/v1/request", body, ct);
        if (Int(created?["id"]) is not int id || id <= 0)
        {
            throw new SeerrException("Seerr did not return a request id.");
        }

        return new SeerrSubmission(id, Status(created?["status"]), false);
    }

    public async Task<SeerrTrackedRequest> GetRequestAsync(int requestId, CancellationToken ct = default)
    {
        if (requestId <= 0)
        {
            throw new ArgumentException("The request id must be a positive number.", nameof(requestId));
        }

        SeerrConnection connection = await this.RequireAsync(ct);
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

        return new SeerrTrackedRequest(id, Status(body?["status"]));
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

    private static SeerrSubmission? ExistingRequest(JsonNode? title)
    {
        if (title?["mediaInfo"] is not JsonObject media || media["requests"] is not JsonArray requests)
        {
            return null;
        }

        foreach (JsonNode? request in requests)
        {
            if (Int(request?["id"]) is int id && id > 0)
            {
                return new SeerrSubmission(id, Status(request?["status"]), true);
            }
        }

        return null;
    }

    private static SeerrRequestStatus Status(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue(out int code))
        {
            return (SeerrRequestStatus)code;
        }

        throw new SeerrException("Seerr returned something unexpected.");
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
}
