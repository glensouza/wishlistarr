namespace Wishlistarr.Api.Backup;

public sealed class BackupOptions
{
    public const string SectionName = "Backups";

    public const int DefaultRetention = 7;

    public const long HardMaxUploadBytes = 512L * 1024 * 1024;

    public int Retention { get; set; } = DefaultRetention;

    public long MaxUploadBytes { get; set; } = HardMaxUploadBytes;

    public bool IsValid() =>
        this.Retention is >= 1 and <= 100
        && this.MaxUploadBytes is >= 1 and <= HardMaxUploadBytes;
}
