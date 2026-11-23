using TimeLedger.Domain;
using TimeLedger.Domain.Entities;
using TimeLedger.Domain.Rules;

namespace TimeLedger.Tests;

public class TimesheetCalculatorTests
{
    private static readonly TimeZoneInfo Jakarta = TimeZoneInfo.FindSystemTimeZoneById("Asia/Jakarta");
    private static readonly Guid EmployeeId = Guid.NewGuid();

    // Mon 2 Nov 2026 – Sun 8 Nov 2026
    private static readonly DateOnly Monday = new(2026, 11, 2);

    private static ClockEvent Punch(ClockEventType type, DateOnly day, int hour, int minute = 0) => new()
    {
        EmployeeId = EmployeeId,
        Type = type,
        // Build the local Jakarta time (UTC+7, no DST) and store it in UTC, as the API does.
        OccurredAt = new DateTimeOffset(day.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.FromHours(7)).ToUniversalTime(),
    };

    private static TimesheetInput Input(
        IReadOnlyList<ClockEvent> events,
        DateOnly? start = null,
        DateOnly? end = null,
        IReadOnlySet<DateOnly>? holidays = null,
        IReadOnlyDictionary<DateOnly, LeaveType>? leave = null,
        DateOnly? asOf = null) =>
        new(WorkSchedule.FiveDay, 5_190_000m, Jakarta,
            start ?? Monday, end ?? Monday, events,
            holidays ?? new HashSet<DateOnly>(),
            leave ?? new Dictionary<DateOnly, LeaveType>(),
            asOf ?? Monday);

    [Fact]
    public void Pairs_in_and_out_into_a_shift_with_overtime()
    {
        var days = TimesheetCalculator.Calculate(Input([
            Punch(ClockEventType.ClockIn, Monday, 8),
            Punch(ClockEventType.ClockOut, Monday, 18),
        ]));

        var day = Assert.Single(days);
        Assert.Equal(600, day.WorkedMinutes);
        Assert.Equal(120, day.OvertimeMinutes);
        Assert.Equal(105_000m, day.OvertimePay);
        Assert.Empty(day.Anomalies);
    }

    [Fact]
    public void Split_shift_minutes_are_summed()
    {
        var days = TimesheetCalculator.Calculate(Input([
            Punch(ClockEventType.ClockIn, Monday, 8),
            Punch(ClockEventType.ClockOut, Monday, 12),
            Punch(ClockEventType.ClockIn, Monday, 13),
            Punch(ClockEventType.ClockOut, Monday, 17),
        ]));

        Assert.Equal(480, Assert.Single(days).WorkedMinutes);
    }

    [Fact]
    public void Night_shift_belongs_to_the_local_day_it_started()
    {
        var tuesday = Monday.AddDays(1);
        var days = TimesheetCalculator.Calculate(Input([
            Punch(ClockEventType.ClockIn, Monday, 22),
            Punch(ClockEventType.ClockOut, tuesday, 6),
        ], end: tuesday, asOf: Monday));

        var day = Assert.Single(days);
        Assert.Equal(Monday, day.Date);
        Assert.Equal(480, day.WorkedMinutes);
    }

    [Fact]
    public void Local_day_is_used_even_when_utc_date_differs()
    {
        // 06:00 Jakarta on Monday is 23:00 UTC on Sunday.
        var days = TimesheetCalculator.Calculate(Input([
            Punch(ClockEventType.ClockIn, Monday, 6),
            Punch(ClockEventType.ClockOut, Monday, 14),
        ]));

        Assert.Equal(Monday, Assert.Single(days).Date);
    }

    [Fact]
    public void Missing_clock_out_is_flagged_not_guessed()
    {
        var days = TimesheetCalculator.Calculate(Input([Punch(ClockEventType.ClockIn, Monday, 8)]));

        var day = Assert.Single(days);
        Assert.Equal(0, day.WorkedMinutes);
        Assert.Contains(day.Anomalies, a => a.Contains("Missing clock-out"));
    }

    [Fact]
    public void Orphan_clock_out_is_flagged()
    {
        var days = TimesheetCalculator.Calculate(Input([Punch(ClockEventType.ClockOut, Monday, 17)]));

        Assert.Contains(Assert.Single(days).Anomalies, a => a.Contains("without a matching clock-in"));
    }

    [Fact]
    public void Saturday_work_is_all_rest_day_overtime()
    {
        var saturday = Monday.AddDays(5);
        var days = TimesheetCalculator.Calculate(Input([
            Punch(ClockEventType.ClockIn, saturday, 9),
            Punch(ClockEventType.ClockOut, saturday, 13),
        ], start: saturday, end: saturday, asOf: saturday));

        var day = Assert.Single(days);
        Assert.Equal(DayKind.RestDay, day.Kind);
        Assert.Equal(240, day.OvertimeMinutes);
        Assert.Equal(240_000m, day.OvertimePay); // 4h × 2x × 30,000
    }

    [Fact]
    public void Public_holiday_is_classified_before_weekday()
    {
        var days = TimesheetCalculator.Calculate(Input([
            Punch(ClockEventType.ClockIn, Monday, 8),
            Punch(ClockEventType.ClockOut, Monday, 12),
        ], holidays: new HashSet<DateOnly> { Monday }));

        Assert.Equal(DayKind.PublicHoliday, Assert.Single(days).Kind);
    }

    [Fact]
    public void Past_workday_with_no_punches_and_no_leave_is_flagged_absent()
    {
        var days = TimesheetCalculator.Calculate(Input([], asOf: Monday.AddDays(1)));

        Assert.Contains("No attendance", Assert.Single(Assert.Single(days).Anomalies));
    }

    [Fact]
    public void Today_and_future_days_are_not_flagged_absent()
    {
        var days = TimesheetCalculator.Calculate(Input([], end: Monday.AddDays(4), asOf: Monday));

        Assert.Empty(days);
    }

    [Fact]
    public void Approved_leave_day_appears_without_absence_flag()
    {
        var days = TimesheetCalculator.Calculate(Input([],
            leave: new Dictionary<DateOnly, LeaveType> { [Monday] = LeaveType.Sick },
            asOf: Monday.AddDays(1)));

        var day = Assert.Single(days);
        Assert.Equal(LeaveType.Sick, day.Leave);
        Assert.Empty(day.Anomalies);
    }

    [Fact]
    public void Working_on_a_leave_day_is_flagged()
    {
        var days = TimesheetCalculator.Calculate(Input([
                Punch(ClockEventType.ClockIn, Monday, 8),
                Punch(ClockEventType.ClockOut, Monday, 10),
            ],
            leave: new Dictionary<DateOnly, LeaveType> { [Monday] = LeaveType.Annual }));

        Assert.Contains(Assert.Single(days).Anomalies, a => a.Contains("approved Annual leave"));
    }

    [Fact]
    public void Weekly_overtime_above_18_hours_is_flagged_on_the_day_it_crosses()
    {
        // 07:00–19:00 Mon–Fri = 4h overtime a day: 16h by Thursday, 20h on Friday.
        var events = new List<ClockEvent>();
        for (var i = 0; i < 5; i++)
        {
            var d = Monday.AddDays(i);
            events.Add(Punch(ClockEventType.ClockIn, d, 7));
            events.Add(Punch(ClockEventType.ClockOut, d, 19)); // 12h → 4h OT
        }

        var days = TimesheetCalculator.Calculate(Input(events, end: Monday.AddDays(4), asOf: Monday.AddDays(5)));

        Assert.DoesNotContain(days.Take(4).SelectMany(d => d.Anomalies), a => a.Contains("Weekly"));
        Assert.Contains(days[4].Anomalies, a => a.Contains("18h limit"));
    }

    [Fact]
    public void Summary_totals_the_period()
    {
        var tuesday = Monday.AddDays(1);
        var days = TimesheetCalculator.Calculate(Input([
                Punch(ClockEventType.ClockIn, Monday, 8),
                Punch(ClockEventType.ClockOut, Monday, 18),
            ],
            end: tuesday,
            leave: new Dictionary<DateOnly, LeaveType> { [tuesday] = LeaveType.Annual },
            asOf: tuesday.AddDays(1)));

        var s = TimesheetCalculator.Summarize(days);
        Assert.Equal(600, s.WorkedMinutes);
        Assert.Equal(120, s.OvertimeMinutes);
        Assert.Equal(105_000m, s.OvertimePay);
        Assert.Equal(1, s.DaysWorked);
        Assert.Equal(1m, s.AnnualLeaveDays);
        Assert.Equal(0, s.AnomalyCount);
    }
}
