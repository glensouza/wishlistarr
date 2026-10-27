using System.Text.Json.Nodes;
using Wishlistarr.Core.Fulfilment;

namespace Wishlistarr.Integrations.Arr;

internal static class ArrJson
{
    public static int? Int(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out int number) ? number : null;

    public static bool Bool(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out bool flag) && flag;

    public static string? String(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    public static bool MergeTags(JsonObject item, IEnumerable<int> tagIds)
    {
        List<int> current = item["tags"] is JsonArray array ? [.. array.Select(Int).OfType<int>()] : [];
        List<int> merged = [.. current.Union(tagIds).Order()];
        if (merged.SequenceEqual(current.Order()) && item["tags"] is not null)
        {
            return false;
        }

        item["tags"] = new JsonArray([.. merged.Select(id => (JsonNode)JsonValue.Create(id))]);
        return true;
    }
}

internal static class SonarrMapper
{
    public static JsonObject ToAddBody(JsonObject lookup, ArrConnection connection, IEnumerable<int> tagIds, IReadOnlyCollection<int> seasons)
    {
        JsonObject body = (JsonObject)lookup.DeepClone();
        body.Remove("id");
        body["qualityProfileId"] = connection.QualityProfileId;
        body["rootFolderPath"] = connection.RootFolderPath;
        body["monitored"] = true;
        body["seasonFolder"] = true;
        ArrJson.MergeTags(body, tagIds);
        SetSeasons(body, seasons);
        body["addOptions"] = new JsonObject
        {
            ["ignoreEpisodesWithFiles"] = false,
            ["ignoreEpisodesWithoutFiles"] = false,
            ["searchForMissingEpisodes"] = true,
        };
        return body;
    }

    public static bool ApplyToExisting(JsonObject series, IEnumerable<int> tagIds, IReadOnlyCollection<int> seasons)
    {
        bool changed = ArrJson.MergeTags(series, tagIds);
        if (!ArrJson.Bool(series["monitored"]))
        {
            series["monitored"] = true;
            changed = true;
        }

        foreach (JsonNode? season in series["seasons"] as JsonArray ?? [])
        {
            if (season is JsonObject entry
                && ArrJson.Int(entry["seasonNumber"]) is { } number
                && seasons.Contains(number)
                && !ArrJson.Bool(entry["monitored"]))
            {
                entry["monitored"] = true;
                changed = true;
            }
        }

        return changed;
    }

    public static IReadOnlyList<int> AllSeasons(JsonObject series) =>
        [.. (series["seasons"] as JsonArray ?? [])
            .Select(season => ArrJson.Int(season?["seasonNumber"]))
            .OfType<int>()
            .Where(number => number > 0)
            .Order()];

    public static IReadOnlyList<int> MatchedSeasons(JsonObject series, IReadOnlyCollection<int> seasons) =>
        [.. (series["seasons"] as JsonArray ?? [])
            .Select(season => ArrJson.Int(season?["seasonNumber"]))
            .OfType<int>()
            .Where(seasons.Contains)
            .Order()];

    private static void SetSeasons(JsonObject body, IReadOnlyCollection<int> seasons)
    {
        foreach (JsonNode? season in body["seasons"] as JsonArray ?? [])
        {
            if (season is JsonObject entry)
            {
                entry["monitored"] = ArrJson.Int(entry["seasonNumber"]) is { } number && seasons.Contains(number);
            }
        }
    }
}

internal static class RadarrMapper
{
    public static JsonObject ToAddBody(JsonObject lookup, ArrConnection connection, IEnumerable<int> tagIds)
    {
        JsonObject body = (JsonObject)lookup.DeepClone();
        body.Remove("id");
        body["qualityProfileId"] = connection.QualityProfileId;
        body["rootFolderPath"] = connection.RootFolderPath;
        body["monitored"] = true;
        body["minimumAvailability"] = "released";
        ArrJson.MergeTags(body, tagIds);
        body["addOptions"] = new JsonObject
        {
            ["monitor"] = "movieOnly",
            ["searchForMovie"] = true,
        };
        return body;
    }

    public static bool ApplyToExisting(JsonObject movie, IEnumerable<int> tagIds)
    {
        bool changed = ArrJson.MergeTags(movie, tagIds);
        if (!ArrJson.Bool(movie["monitored"]))
        {
            movie["monitored"] = true;
            changed = true;
        }

        return changed;
    }
}
