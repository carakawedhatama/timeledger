using Microsoft.EntityFrameworkCore;
using TimeLedger.Api.Contracts;
using TimeLedger.Domain;
using TimeLedger.Domain.Entities;
using TimeLedger.Domain.Rules;

namespace TimeLedger.Api.Data;

public sealed record EmployeeTimesheet(Employee Employee, IReadOnlyList<DailyTimesheet> Days, TimesheetSummary Summary);

/// <summary>Loads punches, leave and holidays for a pay period and runs the domain calculator per employee.</summary>
public sealed class TimesheetService(TimeLedgerDbContext db)
{
    public async Task<IReadOnlyList<EmployeeTimesheet>> BuildAsync(
        PayPeriod period, IQueryable<Employee> scope, DateOnly asOf, CancellationToken ct)
    {
        var employees = await scope.AsNoTracking()
            .Where(e => e.IsActive)
            .OrderBy(e => e.EmployeeCode)
            .ToListAsync(ct);
        var ids = employees.Select(e => e.Id).ToList();
        var weekStart = TimesheetCalculator.WeekStart(period.StartDate);

        var holidays = (await db.PublicHolidays.AsNoTracking()
                .Where(h => h.Date >= weekStart && h.Date <= period.EndDate)
                .Select(h => h.Date)
                .ToListAsync(ct))
            .ToHashSet();

        // Widen the UTC window by three days each side: one day for time zones, the rest so a long shift
        // across the edge keeps both of its punches. The calculator assigns shifts to local days.
        var fromUtc = new DateTimeOffset(weekStart.AddDays(-3).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var toUtc = new DateTimeOffset(period.EndDate.AddDays(4).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var events = await db.ClockEvents.AsNoTracking()
            .Where(c => ids.Contains(c.EmployeeId) && c.OccurredAt >= fromUtc && c.OccurredAt < toUtc)
            .OrderBy(c => c.OccurredAt).ThenBy(c => c.CreatedAt)
            .ToListAsync(ct);

        var leaves = await db.LeaveRequests.AsNoTracking()
            .Where(l => ids.Contains(l.EmployeeId) && l.Status == LeaveStatus.Approved && l.StartDate <= period.EndDate && l.EndDate >= weekStart)
            .ToListAsync(ct);

        var eventsByEmployee = events.ToLookup(e => e.EmployeeId);
        var leavesByEmployee = leaves.ToLookup(l => l.EmployeeId);

        return employees.Select(employee =>
        {
            var input = new TimesheetInput(
                employee.Schedule,
                employee.MonthlyBaseSalary,
                TimeZoneInfo.FindSystemTimeZoneById(employee.TimeZoneId),
                period.StartDate,
                period.EndDate,
                eventsByEmployee[employee.Id].ToList(),
                holidays,
                LeaveCalendar.ExpandApproved(leavesByEmployee[employee.Id], employee.Schedule, holidays),
                asOf);

            var days = TimesheetCalculator.Calculate(input);
            return new EmployeeTimesheet(employee, days, TimesheetCalculator.Summarize(days));
        }).ToList();
    }

    /// <summary>Reads the frozen lines of a closed period.</summary>
    public async Task<IReadOnlyList<EmployeeTimesheet>> LoadFrozenAsync(PayPeriod period, IQueryable<Employee> scope, CancellationToken ct)
    {
        var entries = await db.TimesheetEntries.AsNoTracking()
            .Where(t => t.PayPeriodId == period.Id && scope.Any(e => e.Id == t.EmployeeId))
            .OrderBy(t => t.WorkDate)
            .ToListAsync(ct);

        var employeeIds = entries.Select(e => e.EmployeeId).Distinct().ToList();
        var employees = await db.Employees.AsNoTracking()
            .Where(e => employeeIds.Contains(e.Id))
            .OrderBy(e => e.EmployeeCode)
            .ToListAsync(ct);

        var byEmployee = entries.ToLookup(e => e.EmployeeId);
        return employees.Select(employee =>
        {
            var days = byEmployee[employee.Id]
                .Select(t => new DailyTimesheet(t.WorkDate, t.DayKind, t.WorkedMinutes, t.RegularMinutes,
                    t.OvertimeMinutes, t.OvertimePay, t.LeaveType, t.Anomalies))
                .ToList();
            return new EmployeeTimesheet(employee, days, TimesheetCalculator.Summarize(days));
        }).ToList();
    }

    /// <param name="sheet">The timesheet.</param>
    /// <param name="showPay">False removes the money: overtime pay gives away the salary.</param>
    public static EmployeeTimesheetDto ToDto(EmployeeTimesheet sheet, bool showPay) => new(
        sheet.Employee.Id, sheet.Employee.EmployeeCode, sheet.Employee.FullName,
        TimesheetSummaryDto.From(sheet.Summary, showPay),
        sheet.Days.Select(d => DailyTimesheetDto.From(d, showPay)).ToList());
}
