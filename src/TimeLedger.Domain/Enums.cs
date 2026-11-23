namespace TimeLedger.Domain;

/// <summary>Working-time arrangement under Indonesian labour law (UU 13/2003 as amended, PP 35/2021).</summary>
public enum WorkSchedule
{
    /// <summary>5 working days, 8 hours/day, 40 hours/week. Saturday and Sunday are rest days.</summary>
    FiveDay = 5,

    /// <summary>6 working days, 7 hours/day, 40 hours/week. Sunday is the rest day.</summary>
    SixDay = 6,
}

public enum UserRole
{
    /// <summary>Own attendance, timesheets and leave.</summary>
    Employee = 1,

    /// <summary>An employee who also reviews their direct reports and decides their leave.</summary>
    Supervisor = 2,

    /// <summary>Company-wide payroll, setup, integrations and accounts.</summary>
    PayrollAdmin = 3,
}

public enum ClockEventType
{
    ClockIn = 1,
    ClockOut = 2,
}

public enum ClockSource
{
    Web = 1,
    Mobile = 2,
    Kiosk = 3,
    Api = 4,
}

public enum DayKind
{
    Workday = 1,
    RestDay = 2,
    PublicHoliday = 3,
}

public enum LeaveType
{
    Annual = 1,
    Sick = 2,
    Unpaid = 3,
}

public enum LeaveStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,
}

public enum PayPeriodStatus
{
    Open = 1,
    Closed = 2,
}

public enum IntegrationProvider
{
    /// <summary>Canonical TimeLedger event envelope, HMAC-signed.</summary>
    GenericWebhook = 1,

    /// <summary>Payroll-provider mapping modelled on Gusto's employee-compensation shape (illustrative).</summary>
    GustoStyle = 2,
}

public enum DeliveryStatus
{
    Pending = 1,
    Succeeded = 2,
    Skipped = 3,
    DeadLettered = 4,
}
