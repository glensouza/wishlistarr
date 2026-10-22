namespace Wishlistarr.Api.Kids;

public sealed class KidOptions
{
    public const string SectionName = "Kids";

    public bool RequirePinWhenLimitsDiffer { get; set; } = true;

    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan BackoffMax { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan FailureResetAfter { get; set; } = TimeSpan.FromHours(1);

    public int NotifyThreshold { get; set; } = 5;

    public int LockThreshold { get; set; } = 10;

    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan PairingCodeLifetime { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan DeviceCookieLifetime { get; set; } = TimeSpan.FromDays(365);

    public bool IsValid() =>
        this.BackoffBase > TimeSpan.Zero
        && this.BackoffMax >= this.BackoffBase
        && this.FailureResetAfter > TimeSpan.Zero
        && this.NotifyThreshold > 0
        && this.LockThreshold >= this.NotifyThreshold
        && this.SessionLifetime > TimeSpan.Zero
        && this.PairingCodeLifetime > TimeSpan.Zero
        && this.PairingCodeLifetime <= TimeSpan.FromHours(1)
        && this.DeviceCookieLifetime > TimeSpan.Zero
        && this.DeviceCookieLifetime <= TimeSpan.FromDays(400);

    public TimeSpan GetBackoff(int failedCount)
    {
        double factor = Math.Pow(2, Math.Min(Math.Max(failedCount - 1, 0), 30));
        double ticks = Math.Min(this.BackoffBase.Ticks * factor, this.BackoffMax.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }
}
