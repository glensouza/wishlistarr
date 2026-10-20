namespace Wishlistarr.Data;

public enum JobRunOutcome
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public enum JobRunTrigger
{
    Schedule,
    Manual,
}

public sealed class JobRun
{
    public const int MaxErrorLength = 1000;

    public int Id { get; set; }

    public required string JobName { get; set; }

    public JobRunTrigger Trigger { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public JobRunOutcome Outcome { get; set; }

    public string? Error { get; set; }
}
