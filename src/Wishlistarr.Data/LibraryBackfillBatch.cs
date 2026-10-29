using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Data;

public enum BackfillState
{
    Applying,
    Paused,
    Completed,
    Undoing,
    UndoPaused,
    Undone,
}

public enum BackfillItemState
{
    Pending,
    Applied,
    Preserved,
    Skipped,
    Undone,
}

public sealed class LibraryBackfillBatch
{
    public int Id { get; set; }

    public int KidProfileId { get; set; }

    public required string ServerId { get; set; }

    public required string Label { get; set; }

    public BackfillState State { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string? Error { get; set; }

    public List<LibraryBackfillItem> Items { get; set; } = [];
}

public sealed class LibraryBackfillItem
{
    public int Id { get; set; }

    public int BatchId { get; set; }

    public LibraryBackfillBatch? Batch { get; set; }

    public required string RatingKey { get; set; }

    public required string Guid { get; set; }

    public required string Title { get; set; }

    public MediaKind Kind { get; set; }

    public int? TmdbId { get; set; }

    public BackfillItemState State { get; set; }

    public bool OwnsLabel { get; set; }

    public string? Detail { get; set; }
}
