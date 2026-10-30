using System.Globalization;
using System.Text.Json.Nodes;
using Wishlistarr.Core.Fulfilment;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Integrations.Arr;

public sealed class ArrBackends(IEnumerable<IArrBackend> backends)
{
    public IArrBackend Get(ArrService service) => backends.First(backend => backend.Service == service);
}

public abstract class ArrBackend(ArrService service, ArrClient client, ArrConnectionStore store) : IArrBackend
{
    public ArrService Service => service;

    protected string Name => service.ToString();

    public async Task<ArrTestResult> TestAsync(CancellationToken ct = default)
    {
        ArrConnection? connection = await store.GetAsync(service, ct);
        if (connection is null)
        {
            return new ArrTestResult(false, $"{this.Name} is not configured. Save its address and API key first.", Code: ArrTestCode.NotConfigured);
        }

        try
        {
            JsonNode? status = await client.SendAsync(service, connection, HttpMethod.Get, "api/v3/system/status", null, ct);
            string? app = ArrJson.String(status?["appName"]);
            if (!string.Equals(app, this.Name, StringComparison.OrdinalIgnoreCase))
            {
                return new ArrTestResult(false, $"That address is running {app ?? "something else"}, not {this.Name}.");
            }

            string? version = ArrJson.String(status?["version"]);
            return new ArrTestResult(true, $"{this.Name} answered and accepted the API key.", version, ArrTestCode.Connected);
        }
        catch (ArrException ex)
        {
            return new ArrTestResult(false, ex.Message);
        }
    }

    public async Task<IReadOnlyList<ArrQualityProfile>> GetQualityProfilesAsync(CancellationToken ct = default)
    {
        JsonArray items = await this.GetArrayAsync("api/v3/qualityprofile", ct);
        return [.. items.Select(item => new ArrQualityProfile(ArrJson.Int(item?["id"]) ?? 0, ArrJson.String(item?["name"]) ?? string.Empty))];
    }

    public async Task<IReadOnlyList<ArrRootFolder>> GetRootFoldersAsync(CancellationToken ct = default)
    {
        JsonArray items = await this.GetArrayAsync("api/v3/rootfolder", ct);
        return [.. items.Select(item => new ArrRootFolder(
            ArrJson.String(item?["path"]) ?? string.Empty,
            item?["freeSpace"] is JsonValue value && value.TryGetValue(out long free) ? free : 0))];
    }

    public async Task<IReadOnlyList<ArrTag>> GetTagsAsync(CancellationToken ct = default)
    {
        JsonArray items = await this.GetArrayAsync("api/v3/tag", ct);
        return [.. items.Select(item => new ArrTag(ArrJson.Int(item?["id"]) ?? 0, ArrJson.String(item?["label"]) ?? string.Empty))];
    }

    public async Task<FulfilmentResult> FulfilAsync(FulfilmentRequest request, CancellationToken ct = default)
    {
        ArrConnection connection = await this.RequireReadyAsync(ct);
        int tagId = await this.EnsureTagAsync(connection, ct);
        IReadOnlyList<int> tagIds = [.. connection.TagIds.Append(tagId).Distinct()];
        return await this.FulfilCoreAsync(connection, tagIds, request, ct);
    }

    protected abstract Task<FulfilmentResult> FulfilCoreAsync(
        ArrConnection connection,
        IReadOnlyList<int> tagIds,
        FulfilmentRequest request,
        CancellationToken ct);

    protected Task<JsonNode?> SendAsync(ArrConnection connection, HttpMethod method, string path, JsonNode? body, CancellationToken ct) =>
        client.SendAsync(service, connection, method, path, body, ct);

    protected async Task<JsonArray> GetArrayAsync(ArrConnection connection, string path, CancellationToken ct) =>
        await this.SendAsync(connection, HttpMethod.Get, path, null, ct) as JsonArray ?? [];

    protected async Task<JsonObject> GetObjectAsync(ArrConnection connection, string path, CancellationToken ct) =>
        await this.SendAsync(connection, HttpMethod.Get, path, null, ct) as JsonObject
        ?? throw new ArrException($"{this.Name} returned something unexpected.");

    protected static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private async Task<JsonArray> GetArrayAsync(string path, CancellationToken ct)
    {
        ArrConnection connection = await this.RequireConnectionAsync(ct);
        return await this.GetArrayAsync(connection, path, ct);
    }

    private async Task<ArrConnection> RequireConnectionAsync(CancellationToken ct) =>
        await store.GetAsync(service, ct)
        ?? throw new ArrNotConfiguredException($"{this.Name} is not configured. Save its address and API key first.");

    private async Task<ArrConnection> RequireReadyAsync(CancellationToken ct)
    {
        ArrConnection connection = await this.RequireConnectionAsync(ct);
        if (connection.QualityProfileId is null || string.IsNullOrEmpty(connection.RootFolderPath))
        {
            throw new ArrNotConfiguredException($"Choose a quality profile and a root folder for {this.Name} first.");
        }

        return connection;
    }

    private async Task<int> EnsureTagAsync(ArrConnection connection, CancellationToken ct)
    {
        JsonArray tags = await this.GetArrayAsync(connection, "api/v3/tag", ct);
        foreach (JsonNode? tag in tags)
        {
            if (string.Equals(ArrJson.String(tag?["label"]), ArrDefaults.TagLabel, StringComparison.OrdinalIgnoreCase)
                && ArrJson.Int(tag?["id"]) is { } existing)
            {
                return existing;
            }
        }

        JsonNode? created = await this.SendAsync(connection, HttpMethod.Post, "api/v3/tag", new JsonObject { ["label"] = ArrDefaults.TagLabel }, ct);
        return ArrJson.Int(created?["id"]) ?? throw new ArrException($"{this.Name} did not create the {ArrDefaults.TagLabel} tag.");
    }
}

public sealed class SonarrBackend(ArrClient client, ArrConnectionStore store) : ArrBackend(ArrService.Sonarr, client, store)
{
    protected override async Task<FulfilmentResult> FulfilCoreAsync(
        ArrConnection connection,
        IReadOnlyList<int> tagIds,
        FulfilmentRequest request,
        CancellationToken ct)
    {
        if (request.Kind != MediaKind.Show)
        {
            throw new ArgumentException("Sonarr only handles shows.", nameof(request));
        }

        JsonObject lookup = await this.LookupAsync(connection, request.TmdbId, ct);
        int tvdbId = ArrJson.Int(lookup["tvdbId"]) ?? 0;
        if (tvdbId <= 0)
        {
            throw new ArrException("Sonarr has no TVDB id for that show, so it cannot be added.");
        }

        IReadOnlyList<int> seasons = request.Seasons.Count > 0 ? request.Seasons : SonarrMapper.AllSeasons(lookup);
        RequireSeasons(lookup, seasons);

        if (await this.FindSeriesAsync(connection, tvdbId, ct) is { } present)
        {
            return await this.ReuseAsync(connection, tagIds, seasons, present, ct);
        }

        JsonObject body = SonarrMapper.ToAddBody(lookup, connection, tagIds, seasons);
        try
        {
            JsonNode? added = await this.SendAsync(connection, HttpMethod.Post, "api/v3/series", body, ct);
            return new FulfilmentResult(FulfilmentOutcome.Added, ArrJson.Int(added?["id"]) ?? 0);
        }
        catch (ArrAlreadyExistsException)
        {
            JsonObject raced = await this.FindSeriesAsync(connection, tvdbId, ct)
                ?? throw new ArrException("Sonarr says the show already exists but did not list it.");
            return await this.ReuseAsync(connection, tagIds, seasons, raced, ct);
        }
    }

    private async Task<JsonObject?> FindSeriesAsync(ArrConnection connection, int tvdbId, CancellationToken ct)
    {
        JsonArray existing = await this.GetArrayAsync(connection, $"api/v3/series?tvdbId={Number(tvdbId)}", ct);
        return existing.FirstOrDefault() is JsonObject series && ArrJson.Int(series["id"]) is not null ? series : null;
    }

    private async Task<FulfilmentResult> ReuseAsync(
        ArrConnection connection,
        IReadOnlyList<int> tagIds,
        IReadOnlyList<int> seasons,
        JsonObject series,
        CancellationToken ct)
    {
        RequireSeasons(series, seasons);
        int seriesId = ArrJson.Int(series["id"])!.Value;
        if (SonarrMapper.ApplyToExisting(series, tagIds, seasons))
        {
            await this.SendAsync(connection, HttpMethod.Put, $"api/v3/series/{Number(seriesId)}", series, ct);
        }

        foreach (int season in seasons.Distinct().Order())
        {
            await this.SendAsync(
                connection,
                HttpMethod.Post,
                "api/v3/command",
                new JsonObject { ["name"] = "SeasonSearch", ["seriesId"] = seriesId, ["seasonNumber"] = season },
                ct);
        }

        return new FulfilmentResult(FulfilmentOutcome.Reused, seriesId);
    }

    private static void RequireSeasons(JsonObject series, IReadOnlyList<int> seasons)
    {
        IReadOnlyList<int> listed = SonarrMapper.MatchedSeasons(series, seasons);
        if (seasons.Count == 0 || seasons.Except(listed).Any())
        {
            throw new ArrException("Sonarr does not list all of the requested seasons yet.");
        }
    }

    private async Task<JsonObject> LookupAsync(ArrConnection connection, int tmdbId, CancellationToken ct)
    {
        JsonArray results = await this.GetArrayAsync(connection, $"api/v3/series/lookup?term=tmdb%3A{Number(tmdbId)}", ct);
        return results.OfType<JsonObject>().FirstOrDefault(result => ArrJson.Int(result["tmdbId"]) == tmdbId)
            ?? throw new ArrException("Sonarr could not find that show.");
    }
}

public sealed class RadarrBackend(ArrClient client, ArrConnectionStore store) : ArrBackend(ArrService.Radarr, client, store)
{
    protected override async Task<FulfilmentResult> FulfilCoreAsync(
        ArrConnection connection,
        IReadOnlyList<int> tagIds,
        FulfilmentRequest request,
        CancellationToken ct)
    {
        if (request.Kind != MediaKind.Movie)
        {
            throw new ArgumentException("Radarr only handles movies.", nameof(request));
        }

        if (await this.FindMovieAsync(connection, request.TmdbId, ct) is { } present)
        {
            return await this.ReuseAsync(connection, tagIds, present, ct);
        }

        JsonObject lookup = await this.GetObjectAsync(connection, $"api/v3/movie/lookup/tmdb?tmdbId={Number(request.TmdbId)}", ct);
        try
        {
            JsonNode? added = await this.SendAsync(connection, HttpMethod.Post, "api/v3/movie", RadarrMapper.ToAddBody(lookup, connection, tagIds), ct);
            return new FulfilmentResult(FulfilmentOutcome.Added, ArrJson.Int(added?["id"]) ?? 0);
        }
        catch (ArrAlreadyExistsException)
        {
            JsonObject raced = await this.FindMovieAsync(connection, request.TmdbId, ct)
                ?? throw new ArrException("Radarr says the movie already exists but did not list it.");
            return await this.ReuseAsync(connection, tagIds, raced, ct);
        }
    }

    private async Task<JsonObject?> FindMovieAsync(ArrConnection connection, int tmdbId, CancellationToken ct)
    {
        JsonArray existing = await this.GetArrayAsync(connection, $"api/v3/movie?tmdbId={Number(tmdbId)}", ct);
        return existing.FirstOrDefault() is JsonObject movie && ArrJson.Int(movie["id"]) is not null ? movie : null;
    }

    private async Task<FulfilmentResult> ReuseAsync(ArrConnection connection, IReadOnlyList<int> tagIds, JsonObject movie, CancellationToken ct)
    {
        int movieId = ArrJson.Int(movie["id"])!.Value;
        if (RadarrMapper.ApplyToExisting(movie, tagIds))
        {
            await this.SendAsync(connection, HttpMethod.Put, $"api/v3/movie/{Number(movieId)}", movie, ct);
        }

        if (!ArrJson.Bool(movie["hasFile"]))
        {
            await this.SendAsync(
                connection,
                HttpMethod.Post,
                "api/v3/command",
                new JsonObject { ["name"] = "MoviesSearch", ["movieIds"] = new JsonArray(movieId) },
                ct);
        }

        return new FulfilmentResult(FulfilmentOutcome.Reused, movieId);
    }
}
