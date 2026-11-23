using System.Text.Json;
using TimeLedger.Api.Infrastructure;
using TimeLedger.Api.Integrations;
using TimeLedger.Domain;
using TimeLedger.Domain.Entities;
using TimeLedger.Domain.Integration;
using TimeLedger.Domain.Rules;

namespace TimeLedger.Tests;

public class RuleRegressionTests
{
    private static readonly TimeZoneInfo Jakarta = TimeZoneInfo.FindSystemTimeZoneById("Asia/Jakarta");
    private static readonly DateOnly Monday = new(2026, 11, 2);

    private static IEnumerable<ClockEvent> Shift(DateOnly day, int from, int to)
    {
        ClockEvent Punch(ClockEventType type, int hour) => new()
        {
            Type = type,
            OccurredAt = new DateTimeOffset(day.ToDateTime(new TimeOnly(hour, 0)), TimeSpan.FromHours(7)).ToUniversalTime(),
        };

        return [Punch(ClockEventType.ClockIn, from), Punch(ClockEventType.ClockOut, to)];
    }

    private static IReadOnlyList<DailyTimesheet> Calculate(
        IEnumerable<ClockEvent> events, DateOnly start, DateOnly end, params DateOnly[] holidays) =>
        TimesheetCalculator.Calculate(new TimesheetInput(WorkSchedule.FiveDay, 5_190_000m, Jakarta, start, end,
            events.ToList(), holidays.ToHashSet(), new Dictionary<DateOnly, LeaveType>(), start));

    [Fact]
    public void Rest_day_work_does_not_count_towards_the_weekly_limit()
    {
        // 16h of workday overtime, then 5h on Saturday (PP 35/2021, Pasal 26 ayat 2).
        var events = Enumerable.Range(0, 4).SelectMany(i => Shift(Monday.AddDays(i), 7, 19))
            .Concat(Shift(Monday.AddDays(5), 8, 13));

        var days = Calculate(events, Monday, Monday.AddDays(6));

        Assert.DoesNotContain(days.SelectMany(d => d.Anomalies), a => a.Contains("Weekly"));
    }

    [Fact]
    public void Holiday_work_does_not_count_towards_the_weekly_limit()
    {
        var events = Enumerable.Range(0, 5).SelectMany(i => Shift(Monday.AddDays(i), 7, 19));

        var days = Calculate(events, Monday, Monday.AddDays(4), Monday);

        Assert.DoesNotContain(days.SelectMany(d => d.Anomalies), a => a.Contains("Weekly"));
    }

    [Fact]
    public void Weekly_limit_spans_two_periods_without_leaking_days_between_them()
    {
        var events = Enumerable.Range(0, 5).SelectMany(i => Shift(Monday.AddDays(i), 7, 19));
        var thursday = Monday.AddDays(3);

        var days = Calculate(events, thursday, Monday.AddDays(4));

        Assert.Equal([thursday, thursday.AddDays(1)], days.Select(d => d.Date));
        Assert.Contains(days[1].Anomalies, a => a.Contains("18h limit"));
        Assert.Equal(480, TimesheetCalculator.Summarize(days).OvertimeMinutes);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(8, 2)]
    [InlineData(9, 9)]
    [InlineData(1, 26)] // Sunday 1 Nov belongs to the week of Monday 26 Oct
    public void Week_starts_on_monday(int day, int expectedDay)
    {
        Assert.Equal(expectedDay, TimesheetCalculator.WeekStart(new DateOnly(2026, 11, day)).Day);
    }

    [Fact]
    public void Leave_is_counted_per_calendar_year()
    {
        var byYear = LeaveCalendar.CountDaysByYear(new(2026, 12, 28), new(2027, 1, 6), WorkSchedule.FiveDay,
            new HashSet<DateOnly> { new(2027, 1, 1) });

        Assert.Equal(4m, byYear[2026]);
        Assert.Equal(3m, byYear[2027]);
    }

    [Fact]
    public void Receiver_text_is_made_storable()
    {
        var delivery = WebhookDelivery.Create(Guid.NewGuid(), Guid.NewGuid(), default);

        delivery.MarkFailed(500, "a\0b", default, new RetryPolicy(), 0);
        Assert.Equal("ab", delivery.LastError);

        delivery.MarkFailed(500, new string('x', WebhookDelivery.MaxErrorLength - 1) + "😀", default, new RetryPolicy(), 0);
        Assert.Equal(new string('x', WebhookDelivery.MaxErrorLength - 1), delivery.LastError);
    }

    [Theory]
    [InlineData(8, "16.000")]
    [InlineData(7, "14.000")]
    public void Payroll_export_converts_leave_days_with_the_employees_day_length(int hoursPerDay, string expected)
    {
        var line = new EmployeePayrollLine(Guid.NewGuid(), "E1", "Test", 0, 0, 0, 2, 0, 0, 0, hoursPerDay);

        Assert.Equal(expected, VacationHours(JsonSerializer.SerializeToNode(Event(line), ApiJson.Webhook)!.ToJsonString()));
    }

    [Fact]
    public void Events_stored_before_the_upgrade_still_export_eight_hour_days()
    {
        var line = new EmployeePayrollLine(Guid.NewGuid(), "E1", "Test", 0, 0, 0, 2, 0, 0, 0);
        var payload = JsonSerializer.SerializeToNode(Event(line), ApiJson.Webhook)!;
        payload["employees"]![0]!.AsObject().Remove("normal_daily_hours");

        Assert.Equal("16.000", VacationHours(payload.ToJsonString()));
    }

    private static PayrollPeriodClosedEvent Event(EmployeePayrollLine line) =>
        new(Guid.NewGuid(), Monday, Monday, default, "IDR", [line]);

    private static string? VacationHours(string payload)
    {
        var body = new GustoStyleConnector().BuildBody(
            OutboxMessage.Create(EventTypes.PayrollPeriodClosed, Guid.NewGuid(), payload, default))!;
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("employee_compensations")[0]
            .GetProperty("paid_time_off")[0].GetProperty("hours").GetString();
    }
}
