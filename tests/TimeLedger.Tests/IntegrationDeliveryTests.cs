using TimeLedger.Domain;
using TimeLedger.Domain.Entities;
using TimeLedger.Domain.Integration;

namespace TimeLedger.Tests;

public class IntegrationDeliveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 1, 0, 0, TimeSpan.Zero);
    private const string Secret = "whsec_test";
    private const string Body = """{"type":"payroll.period_closed"}""";

    [Fact]
    public void Signature_round_trips()
    {
        var header = WebhookSigner.Sign(Secret, 1_800_000_000, Body);

        Assert.True(WebhookSigner.Verify(Secret, header, Body, 1_800_000_010));
    }

    [Fact]
    public void Signature_fails_for_tampered_body_wrong_secret_or_stale_timestamp()
    {
        var header = WebhookSigner.Sign(Secret, 1_800_000_000, Body);

        Assert.False(WebhookSigner.Verify(Secret, header, Body + " ", 1_800_000_000));
        Assert.False(WebhookSigner.Verify("other", header, Body, 1_800_000_000));
        Assert.False(WebhookSigner.Verify(Secret, header, Body, 1_800_000_000 + 301));
        Assert.False(WebhookSigner.Verify(Secret, "garbage", Body, 1_800_000_000));
        Assert.False(WebhookSigner.Verify(Secret, null, Body, 1_800_000_000));
    }

    [Theory]
    [InlineData(1, 0.0, 2.5)]
    [InlineData(1, 0.5, 3.75)]
    [InlineData(3, 0.0, 10)]
    [InlineData(20, 0.0, 900)] // capped at 30 min, half of which is the floor
    public void Backoff_grows_exponentially_with_equal_jitter(int attempts, double jitter, double expectedSeconds)
    {
        var delay = new RetryPolicy().NextDelay(attempts, jitter);

        Assert.Equal(expectedSeconds, Math.Round(delay.TotalSeconds, 3));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(422, false)]
    public void Only_transient_failures_are_retried(int? status, bool retryable)
    {
        Assert.Equal(retryable, RetryPolicy.IsRetryable(status));
    }

    [Fact]
    public void Transient_failure_schedules_a_retry()
    {
        var d = WebhookDelivery.Create(Guid.NewGuid(), Guid.NewGuid(), Now);

        d.MarkFailed(503, "Service Unavailable", Now, new RetryPolicy(), jitter: 0);

        Assert.Equal(DeliveryStatus.Pending, d.Status);
        Assert.Equal(1, d.Attempts);
        Assert.Equal(Now.AddSeconds(2.5), d.NextAttemptAt);
    }

    [Fact]
    public void Permanent_failure_dead_letters_immediately()
    {
        var d = WebhookDelivery.Create(Guid.NewGuid(), Guid.NewGuid(), Now);

        d.MarkFailed(401, "Unauthorized", Now, new RetryPolicy(), jitter: 0);

        Assert.Equal(DeliveryStatus.DeadLettered, d.Status);
    }

    [Fact]
    public void Exhausted_retries_dead_letter_and_can_be_requeued()
    {
        var policy = new RetryPolicy { MaxAttempts = 3 };
        var d = WebhookDelivery.Create(Guid.NewGuid(), Guid.NewGuid(), Now);

        for (var i = 0; i < 3; i++)
        {
            d.MarkFailed(null, "timeout", Now, policy, jitter: 0.5);
        }

        Assert.Equal(DeliveryStatus.DeadLettered, d.Status);

        d.Requeue(Now);
        Assert.Equal(DeliveryStatus.Pending, d.Status);
        Assert.Equal(0, d.Attempts);
        Assert.Throws<DomainException>(() => d.Requeue(Now));
    }

    [Fact]
    public void Long_errors_are_truncated()
    {
        var d = WebhookDelivery.Create(Guid.NewGuid(), Guid.NewGuid(), Now);

        d.MarkFailed(500, new string('x', 5000), Now, new RetryPolicy(), jitter: 0);

        Assert.Equal(WebhookDelivery.MaxErrorLength, d.LastError!.Length);
    }

    [Fact]
    public void Connection_subscription_requires_enabled_and_listed_event()
    {
        var c = new IntegrationConnection { Name = "x", EndpointUrl = "https://example.com", Secret = Secret, EventTypes = [EventTypes.PayrollPeriodClosed] };

        Assert.True(c.IsSubscribedTo(EventTypes.PayrollPeriodClosed));
        Assert.False(c.IsSubscribedTo(EventTypes.LeaveApproved));

        c.IsEnabled = false;
        Assert.False(c.IsSubscribedTo(EventTypes.PayrollPeriodClosed));
    }
}
