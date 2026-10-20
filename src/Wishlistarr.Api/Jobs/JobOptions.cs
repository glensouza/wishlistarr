namespace Wishlistarr.Api.Jobs;

public sealed class JobOptions
{
    public const string SectionName = "Jobs";

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan HistoryRetention { get; set; } = TimeSpan.FromDays(30);

    public Dictionary<string, JobScheduleOptions> Schedules { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsValid() =>
        this.PollInterval > TimeSpan.Zero
        && this.HistoryRetention > TimeSpan.Zero
        && this.Schedules.Values.All(schedule => schedule.Interval is null || schedule.Interval > TimeSpan.Zero);

    public JobSchedule Resolve(JobDefinition definition)
    {
        this.Schedules.TryGetValue(definition.Name, out JobScheduleOptions? configured);
        bool enabled = configured?.Enabled ?? true;
        TimeSpan? interval = enabled ? configured?.Interval ?? definition.DefaultInterval : null;
        return new JobSchedule(enabled, interval);
    }
}

public sealed class JobScheduleOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan? Interval { get; set; }
}

public sealed record JobSchedule(bool Enabled, TimeSpan? Interval);
