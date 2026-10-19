using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Tests.Tmdb;

public sealed class TmdbImageProxyTests
{
    [Theory]
    [InlineData("w500", "poster.jpg")]
    [InlineData("original", "Abc_123-x.png")]
    [InlineData("h632", "p.jpeg")]
    public void Valid_sizes_and_files_are_allowed(string size, string file) =>
        Assert.True(TmdbImageProxy.IsAllowed(size, file));

    [Theory]
    [InlineData("w501", "poster.jpg")]
    [InlineData("W500", "poster.jpg")]
    [InlineData("", "poster.jpg")]
    [InlineData("../w500", "poster.jpg")]
    [InlineData("w500", "../poster.jpg")]
    [InlineData("w500", "..")]
    [InlineData("w500", "a/b.jpg")]
    [InlineData("w500", "a\\b.jpg")]
    [InlineData("w500", "poster.svg")]
    [InlineData("w500", "poster.jpg?x=1")]
    [InlineData("w500", "poster.jpg#x")]
    [InlineData("w500", "poster")]
    [InlineData("w500", "//evil.test/x.jpg")]
    [InlineData("w500", "http://evil.test/x.jpg")]
    [InlineData("w500", "%2e%2e.jpg")]
    [InlineData("w500", "poster.jpg\n")]
    [InlineData("w500", "")]
    public void Anything_else_is_rejected(string size, string file) =>
        Assert.False(TmdbImageProxy.IsAllowed(size, file));

    [Fact]
    public async Task Rejected_requests_never_reach_the_network()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Images.FetchAsync("w501", "poster.jpg"));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Images.FetchAsync("w500", "../x.jpg"));

        Assert.Empty(harness.Server.Requests);
    }

    [Fact]
    public async Task Allowed_image_is_fetched_from_the_configured_host_only()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync();

        TmdbImageContent? image = await harness.Images.FetchAsync("w500", "poster.jpg");

        Assert.NotNull(image);
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal(FakeTmdbServer.ImageBytes, image.Bytes);
        Assert.Equal("/t/p/w500/poster.jpg", Assert.Single(harness.Server.Requests).Path);
    }

    [Fact]
    public async Task Redirects_are_not_followed()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync();

        await Assert.ThrowsAsync<TmdbException>(() => harness.Images.FetchAsync("w500", "redirect.jpg"));

        Assert.Single(harness.Server.Requests);
    }

    [Fact]
    public async Task Missing_images_return_null()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync();

        Assert.Null(await harness.Images.FetchAsync("w500", "missing.jpg"));
    }

    [Fact]
    public async Task Non_image_and_oversized_responses_are_refused()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync();

        await Assert.ThrowsAsync<TmdbException>(() => harness.Images.FetchAsync("w500", "page.jpg"));
        await Assert.ThrowsAsync<TmdbException>(() => harness.Images.FetchAsync("w500", "huge.jpg"));
    }
}
