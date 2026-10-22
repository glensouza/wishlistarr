namespace Wishlistarr.Data;

public sealed class KidDevice
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public string? TokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? PairedAt { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }

    public List<KidDeviceProfile> Profiles { get; } = [];
}
