using System.Globalization;
using System.Text.RegularExpressions;

namespace Wishlistarr.Api.Backup;

public static partial class BackupNames
{
    public const string DatabaseEntryName = "wishlistarr.db";

    public const string KeysEntryPrefix = "keys/";

    public const string ReadyFileName = "ready";

    public static bool IsBackupFileName(string name) => BackupFile().IsMatch(name);

    public static bool IsKeyFileName(string name)
    {
        if (name.Length is < 5 or > 120 || !name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (char character in name.AsSpan(0, name.Length - 4))
        {
            if (character is not ('-' or '_') && !char.IsAsciiLetterOrDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    public static DateTimeOffset? CreatedAt(string name)
    {
        if (!IsBackupFileName(name))
        {
            return null;
        }

        string stamp = name.Substring("wishlistarr-".Length, 19);
        return DateTimeOffset.TryParseExact(
            stamp,
            "yyyyMMdd'T'HHmmssfff'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out DateTimeOffset created)
            ? created
            : null;
    }

    public static string NextFileName(DateTimeOffset createdAt, int sequence) =>
        "wishlistarr-"
        + createdAt.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture)
        + "-"
        + sequence.ToString("D6", CultureInfo.InvariantCulture)
        + ".zip";

    [GeneratedRegex(@"^wishlistarr-\d{8}T\d{9}Z-\d{6}\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex BackupFile();
}
