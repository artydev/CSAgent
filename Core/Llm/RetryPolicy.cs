namespace CsAgent.Core.Llm;

/// <summary>
/// Describes how transient API failures are retried: HTTP 429 (rate limit) and lost connections.
/// </summary>
public sealed record RetryPolicy(
    int MaxAttempts = 6,          // total attempts including the first
    int BaseDelayMs = 1000,       // initial backoff delay
    double BackoffFactor = 2.0,   // multiplier applied after each retry
    int MaxDelayMs = 70000,      // cap on the backoff delay
    int ConnectionRetries = 2,   // extra attempts when the connection fails before any answer (not for 429)
    int ConnectionDelayMs = 2000) // wait before the 1st such retry, doubled for the 2nd
{
    /// <summary>The default retry policy (6 attempts, 1s base delay, 2x backoff, 70s cap).</summary>
    public static RetryPolicy Default { get; } = new();

    /// <summary>
    /// Validates the policy values, throwing if any are out of range.
    /// Returns <c>this</c> for chaining.
    /// </summary>
    public RetryPolicy Validate()
    {
        if (MaxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), "MaxAttempts must be >= 1");
        if (BaseDelayMs < 0)
            throw new ArgumentOutOfRangeException(nameof(BaseDelayMs), "BaseDelayMs must be >= 0");
        if (BackoffFactor < 1.0)
            throw new ArgumentOutOfRangeException(nameof(BackoffFactor), "BackoffFactor must be >= 1.0");
        if (MaxDelayMs < BaseDelayMs)
            throw new ArgumentOutOfRangeException(nameof(MaxDelayMs), "MaxDelayMs must be >= BaseDelayMs");
        if (ConnectionRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(ConnectionRetries), "ConnectionRetries must be >= 0");
        if (ConnectionDelayMs < 0)
            throw new ArgumentOutOfRangeException(nameof(ConnectionDelayMs), "ConnectionDelayMs must be >= 0");
        return this;
    }
}
