namespace TimeLedger.Domain.Integration;

/// <summary>Exponential backoff with "equal jitter", capped, followed by dead-lettering.</summary>
public sealed class RetryPolicy
{
    public int MaxAttempts { get; init; } = 8;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(30);

    public bool ShouldDeadLetter(int attemptsMade) => attemptsMade >= MaxAttempts;

    /// <param name="attemptsMade">Attempts already made (1 after the first failure).</param>
    /// <param name="jitter">A random value in [0, 1).</param>
    public TimeSpan NextDelay(int attemptsMade, double jitter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptsMade, 1);
        if (jitter is < 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(jitter), jitter, "Jitter must be in [0, 1).");
        }

        var exponential = BaseDelay.TotalSeconds * Math.Pow(2, attemptsMade - 1);
        var capped = Math.Min(exponential, MaxDelay.TotalSeconds);
        return TimeSpan.FromSeconds((capped / 2) + (capped / 2 * jitter));
    }

    /// <summary>
    /// Network errors (no status), 408, 429 and 5xx are worth retrying. Other 4xx mean the request itself
    /// is wrong (bad auth, bad payload), so retrying would only repeat the failure.
    /// </summary>
    public static bool IsRetryable(int? statusCode) => statusCode is null or 408 or 429 or >= 500;
}
