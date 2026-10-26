namespace Wishlistarr.Tests;

public sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => this.now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer;
        lock (this.timers)
        {
            timer = new ManualTimer(this, callback, state, dueTime);
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

    private DateTimeOffset DueAt(TimeSpan dueTime) =>
        dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : this.now + dueTime;

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider owner;
        private readonly TimerCallback callback;
        private readonly object? state;
        private DateTimeOffset due;
        private bool done;
        private bool disposed;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime)
        {
            this.owner = owner;
            this.callback = callback;
            this.state = state;
            this.due = owner.DueAt(dueTime);
        }

        public DateTimeOffset Due
        {
            get
            {
                lock (this.owner.timers)
                {
                    return this.due;
                }
            }
        }

        public void Fire()
        {
            lock (this.owner.timers)
            {
                if (this.done)
                {
                    return;
                }

                this.done = true;
            }

            this.callback(this.state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (this.owner.timers)
            {
                if (this.disposed)
                {
                    return false;
                }

                this.due = this.owner.DueAt(dueTime);
                this.done = false;
                return true;
            }
        }

        public void Dispose()
        {
            lock (this.owner.timers)
            {
                this.disposed = true;
                this.done = true;
            }
        }

        public ValueTask DisposeAsync()
        {
            this.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
