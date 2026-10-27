namespace Wishlistarr.Data;

public sealed class NotificationApproval
{
    public Guid Id { get; set; }

    public int ParentAccountId { get; set; }

    public int MediaRequestId { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? UsedAt { get; set; }
}
