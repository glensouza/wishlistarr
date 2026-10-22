namespace Wishlistarr.Data;

public sealed class KidSession
{
    public required string Id { get; set; }

    public int KidProfileId { get; set; }

    public KidProfile? KidProfile { get; set; }

    public int? KidDeviceId { get; set; }

    public KidDevice? KidDevice { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public string? UserAgent { get; set; }

    public string? RemoteAddress { get; set; }
}
