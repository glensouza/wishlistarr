namespace Wishlistarr.Data;

public sealed class KidProfile
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public required string Avatar { get; set; }

    public string? PinHash { get; set; }

    public int? MovieAgeLimit { get; set; }

    public int? TvAgeLimit { get; set; }

    public required string PlexLabel { get; set; }

    public string? PlexUserId { get; set; }

    public int? WeeklyAllowance { get; set; }

    public int FailedPinCount { get; set; }

    public DateTimeOffset? LastFailedPinAt { get; set; }

    public DateTimeOffset? LockedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }

    public bool RemoveOwnedLabelsOnArchive { get; set; }

    public List<KidSession> Sessions { get; } = [];

    public List<KidPinAttempt> PinAttempts { get; } = [];
}
