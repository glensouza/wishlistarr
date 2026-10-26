using Microsoft.Extensions.Configuration;

namespace Wishlistarr.Core;

public sealed class DataDirectory
{
    public const string EnvironmentVariable = "WISHLISTARR_DATA_DIR";

    private DataDirectory(string root) => this.Root = root;

    public string Root { get; }

    public const string DatabaseFileName = "wishlistarr.db";

    public string DatabasePath => Path.Combine(this.Root, DatabaseFileName);

    public string KeysPath => Path.Combine(this.Root, "keys");

    public string LogsPath => Path.Combine(this.Root, "logs");

    public string BackupsPath => Path.Combine(this.Root, "backups");

    public string RestorePendingPath => Path.Combine(this.Root, "restore-pending");

    public string RestorePreviousPath => Path.Combine(this.Root, "restore-previous");

    public string RestoreJournalPath => Path.Combine(this.Root, "restore-journal.json");

    public static DataDirectory Resolve(IConfiguration configuration, string contentRoot)
    {
        string? configured = configuration[EnvironmentVariable];
        string root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(contentRoot, "data")
            : Path.GetFullPath(configured, contentRoot);
        return new DataDirectory(root);
    }
}
