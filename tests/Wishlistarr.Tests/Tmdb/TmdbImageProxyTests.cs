using Microsoft.Extensions.Options;
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
    public async Task Cached_images_are_reused_per_size_and_cleared_with_tmdb_data()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync();

        await harness.Images.FetchAsync("w500", "poster.jpg");
        await harness.Images.FetchAsync("w500", "poster.jpg");
        await harness.Images.FetchAsync("w342", "poster.jpg");
        Assert.Equal(2, harness.Server.Requests.Count);

        harness.Cache.Clear();
        await harness.Images.FetchAsync("w500", "poster.jpg");
        Assert.Equal(3, harness.Server.Requests.Count);
    }

    [Fact]
    public async Task Cache_evicts_least_recently_used_images_to_stay_within_its_byte_limit()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync();
        TmdbImageContent image = new(new byte[20 * 1024 * 1024], "image/png");
        harness.Cache.TryGetImage("one", out _, out long generation);
        harness.Cache.SetImage("one", image, generation);
        harness.Cache.SetImage("two", image, generation);
        Assert.True(harness.Cache.TryGetImage("one", out _, out _));

        harness.Cache.SetImage("three", image, generation);

        Assert.False(harness.Cache.TryGetImage("two", out _, out _));
        Assert.True(harness.Cache.TryGetImage("one", out _, out _));
        Assert.True(harness.Cache.TryGetImage("three", out _, out _));
    }

    [Fact]
    public void Cached_images_expire_even_when_accessed()
    {
        ManualTimeProvider time = new();
        using TmdbCache cache = new(Options.Create(new TmdbOptions()), time);
        cache.TryGetImage("one", out _, out long generation);
        cache.SetImage("one", new TmdbImageContent([1], "image/png"), generation);
        time.Advance(TimeSpan.FromDays(6));
        Assert.True(cache.TryGetImage("one", out _, out _));
        time.Advance(TimeSpan.FromDays(1));
        Assert.False(cache.TryGetImage("one", out _, out _));
    }

    [Fact]
    public void Clearing_prevents_an_in_flight_image_from_repopulating_the_cache()
    {
        using TmdbCache cache = new(Options.Create(new TmdbOptions()), TimeProvider.System);
        cache.TryGetImage("one", out _, out long generation);
        cache.Clear();
        cache.SetImage("one", new TmdbImageContent([1], "image/png"), generation);
        Assert.False(cache.TryGetImage("one", out _, out _));
    }

    [Fact]
    public void Many_small_images_are_also_bounded()
    {
        using TmdbCache cache = new(Options.Create(new TmdbOptions()), TimeProvider.System);
        cache.TryGetImage("0", out _, out long generation);
        TmdbImageContent image = new([1], "image/png");
        for (int i = 0; i <= 2000; i++) cache.SetImage(i.ToString(System.Globalization.CultureInfo.InvariantCulture), image, generation);
        Assert.False(cache.TryGetImage("0", out _, out _));
        Assert.True(cache.TryGetImage("2000", out _, out _));
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
