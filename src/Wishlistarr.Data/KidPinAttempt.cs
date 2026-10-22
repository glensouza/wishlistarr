namespace Wishlistarr.Data;

public sealed class KidPinAttempt
{
    public int Id { get; set; }

    public int KidProfileId { get; set; }

    public KidProfile? KidProfile { get; set; }

    public string? DeviceHash { get; set; }

    public int? KidDeviceId { get; set; }

    public KidDevice? KidDevice { get; set; }

    public int FailedCount { get; set; }

    public DateTimeOffset LastFailedAt { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? LockedAt { get; set; }
}
