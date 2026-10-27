namespace Wishlistarr.Data;

public sealed class LabelGuardianReport
{
    public int Id { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset FinishedAt { get; set; }

    public bool DryRun { get; set; }

    public required string ServerId { get; set; }

    public int LedgerChecked { get; set; }

    public bool ExtrasChecked { get; set; }

    public List<LabelGuardianEntry> Entries { get; set; } = [];
}

public enum LabelDriftKind
{
    Missing,
    Extra,
    Moved,
    Unresolved,
}

public enum LabelDriftAction
{
    None,
    Repaired,
    WouldRepair,
    Updated,
    WouldUpdate,
    AlreadyFixed,
    Skipped,
    Interrupted,
    Failed,
}

public sealed class LabelGuardianEntry
{
    public const int MaxTitleLength = 500;
    public const int MaxDetailLength = 300;

    public int Id { get; set; }

    public int ReportId { get; set; }

    public LabelGuardianReport? Report { get; set; }

    public int KidProfileId { get; set; }

    public LabelDriftKind Kind { get; set; }

    public LabelDriftAction Action { get; set; }

    public required string Label { get; set; }

    public required string Guid { get; set; }

    public string? RatingKey { get; set; }

    public string? PreviousRatingKey { get; set; }

    public string? PreviousGuid { get; set; }

    public string? Title { get; set; }

    public string? Detail { get; set; }
}
