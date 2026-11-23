namespace TimeLedger.Domain.Entities;

/// <summary>An immutable punch. Timesheets are always derived from these, never edited in place.</summary>
public class ClockEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public ClockEventType Type { get; set; }

    /// <summary>When the punch happened, in UTC.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    public ClockSource Source { get; set; }

    /// <summary>Client-supplied key; a unique index backs up the Redis idempotency check.</summary>
    public string? IdempotencyKey { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
