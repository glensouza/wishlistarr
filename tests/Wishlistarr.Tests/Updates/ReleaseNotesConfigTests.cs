using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Wishlistarr.Tests.Updates;

public sealed partial class ReleaseNotesConfigTests
{
    private const string CatchAll = "*";

    [Fact]
    public void Every_category_label_can_be_applied_by_the_labeler_or_by_hand()
    {
        HashSet<string> applied = LabelerRules().Keys.ToHashSet(StringComparer.Ordinal);
        HashSet<string> byHand = AppliedByHand();
        Assert.NotEmpty(applied);
        Assert.NotEmpty(byHand);

        Assert.Empty(UnappliedLabels(Categories(), applied, byHand));
    }

    [Fact]
    public void A_category_with_a_label_nothing_applies_is_reported()
    {
        HashSet<string> applied = LabelerRules().Keys.ToHashSet(StringComparer.Ordinal);
        HashSet<string> byHand = AppliedByHand();
        List<Category> categories =
        [
            new("Features", ["feat"]),
            new("Fixes", ["bug"]),
            new("Other changes", [CatchAll]),
        ];

        Assert.Equal(["Features: feat"], UnappliedLabels(categories, applied, byHand));
    }

    [Fact]
    public void Every_branch_prefix_in_the_contributing_guide_maps_to_a_label()
    {
        Dictionary<string, List<string>> rules = LabelerRules();
        string contributing = File.ReadAllText(Path.Combine(RepoRoot(), "CONTRIBUTING.md"));
        string line = contributing.Split('\n').Single(item => item.StartsWith("Use a short prefix", StringComparison.Ordinal));
        string[] prefixes = [.. BranchPrefix().Matches(line).Select(match => match.Groups[1].Value).Distinct()];
        Assert.Equal(["feat", "fix", "docs", "chore"], prefixes);

        string releasing = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "releasing.md"));
        foreach (string prefix in prefixes)
        {
            string pattern = "^" + prefix + "/";
            string[] labels = [.. rules.Where(rule => rule.Value.Contains(pattern)).Select(rule => rule.Key)];
            string label = Assert.Single(labels);
            Assert.Contains($"| `{prefix}/` | `{label}` |", releasing, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Release_notes_have_a_catch_all_category_last_and_no_dependabot_entries()
    {
        List<Category> categories = Categories();
        Category last = categories[^1];
        Assert.Equal([CatchAll], last.Labels);
        Assert.DoesNotContain(categories.SkipLast(1), category => category.Labels.Contains(CatchAll));

        YamlMappingNode changelog = (YamlMappingNode)((YamlMappingNode)Load(".github/release.yml"))["changelog"];
        string[] authors = [.. ((YamlSequenceNode)((YamlMappingNode)changelog["exclude"])["authors"]).Children.Select(Scalar)];
        Assert.Contains("dependabot", authors);
    }

    [Fact]
    public void The_labeler_workflow_is_pinned_and_has_minimal_permissions()
    {
        YamlMappingNode workflow = (YamlMappingNode)Load(".github/workflows/labeler.yml");
        YamlMappingNode permissions = (YamlMappingNode)workflow["permissions"];
        Assert.Equal(2, permissions.Children.Count);
        Assert.Equal("read", Scalar(permissions["contents"]));
        Assert.Equal("write", Scalar(permissions["pull-requests"]));

        string text = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "labeler.yml"));
        Assert.Contains("pull_request_target", text, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/checkout", text, StringComparison.Ordinal);
        Assert.Matches(@"uses: actions/labeler@[0-9a-f]{40}\b", text);
    }

    [Fact]
    public void The_release_workflow_generates_notes_from_the_configuration()
    {
        string root = RepoRoot();
        string workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
        Assert.Contains("gh release create", workflow, StringComparison.Ordinal);
        Assert.Contains("--generate-notes", workflow, StringComparison.Ordinal);
        Assert.Contains("Missing .github/release.yml", workflow, StringComparison.Ordinal);
    }

    private static List<string> UnappliedLabels(IEnumerable<Category> categories, HashSet<string> applied, HashSet<string> byHand) =>
        [.. categories
            .SelectMany(category => category.Labels
                .Where(label => label != CatchAll && !applied.Contains(label) && !byHand.Contains(label))
                .Select(label => $"{category.Title}: {label}"))];

    private static List<Category> Categories()
    {
        YamlMappingNode changelog = (YamlMappingNode)((YamlMappingNode)Load(".github/release.yml"))["changelog"];
        return [.. ((YamlSequenceNode)changelog["categories"]).Children
            .Cast<YamlMappingNode>()
            .Select(node => new Category(
                Scalar(node["title"]),
                [.. ((YamlSequenceNode)node["labels"]).Children.Select(Scalar)]))];
    }

    private static Dictionary<string, List<string>> LabelerRules()
    {
        Dictionary<string, List<string>> rules = new(StringComparer.Ordinal);
        foreach (KeyValuePair<YamlNode, YamlNode> entry in (YamlMappingNode)Load(".github/labeler.yml"))
        {
            List<string> patterns = [];
            foreach (YamlMappingNode matcher in ((YamlSequenceNode)entry.Value).Children.Cast<YamlMappingNode>())
            {
                patterns.AddRange(((YamlSequenceNode)matcher["head-branch"]).Children.Select(Scalar));
            }

            rules[Scalar(entry.Key)] = patterns;
        }

        return rules;
    }

    private static HashSet<string> AppliedByHand()
    {
        string releasing = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "releasing.md"));
        string line = releasing.Split('\n').Single(item => item.StartsWith("Applied by hand:", StringComparison.Ordinal));
        return Backticked().Matches(line).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
    }

    private static YamlNode Load(string relativePath)
    {
        YamlStream stream = [];
        using StringReader reader = new(File.ReadAllText(Path.Combine(RepoRoot(), relativePath)));
        stream.Load(reader);
        return stream.Documents[0].RootNode;
    }

    private static string Scalar(YamlNode node) => ((YamlScalarNode)node).Value!;

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

    [GeneratedRegex(@"`([a-z]+)/[a-z0-9-]+`")]
    private static partial Regex BranchPrefix();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex Backticked();

    private sealed record Category(string Title, List<string> Labels);
}
