using System.Globalization;
using Wishlistarr.Core.Requests;

namespace Wishlistarr.Tests.Requests;

public sealed class AllowanceWeekTests
{
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    [Theory]
    [InlineData("2027-01-04T00:00:00-06:00", "2027-01-04T00:00:00-06:00")]
    [InlineData("2027-01-06T12:30:00-06:00", "2027-01-04T00:00:00-06:00")]
    [InlineData("2027-01-10T23:59:59-06:00", "2027-01-04T00:00:00-06:00")]
    [InlineData("2027-01-11T05:59:59+00:00", "2027-01-04T00:00:00-06:00")]
    [InlineData("2027-01-11T06:00:00+00:00", "2027-01-11T00:00:00-06:00")]
    public void Weeks_start_at_monday_midnight_local(string instant, string expected)
    {
        DateTimeOffset start = AllowanceWeek.StartOf(DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture), Chicago);

        Assert.Equal(DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture), start);
        Assert.Equal(TimeSpan.Zero, start.Offset);
    }

    [Fact]
    public void Next_start_is_the_following_monday()
    {
        DateTimeOffset now = new(2027, 1, 6, 12, 0, 0, TimeSpan.FromHours(-6));

        Assert.Equal(new DateTimeOffset(2027, 1, 11, 0, 0, 0, TimeSpan.FromHours(-6)), AllowanceWeek.NextStart(now, Chicago));
    }

    [Fact]
    public void Weeks_that_cross_a_clock_change_are_shorter_or_longer()
    {
        DateTimeOffset spring = new(2027, 3, 17, 12, 0, 0, TimeSpan.FromHours(-5));
        DateTimeOffset fall = new(2027, 11, 10, 12, 0, 0, TimeSpan.FromHours(-6));

        Assert.Equal(TimeSpan.FromHours(167), AllowanceWeek.StartOf(spring, Chicago) - AllowanceWeek.StartOf(spring.AddDays(-7), Chicago));
        Assert.Equal(TimeSpan.FromHours(169), AllowanceWeek.StartOf(fall, Chicago) - AllowanceWeek.StartOf(fall.AddDays(-7), Chicago));
    }

    [Fact]
    public void Skipped_monday_midnight_starts_the_week_at_the_first_valid_time()
    {
        TimeZoneInfo zone = MondayMidnightGap();
        DateTimeOffset wednesday = new(2027, 3, 10, 12, 0, 0, TimeSpan.FromHours(1));

        DateTimeOffset start = AllowanceWeek.StartOf(wednesday, zone);

        Assert.Equal(new DateTimeOffset(2027, 3, 8, 1, 0, 0, TimeSpan.FromHours(1)), start);
        Assert.Equal(new DateTimeOffset(2027, 3, 8, 0, 0, 0, TimeSpan.Zero), start);
    }

    [Fact]
    public void Repeated_monday_midnight_starts_the_week_at_the_earlier_one()
    {
        TimeZoneInfo zone = MondayMidnightRepeat();
        DateTimeOffset wednesday = new(2027, 10, 13, 12, 0, 0, TimeSpan.Zero);

        DateTimeOffset start = AllowanceWeek.StartOf(wednesday, zone);

        Assert.Equal(new DateTimeOffset(2027, 10, 11, 0, 0, 0, TimeSpan.FromHours(1)), start);
    }

    private static TimeZoneInfo MondayMidnightGap() => CustomZone(
        TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 2, DayOfWeek.Monday),
        TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 4, DayOfWeek.Sunday));

    private static TimeZoneInfo MondayMidnightRepeat() => CustomZone(
        TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 4, DayOfWeek.Sunday),
        TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 1, 0, 0), 10, 2, DayOfWeek.Monday));

    private static TimeZoneInfo CustomZone(TimeZoneInfo.TransitionTime start, TimeZoneInfo.TransitionTime end)
    {
        TimeZoneInfo.AdjustmentRule rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date,
            DateTime.MaxValue.Date,
            TimeSpan.FromHours(1),
            start,
            end);
        return TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.Zero, "Test", "Test", "Test Summer", [rule]);
    }
}
