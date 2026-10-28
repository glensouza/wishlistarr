using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Integrations.Plex;

namespace Wishlistarr.Tests.Plex;

public sealed class PlexTrailerTests
{
    [Fact]
    public async Task Finds_only_trailer_extras_and_streams_the_selected_local_part_with_range()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        await harness.LinkAndSelectAsync();
        harness.Server.ExtrasJson = """{"MediaContainer":{"Metadata":[{"type":"clip","subtype":"interview","Media":[{"container":"mp4","videoCodec":"h264","Part":[{"key":"/library/parts/888/file.mp4"}]}]},{"type":"clip","subtype":"trailer","Media":[{"container":"mp4","videoCodec":"h264","Part":[{"key":"/library/parts/1010/file.mp4"}]}]}]}}""";
        IMediaServerTrailers trailers = harness.Provider.GetRequiredService<IMediaServerTrailers>();

        Assert.True(await trailers.HasTrailerAsync("101"));
        using HttpResponseMessage? response = await trailers.OpenTrailerAsync("101", "bytes=1-2");
        Assert.NotNull(response);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(new byte[] { 2, 3 }, await response.Content.ReadAsByteArrayAsync());
        Assert.Contains(harness.Server.Requests, request => request.Path == "/library/parts/1010/file.mp4" && request.Token == FakePlexServer.ServerToken);
        Assert.DoesNotContain(harness.Server.Requests, request => request.Path == "/library/parts/888/file.mp4");
    }

    [Fact]
    public async Task Rejects_external_and_non_trailer_parts()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        await harness.LinkAndSelectAsync();
        harness.Server.ExtrasJson = """{"MediaContainer":{"Metadata":[{"type":"clip","subtype":"trailer","Media":[{"container":"mp4","videoCodec":"h264","Part":[{"key":"https://example.invalid/file.mp4"}]}]},{"type":"clip","subtype":"interview","Media":[{"container":"mp4","videoCodec":"h264","Part":[{"key":"/library/parts/1010/file.mp4"}]}]}]}}""";
        IMediaServerTrailers trailers = harness.Provider.GetRequiredService<IMediaServerTrailers>();
        Assert.False(await trailers.HasTrailerAsync("101"));
        Assert.Null(await trailers.OpenTrailerAsync("101", null));
    }

    [Fact]
    public async Task Streams_plex_hosted_iva_trailer_without_sending_the_token_elsewhere()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        await harness.LinkAndSelectAsync();
        harness.Server.ExtrasJson = """{"MediaContainer":{"Metadata":[{"type":"clip","subtype":"trailer","Media":[{"container":"mp4","videoCodec":"h264","audioCodec":"aac","Part":[{"key":"/services/iva/assets/4900/video.mp4?fmt=4&bitrate=1500"}]}]}]}}""";
        IMediaServerTrailers trailers = harness.Provider.GetRequiredService<IMediaServerTrailers>();
        using HttpResponseMessage? response = await trailers.OpenTrailerAsync("101", null);
        Assert.NotNull(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3 }, await response.Content.ReadAsByteArrayAsync());
        Assert.Contains(harness.Server.Requests, request => request.Path == "/services/iva/assets/4900/video.mp4" && request.Token == FakePlexServer.ServerToken);
        Assert.DoesNotContain(harness.Server.ImpostorRequests, request => request.Token is not null);
    }
}
