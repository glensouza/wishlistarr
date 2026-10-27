using Microsoft.Data.Sqlite;
using Wishlistarr.Data;

namespace Wishlistarr.Tests.Labels;

public sealed class LabelCollationTests
{
    public static TheoryData<string, string> Pairs => new()
    {
        { "Ada", "ada" },
        { "ADA", "aDa" },
        { "Ada", "Ben" },
        { "Ada", "Ada " },
        { "\u00C9lodie", "\u00E9lodie" },
        { "\u00C9lodie", "\u00C9LODIE" },
        { "Stra\u00DFe", "STRASSE" },
        { "\u0130", "i" },
        { "\u212A", "k" },
        { "\u01C5", "\u01C6" },
        { "[", "{" },
        { "@", "`" },
    };

    [Theory]
    [MemberData(nameof(Pairs))]
    public async Task The_in_memory_comparer_agrees_with_sqlite_nocase(string left, string right)
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT $left = $right COLLATE {LabelCollation.Name}";
        command.Parameters.AddWithValue("$left", left);
        command.Parameters.AddWithValue("$right", right);

        bool sqlite = Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) == 1;

        Assert.Equal(sqlite, LabelCollation.Comparer.Equals(left, right));
        if (sqlite)
        {
            Assert.Equal(LabelCollation.Comparer.GetHashCode(left), LabelCollation.Comparer.GetHashCode(right));
        }
    }
}
