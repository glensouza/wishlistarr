namespace Wishlistarr.Api.Requests;

public sealed class ApprovalOptions
{
    public const string SectionName = "Approval";

    public TimeSpan LabelTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan BulkTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan StuckAfter { get; set; } = TimeSpan.FromMinutes(10);

    public bool IsValid() =>
        this.LabelTimeout > TimeSpan.Zero
        && this.BulkTimeout >= this.LabelTimeout
        && this.StuckAfter > this.LabelTimeout;
}
