namespace TimeLedger.Domain.Rules;

public static class SchedulePolicy
{
    /// <summary>Normal working minutes per workday: 8h on a 5-day week, 7h on a 6-day week.</summary>
    public static int NormalDailyMinutes(WorkSchedule schedule) => schedule switch
    {
        WorkSchedule.FiveDay => 8 * 60,
        WorkSchedule.SixDay => 7 * 60,
        _ => throw new ArgumentOutOfRangeException(nameof(schedule), schedule, null),
    };

    public static bool IsRestDay(WorkSchedule schedule, DayOfWeek day) => schedule switch
    {
        WorkSchedule.FiveDay => day is DayOfWeek.Saturday or DayOfWeek.Sunday,
        WorkSchedule.SixDay => day is DayOfWeek.Sunday,
        _ => throw new ArgumentOutOfRangeException(nameof(schedule), schedule, null),
    };

    public static DayKind Classify(WorkSchedule schedule, DateOnly date, IReadOnlySet<DateOnly> holidays)
    {
        if (holidays.Contains(date))
        {
            return DayKind.PublicHoliday;
        }

        return IsRestDay(schedule, date.DayOfWeek) ? DayKind.RestDay : DayKind.Workday;
    }

    /// <summary>Working days in [start, end] for this schedule, excluding rest days and public holidays.</summary>
    public static IEnumerable<DateOnly> WorkingDays(DateOnly start, DateOnly end, WorkSchedule schedule, IReadOnlySet<DateOnly> holidays)
    {
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (Classify(schedule, d, holidays) == DayKind.Workday)
            {
                yield return d;
            }
        }
    }
}
