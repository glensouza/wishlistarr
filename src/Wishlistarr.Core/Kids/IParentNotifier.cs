namespace Wishlistarr.Core.Kids;

public interface IParentNotifier
{
    Task NotifyAsync(ParentNotification notification, CancellationToken ct = default);
}

public sealed record ParentNotification(string Kind, string Message);

public static class ParentNotificationKinds
{
    public const string PinFailures = "kid.pin.failures";
    public const string ProfileLocked = "kid.profile.locked";
}
