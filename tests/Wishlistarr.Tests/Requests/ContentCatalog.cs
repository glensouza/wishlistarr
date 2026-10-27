using System.Text.Json.Nodes;
using Wishlistarr.Tests.Tmdb;

namespace Wishlistarr.Tests.Requests;

internal static class ContentCatalog
{
    internal static void SetCatalog(this FakeTmdbServer tmdb, string path, string json)
    {
        tmdb.SetBody(path, json);
        foreach (JsonNode? result in JsonNode.Parse(json)!["results"]!.AsArray())
        {
            JsonObject details = result!.DeepClone().AsObject();
            bool movie = path.EndsWith("/movie", StringComparison.Ordinal) || details["media_type"]?.GetValue<string>() == "movie";
            if (details["media_type"]?.GetValue<string>() == "person") continue;
            if (details["genre_ids"] is JsonArray ids)
                details["genres"] = new JsonArray([.. ids.Select(id => (JsonNode)new JsonObject { ["id"] = id!.DeepClone(), ["name"] = "Genre" })]);
            tmdb.SetBody($"/3/{(movie ? "movie" : "tv")}/{details["id"]}", details.ToJsonString());
        }
    }
}
