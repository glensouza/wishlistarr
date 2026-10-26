namespace Wishlistarr.Api.Requests;

public sealed class ApprovalOptions
{
    public const string SectionName = "Approval";

    public TimeSpan LabelTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan BulkTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan StuckAfter { get; set; } = TimeSpan.FromMinutes(10);

    public static TimeSpan MaxLabelTimeout { get; } = TimeSpan.FromMinutes(10);

    public static TimeSpan MaxBulkTimeout { get; } = TimeSpan.FromHours(24);

    // A labeler can spend one LabelTimeout writing and another rolling back, so it may still be running until twice that has passed.
    public bool IsValid() =>
        this.LabelTimeout > TimeSpan.Zero
        && this.LabelTimeout <= MaxLabelTimeout
        && this.BulkTimeout >= this.LabelTimeout
        && this.BulkTimeout <= MaxBulkTimeout
        && this.StuckAfter >= this.LabelTimeout * 2;
}
