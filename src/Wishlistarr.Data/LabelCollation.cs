namespace Wishlistarr.Data;

// SQLite's NOCASE folds only the ASCII letters A to Z, so in-memory checks of ledger labels use the same rule instead of OrdinalIgnoreCase, which would also fold accented letters.
public static class LabelCollation
{
    public const string Name = "NOCASE";

    public static StringComparer Comparer { get; } = new NoCaseComparer();

    private static char Fold(char value) => value is >= 'A' and <= 'Z' ? (char)(value + ('a' - 'A')) : value;

    private sealed class NoCaseComparer : StringComparer
    {
        public override int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            int length = Math.Min(x.Length, y.Length);
            for (int index = 0; index < length; index++)
            {
                int difference = Fold(x[index]) - Fold(y[index]);
                if (difference != 0)
                {
                    return difference;
                }
            }

            return x.Length - y.Length;
        }

        public override bool Equals(string? x, string? y) => this.Compare(x, y) == 0;

        public override int GetHashCode(string obj)
        {
            HashCode hash = default;
            foreach (char value in obj)
            {
                hash.Add(Fold(value));
            }

            return hash.ToHashCode();
        }
    }
}
