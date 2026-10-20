using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Jobs;

public sealed class JobHistoryCleanup(WishlistarrDbContext db, IOptions<JobOptions> options, TimeProvider time) : IBackgroundJob
{
    public const string JobName = "job-history-cleanup";

    public async Task RunAsync(CancellationToken ct)
    {
        DateTimeOffset cutoff = time.GetUtcNow() - options.Value.HistoryRetention;
        List<JobRun> finished = await db.JobRuns
            .AsNoTracking()
            .Where(run => run.Outcome != JobRunOutcome.Running)
            .ToListAsync(ct);
        List<int> expired = [.. finished.Where(run => run.StartedAt < cutoff).Select(run => run.Id)];
        await db.JobRuns.Where(run => expired.Contains(run.Id)).ExecuteDeleteAsync(ct);
    }
}
