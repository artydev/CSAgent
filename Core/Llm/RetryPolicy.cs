namespace CsAgent.Core.Llm;

/// <summary>
/// Describes how transient API failures (HTTP 429) are retried.
/// </summary>
public sealed record RetryPolicy(
    int MaxAttempts = 6,          // total attempts including the first
    int BaseDelayMs = 1000,       // initial backoff delay
    double BackoffFactor = 2.0,   // multiplier applied after each retry
    int MaxDelayMs = 70000)      // cap on the backoff delay
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
        return this;
    }
}
