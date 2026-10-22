using Wishlistarr.Api.Jobs;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Api.Library;

public sealed class LibraryFullRefreshJob(LibraryRefresh refresh, ILogger<LibraryFullRefreshJob> logger) : IBackgroundJob
{
    public const string JobName = "library-full-refresh";

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await refresh.FullAsync(ct);
        }
        catch (MediaServerNotConfiguredException exception)
        {
            logger.LogDebug("Library refresh skipped: {Reason}", exception.Message);
        }
    }
}

public sealed class LibraryIncrementalRefreshJob(LibraryRefresh refresh, ILogger<LibraryIncrementalRefreshJob> logger) : IBackgroundJob
{
    public const string JobName = "library-incremental-refresh";

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await refresh.IncrementalAsync(ct);
        }
        catch (MediaServerNotConfiguredException exception)
        {
            logger.LogDebug("Library refresh skipped: {Reason}", exception.Message);
        }
    }
}
