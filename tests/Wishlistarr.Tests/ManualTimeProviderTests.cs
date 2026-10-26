namespace Wishlistarr.Tests;

public sealed class ManualTimeProviderTests
{
    [Fact]
    public void Changing_a_timer_moves_its_due_time()
    {
        ManualTimeProvider time = new();
        int fired = 0;
        using ITimer timer = time.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.True(timer.Change(TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan));
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(0, fired);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void A_fired_timer_can_be_rescheduled_and_a_disposed_one_cannot()
    {
        ManualTimeProvider time = new();
        int fired = 0;
        ITimer timer = time.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, fired);
        Assert.True(timer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(2, fired);

        Assert.True(timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan));
        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(2, fired);

        timer.Dispose();
        Assert.False(timer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, fired);
    }
}
