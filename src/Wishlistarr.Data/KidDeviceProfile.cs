namespace Wishlistarr.Data;

public sealed class KidDeviceProfile
{
    public int KidDeviceId { get; set; }

    public KidDevice? KidDevice { get; set; }

    public int KidProfileId { get; set; }

    public KidProfile? KidProfile { get; set; }
}
