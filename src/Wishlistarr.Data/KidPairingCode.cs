namespace Wishlistarr.Data;

public sealed class KidPairingCode
{
    public int Id { get; set; }

    public int KidDeviceId { get; set; }

    public KidDevice? KidDevice { get; set; }

    public required string CodeHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}
