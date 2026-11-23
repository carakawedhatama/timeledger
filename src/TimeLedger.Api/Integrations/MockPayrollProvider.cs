using System.Collections.Concurrent;
using TimeLedger.Api.Auth;
using TimeLedger.Domain.Integration;

namespace TimeLedger.Api.Integrations;

public sealed record ReceivedWebhook(DateTimeOffset ReceivedAt, string? Event, string? DeliveryId, bool SignatureValid, int RespondedWith, string Body);

/// <summary>
/// A stand-in payroll provider for local demos. It verifies signatures and fails a configurable share
/// of requests with 503 so the retry and dead-letter path can be watched end to end.
/// </summary>
public sealed class MockPayrollProvider
{
    private const int Keep = 50;
    private readonly ConcurrentQueue<ReceivedWebhook> _received = new();

    public IReadOnlyList<ReceivedWebhook> Received => _received.Reverse().ToList();

    public void Record(ReceivedWebhook item)
    {
        _received.Enqueue(item);
        while (_received.Count > Keep && _received.TryDequeue(out _))
        {
        }
    }

    public static IEndpointRouteBuilder MapMockPayrollProvider(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/mock/payroll-provider").WithTags("Mock payroll provider (dev only)");

        group.MapPost("/webhooks", async (HttpRequest request, MockPayrollProvider inbox, IConfiguration config, TimeProvider time) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            var secret = config["MockProvider:Secret"] ?? string.Empty;
            var failureRate = config.GetValue("MockProvider:FailureRate", 0.3);

            var valid = WebhookSigner.Verify(secret, request.Headers[WebhookSigner.SignatureHeader], body, time.GetUtcNow().ToUnixTimeSeconds());
            var status = !valid ? StatusCodes.Status401Unauthorized
                : Random.Shared.NextDouble() < failureRate ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status202Accepted;

            inbox.Record(new ReceivedWebhook(time.GetUtcNow(), request.Headers["X-TimeLedger-Event"], request.Headers["X-TimeLedger-Delivery"], valid, status, body));
            return Results.StatusCode(status);
        }).AllowAnonymous().ExcludeFromDescription(); // authenticated by its HMAC signature

        group.MapGet("/received", (MockPayrollProvider inbox) => inbox.Received)
            .RequireAuthorization(Policies.PayrollAdmin)
            .WithSummary("Webhooks the mock provider has received, newest first.");

        return app;
    }
}
