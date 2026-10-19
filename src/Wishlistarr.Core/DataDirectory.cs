using Microsoft.Extensions.Configuration;

namespace Wishlistarr.Core;

public sealed class DataDirectory
{
    public const string EnvironmentVariable = "WISHLISTARR_DATA_DIR";

    private DataDirectory(string root) => this.Root = root;

    public string Root { get; }

    public string DatabasePath => Path.Combine(this.Root, "wishlistarr.db");

    public string KeysPath => Path.Combine(this.Root, "keys");

    public string LogsPath => Path.Combine(this.Root, "logs");

    public static DataDirectory Resolve(IConfiguration configuration, string contentRoot)
    {
        string? configured = configuration[EnvironmentVariable];
        string root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(contentRoot, "data")
            : Path.GetFullPath(configured, contentRoot);
        return new DataDirectory(root);
    }
}
