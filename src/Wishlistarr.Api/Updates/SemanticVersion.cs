using System.Globalization;
using System.Text.RegularExpressions;

namespace Wishlistarr.Api.Updates;

public sealed partial class SemanticVersion : IComparable<SemanticVersion>
{
    // The same pattern as the tag check in .github/workflows/release.yml, without the v prefix.
    // The end anchor is \z because $ also matches before a trailing newline.
    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    private SemanticVersion(int major, int minor, int patch, IReadOnlyList<string> prerelease)
    {
        this.Major = major;
        this.Minor = minor;
        this.Patch = patch;
        this.Prerelease = prerelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public IReadOnlyList<string> Prerelease { get; }

    public bool IsPrerelease => this.Prerelease.Count > 0;

    public static bool TryParse(string? text, out SemanticVersion? version)
    {
        version = null;
        if (text is null || !VersionPattern().IsMatch(text))
        {
            return false;
        }

        string[] coreAndPre = text.Split('-', 2);
        string[] numbers = coreAndPre[0].Split('.');
        if (!int.TryParse(numbers[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major)
            || !int.TryParse(numbers[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minor)
            || !int.TryParse(numbers[2], NumberStyles.None, CultureInfo.InvariantCulture, out int patch))
        {
            return false;
        }

        IReadOnlyList<string> prerelease = coreAndPre.Length == 2 ? coreAndPre[1].Split('.') : [];
        version = new SemanticVersion(major, minor, patch, prerelease);
        return true;
    }

    public static bool TryParseTag(string? tag, out SemanticVersion? version)
    {
        version = null;
        return tag is not null && tag.StartsWith('v') && TryParse(tag[1..], out version);
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int major = this.Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        int minor = this.Minor.CompareTo(other.Minor);
        if (minor != 0)
        {
            return minor;
        }

        int patch = this.Patch.CompareTo(other.Patch);
        if (patch != 0)
        {
            return patch;
        }

        if (this.IsPrerelease != other.IsPrerelease)
        {
            return this.IsPrerelease ? -1 : 1;
        }

        int shared = Math.Min(this.Prerelease.Count, other.Prerelease.Count);
        for (int index = 0; index < shared; index++)
        {
            int compared = CompareIdentifier(this.Prerelease[index], other.Prerelease[index]);
            if (compared != 0)
            {
                return compared;
            }
        }

        return this.Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    public override string ToString()
    {
        string core = $"{this.Major}.{this.Minor}.{this.Patch}";
        return this.IsPrerelease ? core + "-" + string.Join('.', this.Prerelease) : core;
    }

    private static int CompareIdentifier(string left, string right)
    {
        bool leftNumber = IsNumeric(left);
        bool rightNumber = IsNumeric(right);
        if (leftNumber && rightNumber)
        {
            return CompareNumeric(left, right);
        }

        if (leftNumber)
        {
            return -1;
        }

        if (rightNumber)
        {
            return 1;
        }

        return string.CompareOrdinal(left, right);
    }

    private static bool IsNumeric(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);

    private static int CompareNumeric(string left, string right)
    {
        string a = left.TrimStart('0');
        string b = right.TrimStart('0');
        if (a.Length == 0)
        {
            a = "0";
        }

        if (b.Length == 0)
        {
            b = "0";
        }

        int length = a.Length.CompareTo(b.Length);
        return length != 0 ? length : string.CompareOrdinal(a, b);
    }
}
