namespace Wishlistarr.Data;

public sealed class KidReadyNotice
{
    public int Id { get; set; }

    public int KidProfileId { get; set; }

    public int MediaRequestId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? DismissedAt { get; set; }
}
