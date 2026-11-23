using TimeLedger.Domain.Integration;

namespace TimeLedger.Domain.Entities;

/// <summary>
/// A domain event written in the same transaction as the state change that caused it
/// (transactional outbox), so an event is never lost and never sent for a rolled-back change.
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Type { get; set; }
    public Guid AggregateId { get; set; }

    /// <summary>Event payload as JSON (jsonb).</summary>
    public required string Payload { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Set once deliveries have been created for every subscribed connection.</summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    public static OutboxMessage Create(string type, Guid aggregateId, string payloadJson, DateTimeOffset now) =>
        new() { Type = type, AggregateId = aggregateId, Payload = payloadJson, CreatedAt = now };
}

public class IntegrationConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public IntegrationProvider Provider { get; set; }
    public required string EndpointUrl { get; set; }

    /// <summary>HMAC signing secret shared with the receiver.</summary>
    public required string Secret { get; set; }

    /// <summary>Event types this connection subscribes to, e.g. "payroll.period_closed".</summary>
    public List<string> EventTypes { get; set; } = [];

    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    public bool IsSubscribedTo(string eventType) => IsEnabled && EventTypes.Contains(eventType);
}

/// <summary>One attempt-tracked delivery of one outbox message to one connection.</summary>
public class WebhookDelivery
{
    public const int MaxErrorLength = 1000;

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OutboxMessageId { get; set; }
    public Guid ConnectionId { get; set; }
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;
    public int Attempts { get; set; }

    /// <summary>When the worker may next pick this up. Also used as a short lease while a send is in flight.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public static WebhookDelivery Create(Guid outboxMessageId, Guid connectionId, DateTimeOffset now) => new()
    {
        OutboxMessageId = outboxMessageId,
        ConnectionId = connectionId,
        NextAttemptAt = now,
        CreatedAt = now,
    };

    public void MarkSucceeded(int statusCode, DateTimeOffset now)
    {
        Attempts++;
        Status = DeliveryStatus.Succeeded;
        LastStatusCode = statusCode;
        LastError = null;
        CompletedAt = now;
    }

    /// <summary>The connector has no mapping for this event type; nothing to send.</summary>
    public void MarkSkipped(string reason, DateTimeOffset now)
    {
        Status = DeliveryStatus.Skipped;
        LastError = reason;
        CompletedAt = now;
    }

    public void MarkFailed(int? statusCode, string error, DateTimeOffset now, RetryPolicy policy, double jitter)
    {
        Attempts++;
        LastStatusCode = statusCode;
        LastError = StorableError(error);

        if (!RetryPolicy.IsRetryable(statusCode) || policy.ShouldDeadLetter(Attempts))
        {
            Status = DeliveryStatus.DeadLettered;
            CompletedAt = now;
            return;
        }

        NextAttemptAt = now + policy.NextDelay(Attempts, jitter);
    }

    /// <summary>Receiver-controlled text: PostgreSQL rejects NUL, and half a surrogate pair can't be encoded.</summary>
    private static string StorableError(string error)
    {
        var clean = error.Replace("\0", string.Empty);
        if (clean.Length <= MaxErrorLength)
        {
            return clean;
        }

        var length = char.IsHighSurrogate(clean[MaxErrorLength - 1]) ? MaxErrorLength - 1 : MaxErrorLength;
        return clean[..length];
    }

    /// <summary>Operator action: give a dead-lettered delivery a fresh retry budget.</summary>
    public void Requeue(DateTimeOffset now)
    {
        if (Status != DeliveryStatus.DeadLettered)
        {
            throw new DomainException($"Only dead-lettered deliveries can be requeued (this one is {Status}).");
        }

        Status = DeliveryStatus.Pending;
        Attempts = 0;
        NextAttemptAt = now;
        CompletedAt = null;
    }
}
