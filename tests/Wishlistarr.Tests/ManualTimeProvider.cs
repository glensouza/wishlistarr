namespace Wishlistarr.Tests;

public sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => this.now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer = new(callback, state, this.now + dueTime);
        lock (this.timers)
        {
            this.timers.Add(timer);
        }

        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (this.timers)
        {
            this.now += by;
            due = [.. this.timers.Where(timer => timer.Due <= this.now)];
        }

        foreach (ManualTimer timer in due)
        {
            timer.Fire();
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, DateTimeOffset due) : ITimer
    {
        private bool done;

        public DateTimeOffset Due => due;

        public void Fire()
        {
            if (!this.done)
            {
                this.done = true;
                callback(state);
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => !this.done;

        public void Dispose() => this.done = true;

        public ValueTask DisposeAsync()
        {
            this.done = true;
            return ValueTask.CompletedTask;
        }
    }
}
