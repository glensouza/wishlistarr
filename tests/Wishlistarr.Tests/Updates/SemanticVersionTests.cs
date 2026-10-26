using Wishlistarr.Api.System;
using Wishlistarr.Api.Updates;

namespace Wishlistarr.Tests.Updates;

public sealed class SemanticVersionTests
{
    [Fact]
    public void Precedence_matches_the_semantic_version_spec()
    {
        string[] ordered =
        [
            "1.0.0-alpha",
            "1.0.0-alpha.1",
            "1.0.0-alpha.beta",
            "1.0.0-beta",
            "1.0.0-beta.2",
            "1.0.0-beta.11",
            "1.0.0-rc.1",
            "1.0.0",
        ];

        SemanticVersion[] versions = [.. ordered.Select(Parse)];
        for (int left = 0; left < versions.Length; left++)
        {
            Assert.Equal(0, versions[left].CompareTo(Parse(ordered[left])));
            Assert.Equal(ordered[left], versions[left].ToString());
            for (int right = left + 1; right < versions.Length; right++)
            {
                Assert.True(versions[left].CompareTo(versions[right]) < 0, $"{ordered[left]} < {ordered[right]}");
                Assert.True(versions[right].CompareTo(versions[left]) > 0, $"{ordered[right]} > {ordered[left]}");
            }
        }

        Assert.True(Parse("1.0.0-1").CompareTo(Parse("1.0.0-alpha")) < 0);
        Assert.True(Parse("1.0.0-999999999999").CompareTo(Parse("1.0.0-2")) > 0);
        Assert.True(Parse("2.0.0").CompareTo(Parse("1.9.9")) > 0);
        Assert.True(Parse("1.1.0").CompareTo(Parse("1.0.9")) > 0);
    }

    [Fact]
    public void Rejects_versions_the_release_workflow_would_reject()
    {
        Assert.False(SemanticVersion.TryParse(null, out _));
        Assert.False(SemanticVersion.TryParse("", out _));
        Assert.False(SemanticVersion.TryParse("1.0", out _));
        Assert.False(SemanticVersion.TryParse("01.0.0", out _));
        Assert.False(SemanticVersion.TryParse("1.0.0+build", out _));
        Assert.False(SemanticVersion.TryParse("v1.0.0", out _));
        Assert.False(SemanticVersion.TryParse("1.2.3\n", out _));
        Assert.False(SemanticVersion.TryParse("1.2.3-rc.1\n", out _));
        Assert.False(SemanticVersion.TryParseTag("v1.2.3\n", out _));
        Assert.False(SemanticVersion.TryParse("999999999999.0.0", out _));
        Assert.False(SemanticVersion.TryParseTag("1.2.3", out _));
        Assert.False(SemanticVersion.TryParseTag("V1.2.3", out _));

        Assert.True(SemanticVersion.TryParseTag("v1.2.3-rc.1", out SemanticVersion? tagged));
        Assert.NotNull(tagged);
        Assert.Equal("1.2.3-rc.1", tagged.ToString());
        Assert.True(tagged.IsPrerelease);
    }

    [Fact]
    public void Running_version_is_a_stable_semantic_version()
    {
        Assert.Equal("1.2.3", AppVersion.Normalize("1.2.3+abc"));
        Assert.Equal("1.2.3-rc.1", AppVersion.Normalize("1.2.3-rc.1+sha"));
        Assert.Equal("0.0.0", AppVersion.Normalize(null));
        Assert.Equal("0.0.0", AppVersion.Normalize("  "));

        Assert.True(SemanticVersion.TryParse(AppVersion.Current, out SemanticVersion? current));
        Assert.NotNull(current);
        Assert.False(current.IsPrerelease);
    }

    [Fact]
    public void Stable_install_ignores_drafts_and_prereleases_and_picks_the_highest()
    {
        ChosenRelease? chosen = ReleaseSelection.Choose(
            Parse("1.2.3"),
            [
                new PublishedRelease("v1.2.9-rc.1", false, false),
                new PublishedRelease("v1.2.4", false, true),
                new PublishedRelease("v9.0.0", true, false),
                new PublishedRelease("v1.2.3", false, false),
                new PublishedRelease("v1.2.2", false, false),
                new PublishedRelease("not-a-tag", false, false),
                new PublishedRelease("V1.9.0", false, false),
                new PublishedRelease("v1.2.4", false, false),
                new PublishedRelease("v1.2.6", false, false),
                new PublishedRelease("v1.2.5", false, false),
            ]);

        Assert.NotNull(chosen);
        Assert.Equal("v1.2.6", chosen.Tag);
        Assert.Equal("1.2.6", chosen.Version.ToString());
    }

    [Fact]
    public void Prerelease_install_can_select_a_newer_prerelease()
    {
        ChosenRelease? chosen = ReleaseSelection.Choose(
            Parse("1.3.0-rc.1"),
            [
                new PublishedRelease("v1.3.0-rc.2", false, true),
                new PublishedRelease("v1.4.0-rc.1", false, true),
                new PublishedRelease("v9.0.0", true, false),
                new PublishedRelease("v1.2.0", false, false),
                new PublishedRelease("v1.3.0", false, false),
            ]);

        Assert.NotNull(chosen);
        Assert.Equal("v1.4.0-rc.1", chosen.Tag);
    }

    [Fact]
    public void Nothing_newer_selects_nothing()
    {
        Assert.Null(ReleaseSelection.Choose(Parse("1.2.3"), []));
        Assert.Null(ReleaseSelection.Choose(
            Parse("1.2.3"),
            [
                new PublishedRelease("v1.2.3", false, false),
                new PublishedRelease("v1.2.4-rc.1", false, false),
                new PublishedRelease("v2.0.0", true, false),
            ]));
    }

    private static SemanticVersion Parse(string text)
    {
        Assert.True(SemanticVersion.TryParse(text, out SemanticVersion? version));
        Assert.NotNull(version);
        return version;
    }
}
