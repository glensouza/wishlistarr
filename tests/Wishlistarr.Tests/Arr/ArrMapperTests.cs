using System.Text.Json.Nodes;
using Wishlistarr.Core.Fulfilment;
using Wishlistarr.Integrations.Arr;

namespace Wishlistarr.Tests.Arr;

public sealed class ArrMapperTests
{
    private static readonly ArrConnection Connection = new(new Uri("http://sonarr.test/"), "key", 4, "/media/kids", [1]);

    [Fact]
    public void Sonarr_add_body_uses_the_chosen_profile_folder_tag_and_searches_on_add()
    {
        JsonObject lookup = Show();

        JsonObject body = SonarrMapper.ToAddBody(lookup, Connection, [1, 7], [2]);

        Assert.Equal(4, body["qualityProfileId"]!.GetValue<int>());
        Assert.Equal("/media/kids", body["rootFolderPath"]!.GetValue<string>());
        Assert.True(body["monitored"]!.GetValue<bool>());
        Assert.Equal([1, 7], body["tags"]!.AsArray().Select(tag => tag!.GetValue<int>()));
        Assert.True(body["addOptions"]!["searchForMissingEpisodes"]!.GetValue<bool>());
        Assert.Equal("Fixture Show", body["title"]!.GetValue<string>());
    }

    [Fact]
    public void Sonarr_add_body_monitors_only_the_requested_seasons()
    {
        JsonObject body = SonarrMapper.ToAddBody(Show(), Connection, [1], [1, 3]);

        Dictionary<int, bool> monitored = body["seasons"]!.AsArray()
            .ToDictionary(season => season!["seasonNumber"]!.GetValue<int>(), season => season!["monitored"]!.GetValue<bool>());
        Assert.Equal(new Dictionary<int, bool> { [0] = false, [1] = true, [2] = false, [3] = true }, monitored);
    }

    [Fact]
    public void Sonarr_add_body_leaves_the_lookup_untouched()
    {
        JsonObject lookup = Show();
        string before = lookup.ToJsonString();

        SonarrMapper.ToAddBody(lookup, Connection, [1], [2]);

        Assert.Equal(before, lookup.ToJsonString());
    }

    [Fact]
    public void Sonarr_existing_series_gains_the_season_and_tag_without_losing_others()
    {
        JsonObject series = Show();
        series["monitored"] = false;
        series["tags"] = new JsonArray(5);
        series["seasons"]!.AsArray()[1]!["monitored"] = true;

        bool changed = SonarrMapper.ApplyToExisting(series, [1], [3]);

        Assert.True(changed);
        Assert.True(series["monitored"]!.GetValue<bool>());
        Assert.Equal([1, 5], series["tags"]!.AsArray().Select(tag => tag!.GetValue<int>()));
        Assert.Equal(
            [false, true, false, true],
            series["seasons"]!.AsArray().Select(season => season!["monitored"]!.GetValue<bool>()));
    }

    [Fact]
    public void Sonarr_existing_series_that_already_matches_is_unchanged()
    {
        JsonObject series = Show();
        series["monitored"] = true;
        series["tags"] = new JsonArray(1);
        series["seasons"]!.AsArray()[2]!["monitored"] = true;

        Assert.False(SonarrMapper.ApplyToExisting(series, [1], [2]));
    }

    [Fact]
    public void Radarr_add_body_uses_the_chosen_profile_folder_tag_and_searches_on_add()
    {
        JsonObject body = RadarrMapper.ToAddBody(new JsonObject { ["title"] = "Fixture Movie", ["tmdbId"] = 603, ["id"] = 0 }, Connection, [1, 9]);

        Assert.Equal(4, body["qualityProfileId"]!.GetValue<int>());
        Assert.Equal("/media/kids", body["rootFolderPath"]!.GetValue<string>());
        Assert.True(body["monitored"]!.GetValue<bool>());
        Assert.Equal([1, 9], body["tags"]!.AsArray().Select(tag => tag!.GetValue<int>()));
        Assert.True(body["addOptions"]!["searchForMovie"]!.GetValue<bool>());
        Assert.Null(body["id"]);
    }

    [Fact]
    public void Radarr_existing_movie_is_monitored_and_tagged()
    {
        JsonObject movie = new() { ["monitored"] = false, ["tags"] = new JsonArray() };

        Assert.True(RadarrMapper.ApplyToExisting(movie, [2]));
        Assert.True(movie["monitored"]!.GetValue<bool>());
        Assert.Equal([2], movie["tags"]!.AsArray().Select(tag => tag!.GetValue<int>()));
        Assert.False(RadarrMapper.ApplyToExisting(movie, [2]));
    }

    [Theory]
    [InlineData("http://sonarr.local:8989", "http://sonarr.local:8989/")]
    [InlineData("https://media.example.com/sonarr", "https://media.example.com/sonarr/")]
    [InlineData("  http://10.0.0.5:7878/  ", "http://10.0.0.5:7878/")]
    public void Valid_urls_are_normalized_with_a_trailing_slash(string input, string expected)
    {
        Assert.True(ArrSettings.TryNormalizeUrl(input, out Uri url));
        Assert.Equal(expected, url.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sonarr.local:8989")]
    [InlineData("ftp://sonarr.local/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://user:pass@sonarr.local/")]
    [InlineData("http://sonarr.local/?apikey=abc")]
    [InlineData("http://sonarr.local/#frag")]
    public void Invalid_urls_are_rejected(string input) => Assert.False(ArrSettings.TryNormalizeUrl(input, out _));

    private static JsonObject Show() => new()
    {
        ["title"] = "Fixture Show",
        ["tmdbId"] = 1000,
        ["tvdbId"] = 2000,
        ["seasons"] = new JsonArray(
            Season(0),
            Season(1),
            Season(2),
            Season(3)),
    };

    private static JsonObject Season(int number) => new() { ["seasonNumber"] = number, ["monitored"] = false };
}
