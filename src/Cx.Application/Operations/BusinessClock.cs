using System.Globalization;

namespace Cx.Application.Operations;

public static class BusinessClock
{
    public static void Validate(BusinessCalendar calendar)
    {
        if (calendar is null) throw new OperationsException("Specify the work calendar.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZone); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        { throw new OperationsException("The time zone is not valid."); }
        if (calendar.WorkingDays is not { Length: >= 1 and <= 7 } || calendar.WorkingDays.Distinct().Count() != calendar.WorkingDays.Length || calendar.WorkingDays.Any(day => day is < 0 or > 6))
            throw new OperationsException("Specify working days.");
        if (!TimeOnly.TryParseExact(calendar.StartsAt, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !TimeOnly.TryParseExact(calendar.EndsAt, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) || start >= end)
            throw new OperationsException("The end time must be after the start time.");
        if (calendar.Holidays is null || calendar.Holidays.Length > 1000 || calendar.Holidays.Any(day => !DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
            throw new OperationsException("The holiday date is not valid.");
    }

    private static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> Windows(DateTimeOffset from, DateTimeOffset to, BusinessCalendar calendar)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZone);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, zone).DateTime);
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to, zone).DateTime);
        var start = TimeOnly.ParseExact(calendar.StartsAt, "HH:mm", CultureInfo.InvariantCulture);
        var end = TimeOnly.ParseExact(calendar.EndsAt, "HH:mm", CultureInfo.InvariantCulture);
        var holidays = calendar.Holidays.ToHashSet(StringComparer.Ordinal);
        for (; day <= last; day = day.AddDays(1))
        {
            if (!calendar.WorkingDays.Contains((int)day.DayOfWeek) || holidays.Contains(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))) continue;
            DateTimeOffset Utc(TimeOnly time) => new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(time, DateTimeKind.Unspecified), zone));
            yield return (Utc(start), Utc(end));
        }
    }

    public static DateTimeOffset Add(DateTimeOffset start, double minutes, BusinessCalendar calendar)
    {
        Validate(calendar);
        if (!double.IsFinite(minutes) || minutes < 0 || minutes > 525600) throw new OperationsException("The deadline is not valid.");
        if (minutes == 0) return start;
        foreach (var window in Windows(start, start.AddYears(10), calendar))
        {
            var begin = start > window.Start ? start : window.Start;
            if (begin >= window.End) continue;
            var available = (window.End - begin).TotalMinutes;
            if (minutes <= available) return begin.AddMinutes(minutes);
            minutes -= available;
        }
        throw new OperationsException("The work calendar does not have enough capacity for this deadline.");
    }

    public static double Between(DateTimeOffset from, DateTimeOffset to, BusinessCalendar calendar)
    {
        if (to <= from) return 0;
        Validate(calendar);
        return Windows(from, to, calendar).Sum(window => Math.Max(0, ((to < window.End ? to : window.End) - (from > window.Start ? from : window.Start)).TotalMinutes));
    }
}
