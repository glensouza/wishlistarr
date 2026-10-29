namespace Wishlistarr.Api.Labels;

public interface IProfileLabelGate
{
    Task<IDisposable> EnterAsync(CancellationToken ct);
}

public sealed class ProfileLabelGate : IProfileLabelGate
{
    private readonly ItemLocks locks = new();

    // Profile saves never acquire item or backfill locks, so taking this gate before an undo's item lock cannot form a lock cycle.
    public Task<IDisposable> EnterAsync(CancellationToken ct) => this.locks.AcquireAsync("profile-labels", ct);
}
