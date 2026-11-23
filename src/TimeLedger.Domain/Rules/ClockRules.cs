namespace TimeLedger.Domain.Rules;

public static class ClockRules
{
    /// <summary>
    /// Validates a new punch against the employee's latest punch. Returns an error message, or null if valid.
    /// </summary>
    public static string? Validate(ClockEventType? lastType, DateTimeOffset? lastAt, ClockEventType newType, DateTimeOffset occurredAt)
    {
        if (lastAt is not null && occurredAt < lastAt)
        {
            return "Punch is earlier than the employee's latest punch. Backdated punches need a timesheet adjustment.";
        }

        return newType switch
        {
            ClockEventType.ClockIn when lastType == ClockEventType.ClockIn => "Employee is already clocked in.",
            ClockEventType.ClockOut when lastType != ClockEventType.ClockIn => "Employee is not clocked in.",
            _ => null,
        };
    }
}
