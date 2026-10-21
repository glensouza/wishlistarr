namespace Wishlistarr.Api.Webhooks;

public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";

    public int MaxPayloadBytes { get; set; } = 2 * 1024 * 1024;

    public TimeSpan DeduplicationWindow { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);

    public bool IsValid() =>
        this.MaxPayloadBytes > 0
        && this.DeduplicationWindow > TimeSpan.Zero
        && this.Retention >= this.DeduplicationWindow;
}
