using Wishlistarr.Api.Jobs;

namespace Wishlistarr.Api.Backup;

public sealed class DatabaseBackupJob(BackupService backups) : IBackgroundJob
{
    public const string JobName = "database-backup";

    public Task RunAsync(CancellationToken ct) => backups.CreateBackupAsync(ct);
}
