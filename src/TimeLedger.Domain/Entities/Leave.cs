namespace TimeLedger.Domain.Entities;

public class LeaveRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public LeaveType Type { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }

    /// <summary>Working days covered, excluding rest days and public holidays.</summary>
    public decimal Days { get; set; }

    public LeaveStatus Status { get; set; } = LeaveStatus.Pending;
    public string? Reason { get; set; }
    public string? DecidedBy { get; set; }
    public string? DecisionNote { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Optimistic concurrency token (mapped to PostgreSQL xmin).</summary>
    public uint Version { get; set; }

    public bool Overlaps(DateOnly start, DateOnly end) => StartDate <= end && EndDate >= start;

    public void Approve(string approver, DateTimeOffset now)
    {
        EnsurePending();
        Status = LeaveStatus.Approved;
        DecidedBy = approver;
        DecidedAt = now;
    }

    public void Reject(string approver, string? note, DateTimeOffset now)
    {
        EnsurePending();
        Status = LeaveStatus.Rejected;
        DecidedBy = approver;
        DecisionNote = note;
        DecidedAt = now;
    }

    private void EnsurePending()
    {
        if (Status != LeaveStatus.Pending)
        {
            throw new DomainException($"Leave request is already {Status}; only pending requests can be decided.");
        }
    }
}

/// <summary>Annual leave balance for one employee and calendar year.</summary>
public class LeaveBalance
{
    /// <summary>Statutory minimum after 12 months of continuous service (UU 13/2003, as amended).</summary>
    public const decimal StatutoryAnnualDays = 12m;

    public Guid EmployeeId { get; set; }
    public int Year { get; set; }
    public decimal EntitledDays { get; set; } = StatutoryAnnualDays;
    public decimal UsedDays { get; set; }
    public uint Version { get; set; }

    public decimal RemainingDays => EntitledDays - UsedDays;

    public void Consume(decimal days)
    {
        if (days <= 0)
        {
            throw new DomainException("Leave days must be positive.");
        }

        if (days > RemainingDays)
        {
            throw new DomainException($"Insufficient annual leave: {RemainingDays} day(s) remaining, {days} requested.");
        }

        UsedDays += days;
    }
}

public class PublicHoliday
{
    public DateOnly Date { get; set; }
    public required string Name { get; set; }
}
