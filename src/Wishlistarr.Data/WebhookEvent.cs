namespace Wishlistarr.Data;

public sealed class WebhookEvent
{
    public const int MaxSourceLength = 16;
    public const int MaxEventTypeLength = 100;
    public const int KeyLength = 64;

    public long Id { get; set; }

    public required string Source { get; set; }

    public required string DeliveryKey { get; set; }

    public required string EventType { get; set; }

    public required string Payload { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }
}
