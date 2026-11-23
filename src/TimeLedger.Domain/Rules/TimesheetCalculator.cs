using TimeLedger.Domain.Entities;

namespace TimeLedger.Domain.Rules;

public sealed record DailyTimesheet(
    DateOnly Date,
    DayKind Kind,
    int WorkedMinutes,
    int RegularMinutes,
    int OvertimeMinutes,
    decimal OvertimePay,
    LeaveType? Leave,
    IReadOnlyList<string> Anomalies);

public sealed record TimesheetSummary(
    int WorkedMinutes,
    int RegularMinutes,
    int OvertimeMinutes,
    decimal OvertimePay,
    int DaysWorked,
    decimal AnnualLeaveDays,
    decimal SickLeaveDays,
    decimal UnpaidLeaveDays,
    int AnomalyCount);

public sealed record TimesheetInput(
    WorkSchedule Schedule,
    decimal MonthlySalary,
    TimeZoneInfo TimeZone,
    DateOnly Start,
    DateOnly End,
    IReadOnlyList<ClockEvent> Events,
    IReadOnlySet<DateOnly> Holidays,
    IReadOnlyDictionary<DateOnly, LeaveType> LeaveDays,
    DateOnly AsOf);

/// <summary>
/// Turns raw punches into daily timesheets.
/// <list type="bullet">
/// <item>Punches are paired in time order: in → out is one shift.</item>
/// <item>A shift belongs to the local calendar day of its clock-in, so a night shift is not split across days.</item>
/// <item>Unpaired punches and suspicious shifts are flagged as anomalies for a supervisor, never silently dropped or guessed.</item>
/// <item>Breaks are expected to be punched out; no automatic deduction is applied.</item>
/// </list>
/// </summary>
public static class TimesheetCalculator
{
    public const int SuspiciousShiftMinutes = 16 * 60;

    public static IReadOnlyList<DailyTimesheet> Calculate(TimesheetInput input)
    {
        if (input.End < input.Start)
        {
            throw new ArgumentException("End must be on or after Start.", nameof(input));
        }

        var minutesByDay = new Dictionary<DateOnly, int>();
        var anomaliesByDay = new Dictionary<DateOnly, List<string>>();

        void Flag(DateOnly day, string message)
        {
            if (!anomaliesByDay.TryGetValue(day, out var list))
            {
                anomaliesByDay[day] = list = [];
            }

            list.Add(message);
        }

        DateOnly LocalDate(DateTimeOffset utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, input.TimeZone).DateTime);

        ClockEvent? open = null;
        foreach (var evt in input.Events.OrderBy(e => e.OccurredAt))
        {
            if (evt.Type == ClockEventType.ClockIn)
            {
                if (open is not null)
                {
                    Flag(LocalDate(open.OccurredAt), "Missing clock-out (followed by another clock-in).");
                }

                open = evt;
                continue;
            }

            if (open is null)
            {
                Flag(LocalDate(evt.OccurredAt), "Clock-out without a matching clock-in.");
                continue;
            }

            var day = LocalDate(open.OccurredAt);
            var minutes = (int)Math.Floor((evt.OccurredAt - open.OccurredAt).TotalMinutes);
            minutesByDay[day] = minutesByDay.GetValueOrDefault(day) + minutes;

            if (minutes > SuspiciousShiftMinutes)
            {
                Flag(day, $"Shift of {minutes / 60}h{minutes % 60:00}m is unusually long; check for a missed clock-out.");
            }

            open = null;
        }

        if (open is not null)
        {
            Flag(LocalDate(open.OccurredAt), "Missing clock-out (still clocked in).");
        }

        var result = new List<DailyTimesheet>();
        // Start from that week's Monday so the weekly limit also works for a week split across two periods.
        for (var date = WeekStart(input.Start); date <= input.End; date = date.AddDays(1))
        {
            var kind = SchedulePolicy.Classify(input.Schedule, date, input.Holidays);
            var worked = minutesByDay.GetValueOrDefault(date);
            LeaveType? leave = input.LeaveDays.TryGetValue(date, out var lt) ? lt : null;
            var anomalies = anomaliesByDay.TryGetValue(date, out var flagged) ? new List<string>(flagged) : [];

            if (leave is not null && worked > 0)
            {
                anomalies.Add($"Worked {worked} min on a day of approved {leave} leave.");
            }

            if (kind == DayKind.Workday && worked == 0 && leave is null && date < input.AsOf && anomalies.Count == 0)
            {
                anomalies.Add("No attendance and no approved leave.");
            }

            var ot = OvertimeCalculator.Calculate(worked, kind, input.Schedule, input.MonthlySalary);
            anomalies.AddRange(ot.Warnings);

            var hasActivity = worked > 0 || leave is not null || anomalies.Count > 0;
            if (hasActivity)
            {
                result.Add(new DailyTimesheet(date, kind, worked, ot.RegularMinutes, ot.OvertimeMinutes, ot.OvertimePay, leave, anomalies));
            }
        }

        FlagWeeklyOvertime(result);
        return result.Where(d => d.Date >= input.Start).ToList();
    }

    /// <summary>The Monday of the ISO week containing <paramref name="date"/>.</summary>
    public static DateOnly WeekStart(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    public static TimesheetSummary Summarize(IEnumerable<DailyTimesheet> days)
    {
        var list = days as IReadOnlyCollection<DailyTimesheet> ?? days.ToList();
        return new TimesheetSummary(
            WorkedMinutes: list.Sum(d => d.WorkedMinutes),
            RegularMinutes: list.Sum(d => d.RegularMinutes),
            OvertimeMinutes: list.Sum(d => d.OvertimeMinutes),
            OvertimePay: list.Sum(d => d.OvertimePay),
            DaysWorked: list.Count(d => d.WorkedMinutes > 0),
            AnnualLeaveDays: list.Count(d => d.Leave == LeaveType.Annual),
            SickLeaveDays: list.Count(d => d.Leave == LeaveType.Sick),
            UnpaidLeaveDays: list.Count(d => d.Leave == LeaveType.Unpaid),
            AnomalyCount: list.Sum(d => d.Anomalies.Count));
    }

    /// <summary>Flags the day on which an ISO week's overtime first passes the 18h weekly limit. Work on rest days and public
    /// holidays does not count towards it (PP 35/2021, Pasal 26 ayat 2).</summary>
    private static void FlagWeeklyOvertime(List<DailyTimesheet> days)
    {
        foreach (var week in days.Where(d => d.Kind == DayKind.Workday).GroupBy(d => (System.Globalization.ISOWeek.GetYear(d.Date.ToDateTime(TimeOnly.MinValue)),
                                               System.Globalization.ISOWeek.GetWeekOfYear(d.Date.ToDateTime(TimeOnly.MinValue)))))
        {
            var running = 0;
            foreach (var day in week.OrderBy(d => d.Date))
            {
                var before = running;
                running += day.OvertimeMinutes;
                if (before <= OvertimeCalculator.MaxWeeklyOvertimeMinutes && running > OvertimeCalculator.MaxWeeklyOvertimeMinutes)
                {
                    ((List<string>)day.Anomalies).Add($"Weekly overtime reached {running / 60}h{running % 60:00}m, above the 18h limit.");
                }
            }
        }
    }
}
