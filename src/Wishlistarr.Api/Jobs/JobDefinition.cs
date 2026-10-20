namespace Wishlistarr.Api.Jobs;

public interface IBackgroundJob
{
    Task RunAsync(CancellationToken ct);
}

public sealed record JobDefinition(string Name, string Description, TimeSpan? DefaultInterval, Type JobType);
