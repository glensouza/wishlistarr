namespace Wishlistarr.Data;

public sealed class OwnedLabel
{
    public int Id { get; set; }

    public required string ServerId { get; set; }

    public required string Guid { get; set; }

    public required string RatingKey { get; set; }

    public required string Label { get; set; }

    public int KidProfileId { get; set; }

    public KidProfile? KidProfile { get; set; }

    public required string Source { get; set; }

    public DateTimeOffset AppliedAt { get; set; }
}
