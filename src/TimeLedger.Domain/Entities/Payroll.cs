namespace TimeLedger.Domain.Entities;

public class PayPeriod
{
    public const int MaxLengthDays = 31;

    public Guid Id { get; set; } = Guid.NewGuid();
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public PayPeriodStatus Status { get; set; } = PayPeriodStatus.Open;
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public uint Version { get; set; }

    public static PayPeriod Create(DateOnly start, DateOnly end, DateTimeOffset now)
    {
        if (end < start)
        {
            throw new DomainException("Pay period end date must be on or after its start date.");
        }

        if (end.DayNumber - start.DayNumber + 1 > MaxLengthDays)
        {
            throw new DomainException($"A pay period can span at most {MaxLengthDays} days.");
        }

        return new PayPeriod { StartDate = start, EndDate = end, CreatedAt = now };
    }

    public bool Overlaps(DateOnly start, DateOnly end) => StartDate <= end && EndDate >= start;

    public void Close(DateTimeOffset now)
    {
        if (Status == PayPeriodStatus.Closed)
        {
            throw new DomainException("Pay period is already closed.");
        }

        Status = PayPeriodStatus.Closed;
        ClosedAt = now;
    }
}

/// <summary>A frozen daily timesheet line, written when a pay period closes.</summary>
public class TimesheetEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PayPeriodId { get; set; }
    public Guid EmployeeId { get; set; }
    public DateOnly WorkDate { get; set; }
    public DayKind DayKind { get; set; }
    public int WorkedMinutes { get; set; }
    public int RegularMinutes { get; set; }
    public int OvertimeMinutes { get; set; }
    public decimal OvertimePay { get; set; }
    public LeaveType? LeaveType { get; set; }
    public List<string> Anomalies { get; set; } = [];
}
