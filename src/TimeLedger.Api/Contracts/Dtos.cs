using TimeLedger.Domain;
using TimeLedger.Domain.Entities;
using TimeLedger.Domain.Rules;

namespace TimeLedger.Api.Contracts;

// ---- Employees ----
public sealed record CreateEmployeeRequest(
    string? EmployeeCode, string? FullName, string? Email, decimal MonthlyBaseSalary,
    WorkSchedule Schedule = WorkSchedule.FiveDay, string? TimeZoneId = "Asia/Jakarta", Guid? SupervisorId = null);

public sealed record SetSupervisorRequest(Guid? SupervisorId);

/// <summary><see cref="MonthlyBaseSalary"/> is null when the caller may not see it.</summary>
public sealed record EmployeeDto(
    Guid Id, string EmployeeCode, string FullName, string Email, decimal? MonthlyBaseSalary,
    WorkSchedule Schedule, string TimeZoneId, Guid? SupervisorId, bool IsActive)
{
    public static EmployeeDto From(Employee e, Actor viewer) =>
        new(e.Id, e.EmployeeCode, e.FullName, e.Email,
            AccessRules.CanViewPay(viewer, e.Id) ? e.MonthlyBaseSalary : null,
            e.Schedule, e.TimeZoneId, e.SupervisorId, e.IsActive);
}

// ---- Clock ----
public sealed record ClockEventRequest(Guid EmployeeId, ClockEventType Type, ClockSource Source = ClockSource.Web, DateTimeOffset? OccurredAt = null);

public sealed record ClockEventDto(Guid Id, Guid EmployeeId, ClockEventType Type, DateTimeOffset OccurredAt, ClockSource Source)
{
    public static ClockEventDto From(ClockEvent e) => new(e.Id, e.EmployeeId, e.Type, e.OccurredAt, e.Source);
}

// ---- Leave ----
public sealed record CreateLeaveRequest(Guid EmployeeId, LeaveType Type, DateOnly StartDate, DateOnly EndDate, string? Reason);

/// <summary>The decider is always the signed-in user, never a field in the body.</summary>
public sealed record DecideLeaveRequest(string? Note);

public sealed record LeaveRequestDto(
    Guid Id, Guid EmployeeId, string EmployeeCode, string EmployeeName, LeaveType Type,
    DateOnly StartDate, DateOnly EndDate, decimal Days, LeaveStatus Status, string? Reason,
    string? DecidedBy, string? DecisionNote, DateTimeOffset? DecidedAt, DateTimeOffset CreatedAt);

public sealed record LeaveBalanceDto(Guid EmployeeId, int Year, decimal EntitledDays, decimal UsedDays, decimal RemainingDays);

// ---- Holidays ----
public sealed record HolidayDto(DateOnly Date, string? Name);

// ---- Pay periods & timesheets ----
public sealed record CreatePayPeriodRequest(DateOnly StartDate, DateOnly EndDate);

public sealed record PayPeriodDto(Guid Id, DateOnly StartDate, DateOnly EndDate, PayPeriodStatus Status, DateTimeOffset? ClosedAt)
{
    public static PayPeriodDto From(PayPeriod p) => new(p.Id, p.StartDate, p.EndDate, p.Status, p.ClosedAt);
}

/// <summary><see cref="OvertimePay"/> is null when the caller may not see it.</summary>
public sealed record DailyTimesheetDto(
    DateOnly Date, DayKind Kind, int WorkedMinutes, int RegularMinutes, int OvertimeMinutes,
    decimal? OvertimePay, LeaveType? Leave, IReadOnlyList<string> Anomalies)
{
    public static DailyTimesheetDto From(DailyTimesheet d, bool showPay) =>
        new(d.Date, d.Kind, d.WorkedMinutes, d.RegularMinutes, d.OvertimeMinutes, showPay ? d.OvertimePay : null, d.Leave, d.Anomalies);
}

/// <summary><see cref="OvertimePay"/> is null when the caller may not see it.</summary>
public sealed record TimesheetSummaryDto(
    int WorkedMinutes, int RegularMinutes, int OvertimeMinutes, decimal? OvertimePay, int DaysWorked,
    decimal AnnualLeaveDays, decimal SickLeaveDays, decimal UnpaidLeaveDays, int AnomalyCount)
{
    public static TimesheetSummaryDto From(TimesheetSummary s, bool showPay) =>
        new(s.WorkedMinutes, s.RegularMinutes, s.OvertimeMinutes, showPay ? s.OvertimePay : null, s.DaysWorked,
            s.AnnualLeaveDays, s.SickLeaveDays, s.UnpaidLeaveDays, s.AnomalyCount);
}

public sealed record EmployeeTimesheetDto(
    Guid EmployeeId, string EmployeeCode, string FullName, TimesheetSummaryDto Summary, IReadOnlyList<DailyTimesheetDto> Days);

public sealed record PeriodTimesheetsDto(PayPeriodDto Period, bool IsFinal, IReadOnlyList<EmployeeTimesheetDto> Employees);

// ---- Integrations ----
public sealed record CreateIntegrationRequest(
    string? Name, IntegrationProvider Provider, string? EndpointUrl, IReadOnlyList<string>? EventTypes, string? Secret);

public sealed record IntegrationDto(
    Guid Id, string Name, IntegrationProvider Provider, string EndpointUrl, IReadOnlyList<string> EventTypes,
    bool IsEnabled, string SecretHint)
{
    public static IntegrationDto From(IntegrationConnection c) =>
        new(c.Id, c.Name, c.Provider, c.EndpointUrl, c.EventTypes, c.IsEnabled,
            // Four characters of a short secret would be most or all of it.
            c.Secret.Length >= MinSecretLength ? "…" + c.Secret[^4..] : "…");

    public const int MinSecretLength = 16;
}

/// <summary>Returned once, at creation: the only time the full signing secret is shown.</summary>
public sealed record CreatedIntegrationDto(IntegrationDto Integration, string Secret);

public sealed record DeliveryDto(
    Guid Id, Guid ConnectionId, string ConnectionName, string EventType, DeliveryStatus Status, int Attempts,
    DateTimeOffset NextAttemptAt, int? LastStatusCode, string? LastError, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);
