namespace Wishlistarr.Data;

public sealed class WebPushSubscription
{
    public int Id { get; set; }

    public int? ParentAccountId { get; set; }

    public int? KidDeviceId { get; set; }

    public required string Endpoint { get; set; }

    public required string P256dh { get; set; }

    public required string Auth { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
