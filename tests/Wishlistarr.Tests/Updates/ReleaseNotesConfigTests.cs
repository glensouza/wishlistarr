namespace Wishlistarr.Tests.Updates;

public sealed class ReleaseNotesConfigTests
{
    [Fact]
    public void Generated_notes_use_branch_prefixes_and_area_labels()
    {
        string root = RepoRoot();
        string notes = File.ReadAllText(Path.Combine(root, ".github", "release.yml"));
        int security = IndexOf(notes, "Security");
        int features = IndexOf(notes, "Features");
        int fixes = IndexOf(notes, "Fixes");
        int documentation = IndexOf(notes, "Documentation");
        int maintenance = IndexOf(notes, "Maintenance");
        int other = IndexOf(notes, "Other changes");
        Assert.True(security < features && features < fixes && fixes < documentation && documentation < maintenance && maintenance < other);

        Assert.Contains("\"area:security\"", notes, StringComparison.Ordinal);
        Assert.Contains("\n        - feat\n", notes, StringComparison.Ordinal);
        Assert.Contains("\n        - fix\n", notes, StringComparison.Ordinal);
        Assert.Contains("\n        - docs\n", notes, StringComparison.Ordinal);
        Assert.Contains("\"area:docs\"", notes, StringComparison.Ordinal);
        Assert.Contains("\n        - chore\n", notes, StringComparison.Ordinal);
        Assert.Contains("\"area:devops\"", notes, StringComparison.Ordinal);
        Assert.Contains("\"area:testing\"", notes, StringComparison.Ordinal);
        Assert.Contains("\"*\"", notes, StringComparison.Ordinal);

        string workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
        Assert.Contains("gh release create", workflow, StringComparison.Ordinal);
        Assert.Contains("--generate-notes", workflow, StringComparison.Ordinal);
        Assert.Contains(".github/release.yml", workflow, StringComparison.Ordinal);
        Assert.Contains("Missing .github/release.yml", workflow, StringComparison.Ordinal);

        string releasing = File.ReadAllText(Path.Combine(root, "docs", "releasing.md"));
        Assert.Contains("--generate-notes", releasing, StringComparison.Ordinal);
        Assert.Contains(".github/release.yml", releasing, StringComparison.Ordinal);
        Assert.Contains("area:security", releasing, StringComparison.Ordinal);
    }

    private static int IndexOf(string text, string value)
    {
        int index = text.IndexOf(value, StringComparison.Ordinal);
        Assert.True(index >= 0, value);
        return index;
    }

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wishlistarr.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
