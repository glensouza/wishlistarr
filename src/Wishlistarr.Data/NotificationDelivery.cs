namespace Wishlistarr.Data;

public sealed class NotificationDelivery
{
    public int Id { get; set; }

    public int NotificationSubscriptionId { get; set; }

    public NotificationSubscription? Subscription { get; set; }

    public int? MediaRequestEventId { get; set; }

    public int? MediaRequestId { get; set; }

    public required string Event { get; set; }

    public required string Message { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? LastError { get; set; }
}
