namespace Wishlistarr.Api.Requests;

public sealed class ApprovalOptions
{
    public const string SectionName = "Approval";

    public TimeSpan LabelTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan BulkTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public static TimeSpan MaxLabelTimeout { get; } = TimeSpan.FromMinutes(10);

    public static TimeSpan MaxBulkTimeout { get; } = TimeSpan.FromHours(24);

    public bool IsValid() =>
        this.LabelTimeout > TimeSpan.Zero
        && this.LabelTimeout <= MaxLabelTimeout
        && this.BulkTimeout >= this.LabelTimeout
        && this.BulkTimeout <= MaxBulkTimeout;
}
