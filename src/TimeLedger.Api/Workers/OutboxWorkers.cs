using Microsoft.EntityFrameworkCore;
using TimeLedger.Api.Data;
using TimeLedger.Api.Integrations;
using TimeLedger.Domain.Entities;
using TimeLedger.Domain.Integration;

namespace TimeLedger.Api.Workers;

/// <summary>Shared polling loop: run a batch, sleep, repeat; never let one bad batch kill the worker.</summary>
public abstract class PollingWorker(IServiceScopeFactory scopes, ILogger logger, TimeSpan interval) : BackgroundService
{
    protected ILogger Logger { get; } = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await RunBatchAsync(scope.ServiceProvider, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    Logger.LogError(ex, "{Worker} batch failed", GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    protected abstract Task RunBatchAsync(IServiceProvider services, CancellationToken ct);
}

/// <summary>
/// Stage 1: turns each unprocessed outbox message into one delivery row per subscribed connection.
/// FOR UPDATE SKIP LOCKED lets several API instances run this worker without double fan-out.
/// </summary>
public sealed class OutboxFanOutWorker(IServiceScopeFactory scopes, ILogger<OutboxFanOutWorker> logger)
    : PollingWorker(scopes, logger, TimeSpan.FromSeconds(2))
{
    private const int BatchSize = 50;

    protected override async Task RunBatchAsync(IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<TimeLedgerDbContext>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var messages = await db.OutboxMessages
            .FromSql($"""
                SELECT * FROM outbox_messages
                WHERE processed_at IS NULL
                ORDER BY created_at
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        if (messages.Count == 0)
        {
            await tx.CommitAsync(ct);
            return;
        }

        var connections = await db.IntegrationConnections.AsNoTracking().Where(c => c.IsEnabled).ToListAsync(ct);

        foreach (var message in messages)
        {
            foreach (var connection in connections.Where(c => c.IsSubscribedTo(message.Type)))
            {
                db.WebhookDeliveries.Add(WebhookDelivery.Create(message.Id, connection.Id, now));
            }

            message.ProcessedAt = now;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        Logger.LogInformation("Fanned out {Count} outbox message(s)", messages.Count);
    }
}

/// <summary>
/// Stage 2: sends due deliveries. Rows are claimed with an UPDATE … RETURNING that pushes
/// next_attempt_at forward by a lease, so the HTTP call happens outside any open transaction and a
/// crashed worker's deliveries are picked up again when the lease expires.
/// </summary>
public sealed class WebhookDeliveryWorker(IServiceScopeFactory scopes, ILogger<WebhookDeliveryWorker> logger)
    : PollingWorker(scopes, logger, TimeSpan.FromSeconds(2))
{
    private const int BatchSize = 20;
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    protected override async Task RunBatchAsync(IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<TimeLedgerDbContext>();
        var time = services.GetRequiredService<TimeProvider>();
        var http = services.GetRequiredService<IHttpClientFactory>().CreateClient("webhooks");
        var registry = services.GetRequiredService<ConnectorRegistry>();
        var policy = services.GetRequiredService<RetryPolicy>();

        var now = time.GetUtcNow();
        var leaseUntil = now + Lease;

        var claimed = await db.WebhookDeliveries
            .FromSql($"""
                WITH due AS MATERIALIZED (
                    SELECT d.id FROM webhook_deliveries d
                    JOIN integration_connections c ON c.id = d.connection_id AND c.is_enabled
                    WHERE d.status = 'Pending' AND d.next_attempt_at <= {now}
                    ORDER BY d.next_attempt_at
                    LIMIT {BatchSize}
                    FOR UPDATE OF d SKIP LOCKED)
                UPDATE webhook_deliveries SET next_attempt_at = {leaseUntil}
                WHERE id IN (SELECT id FROM due)
                RETURNING *
                """)
            .ToListAsync(ct);

        if (claimed.Count == 0)
        {
            return;
        }

        var messageIds = claimed.Select(d => d.OutboxMessageId).Distinct().ToList();
        var connectionIds = claimed.Select(d => d.ConnectionId).Distinct().ToList();
        var messages = await db.OutboxMessages.AsNoTracking().Where(m => messageIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        var connections = await db.IntegrationConnections.AsNoTracking().Where(c => connectionIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);

        // In parallel, so a batch takes as long as its slowest receiver (at most the HTTP timeout). One at a
        // time, 20 slow receivers would outlast the lease and another instance would send the same rows again.
        // The context isn't thread-safe: each send only mutates its own delivery, and saving happens afterwards.
        await Task.WhenAll(claimed.Select(delivery => SendOneAsync(
            delivery, messages[delivery.OutboxMessageId], connections[delivery.ConnectionId], http, registry, policy, time, ct)));
        await db.SaveChangesAsync(ct);
    }

    private async Task SendOneAsync(
        WebhookDelivery delivery, OutboxMessage message, IntegrationConnection connection,
        HttpClient http, ConnectorRegistry registry, RetryPolicy policy, TimeProvider time, CancellationToken ct)
    {
        string? body;
        try
        {
            body = registry.Get(connection.Provider).BuildBody(message);
        }
        catch (Exception ex)
        {
            // A mapping bug will not fix itself on retry.
            delivery.MarkFailed(400, $"Payload mapping failed: {ex.Message}", time.GetUtcNow(), policy, 0);
            Logger.LogError(ex, "Mapping failed for delivery {DeliveryId}", delivery.Id);
            return;
        }

        if (body is null)
        {
            delivery.MarkSkipped($"{connection.Provider} has no mapping for {message.Type}.", time.GetUtcNow());
            return;
        }

        try
        {
            using var request = ConnectorRegistry.CreateSignedRequest(connection, message, delivery.Id, body, time.GetUtcNow());
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                delivery.MarkSucceeded(status, time.GetUtcNow());
                Logger.LogInformation("Delivered {Event} to {Connection} ({Status})", message.Type, connection.Name, status);
                return;
            }

            var detail = await ReadErrorAsync(response, http.Timeout, ct);
            delivery.MarkFailed(status, $"HTTP {status}: {detail}", time.GetUtcNow(), policy, Random.Shared.NextDouble());
            Logger.LogWarning("Delivery {DeliveryId} to {Connection} failed with {Status}; now {DeliveryStatus}",
                delivery.Id, connection.Name, status, delivery.Status);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Anything uncaught here would fail the whole batch and resend its successes after the lease.
            delivery.MarkFailed(null, ex.Message, time.GetUtcNow(), policy, Random.Shared.NextDouble());
            Logger.LogWarning("Delivery {DeliveryId} to {Connection} failed: {Error}", delivery.Id, connection.Name, ex.Message);
        }
    }

    /// <summary>Only the start of an error body is stored, so only the start is read, and only for a limited time.</summary>
    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);

        var buffer = new byte[WebhookDelivery.MaxErrorLength * 4];
        var read = 0;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
            read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, limit.Token);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // The status code decides what happens next; an unreadable body must not turn a 400 into a retry.
            return $"(body unreadable: {ex.Message})";
        }

        return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
    }
}
