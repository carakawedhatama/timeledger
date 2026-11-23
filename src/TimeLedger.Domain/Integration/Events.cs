namespace TimeLedger.Domain.Integration;

public static class EventTypes
{
    public const string PayrollPeriodClosed = "payroll.period_closed";
    public const string LeaveApproved = "leave.approved";

    public static readonly IReadOnlyList<string> All = [PayrollPeriodClosed, LeaveApproved];
}

/// <summary>Published when a pay period is closed and its timesheets are frozen.</summary>
public sealed record PayrollPeriodClosedEvent(
    Guid PayPeriodId,
    DateOnly StartDate,
    DateOnly EndDate,
    DateTimeOffset ClosedAt,
    string Currency,
    IReadOnlyList<EmployeePayrollLine> Employees);

public sealed record EmployeePayrollLine(
    Guid EmployeeId,
    string EmployeeCode,
    string FullName,
    decimal RegularHours,
    decimal OvertimeHours,
    decimal OvertimePay,
    decimal AnnualLeaveDays,
    decimal SickLeaveDays,
    decimal UnpaidLeaveDays,
    int AnomalyCount,
    decimal NormalDailyHours = 8m);

public sealed record LeaveApprovedEvent(
    Guid LeaveRequestId,
    Guid EmployeeId,
    string EmployeeCode,
    LeaveType Type,
    DateOnly StartDate,
    DateOnly EndDate,
    decimal Days);
