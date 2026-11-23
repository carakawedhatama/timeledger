using TimeLedger.Domain;
using TimeLedger.Domain.Entities;
using TimeLedger.Domain.Rules;

namespace TimeLedger.Tests;

public class LeaveAndClockRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Leave_days_skip_weekends_and_holidays()
    {
        // Fri 13 Nov → Tue 17 Nov 2026, with Monday 16 Nov as a holiday: Fri + Tue = 2 days.
        var holidays = new HashSet<DateOnly> { new(2026, 11, 16) };

        var days = LeaveCalendar.CountDays(new DateOnly(2026, 11, 13), new DateOnly(2026, 11, 17), WorkSchedule.FiveDay, holidays);

        Assert.Equal(2m, days);
    }

    [Fact]
    public void Six_day_schedule_counts_saturday_as_a_leave_day()
    {
        var days = LeaveCalendar.CountDays(new DateOnly(2026, 11, 13), new DateOnly(2026, 11, 15), WorkSchedule.SixDay, new HashSet<DateOnly>());

        Assert.Equal(2m, days); // Fri + Sat; Sunday is the rest day
    }

    [Fact]
    public void Only_pending_requests_can_be_decided()
    {
        var request = new LeaveRequest { Type = LeaveType.Annual };
        request.Approve("hr.lead", Now);

        var ex = Assert.Throws<DomainException>(() => request.Reject("hr.lead", "changed mind", Now));
        Assert.Contains("already Approved", ex.Message);
    }

    [Fact]
    public void Annual_balance_cannot_go_negative()
    {
        var balance = new LeaveBalance { UsedDays = 11m };

        Assert.Throws<DomainException>(() => balance.Consume(2m));
        balance.Consume(1m);
        Assert.Equal(0m, balance.RemainingDays);
    }

    [Fact]
    public void Expand_ignores_unapproved_requests()
    {
        var requests = new[]
        {
            new LeaveRequest { Type = LeaveType.Sick, StartDate = new(2026, 11, 2), EndDate = new(2026, 11, 3), Status = LeaveStatus.Approved },
            new LeaveRequest { Type = LeaveType.Annual, StartDate = new(2026, 11, 4), EndDate = new(2026, 11, 4), Status = LeaveStatus.Pending },
        };

        var map = LeaveCalendar.ExpandApproved(requests, WorkSchedule.FiveDay, new HashSet<DateOnly>());

        Assert.Equal(2, map.Count);
        Assert.Equal(LeaveType.Sick, map[new DateOnly(2026, 11, 3)]);
    }

    [Theory]
    [InlineData(null, ClockEventType.ClockIn, true)]
    [InlineData(null, ClockEventType.ClockOut, false)]
    [InlineData(ClockEventType.ClockIn, ClockEventType.ClockOut, true)]
    [InlineData(ClockEventType.ClockIn, ClockEventType.ClockIn, false)]
    [InlineData(ClockEventType.ClockOut, ClockEventType.ClockIn, true)]
    [InlineData(ClockEventType.ClockOut, ClockEventType.ClockOut, false)]
    public void Clock_sequence_rules(ClockEventType? last, ClockEventType next, bool valid)
    {
        var error = ClockRules.Validate(last, last is null ? null : Now.AddHours(-1), next, Now);

        Assert.Equal(valid, error is null);
    }

    [Fact]
    public void Backdated_punch_is_rejected()
    {
        var error = ClockRules.Validate(ClockEventType.ClockIn, Now, ClockEventType.ClockOut, Now.AddMinutes(-5));

        Assert.Contains("earlier than", error);
    }

    [Fact]
    public void Pay_period_is_validated_and_closes_once()
    {
        Assert.Throws<DomainException>(() => PayPeriod.Create(new(2026, 11, 30), new(2026, 11, 1), Now));
        Assert.Throws<DomainException>(() => PayPeriod.Create(new(2026, 11, 1), new(2026, 12, 15), Now));

        var period = PayPeriod.Create(new(2026, 11, 1), new(2026, 11, 30), Now);
        period.Close(Now);

        Assert.Equal(PayPeriodStatus.Closed, period.Status);
        Assert.Throws<DomainException>(() => period.Close(Now));
    }
}
