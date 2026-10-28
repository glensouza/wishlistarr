namespace Wishlistarr.Data;

public sealed class WebPushDelivery
{
    public int Id { get; set; }

    public int WebPushSubscriptionId { get; set; }

    public WebPushSubscription? Subscription { get; set; }

    public int? MediaRequestId { get; set; }

    public int? KidProfileId { get; set; }

    public required string Event { get; set; }

    /// <summary>
    /// Plain text for events that have no request to read later, such as PIN failures.
    /// Request notices leave this empty and build the text at send time.
    /// </summary>
    public string? Message { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? LastError { get; set; }
}
