namespace Wishlistarr.Core.Requests;

public static class AllowanceWeek
{
    public static DateTimeOffset StartOf(DateTimeOffset instant, TimeZoneInfo zone)
    {
        DateTime local = TimeZoneInfo.ConvertTime(instant, zone).DateTime;
        return ToInstant(local.Date.AddDays(-DaysSinceMonday(local)), zone);
    }

    public static DateTimeOffset NextStart(DateTimeOffset instant, TimeZoneInfo zone)
    {
        DateTime local = TimeZoneInfo.ConvertTime(instant, zone).DateTime;
        return ToInstant(local.Date.AddDays(7 - DaysSinceMonday(local)), zone);
    }

    private static int DaysSinceMonday(DateTime local) => ((int)local.DayOfWeek + 6) % 7;

    private static DateTimeOffset ToInstant(DateTime midnight, TimeZoneInfo zone)
    {
        DateTime local = DateTime.SpecifyKind(midnight, DateTimeKind.Unspecified);

        // A clock change at midnight can skip it, so the week starts at the first local time that exists.
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        TimeSpan offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
