namespace CsAgent.Core.Llm;

/// <summary>Which limit an HTTP 429 answer is about.</summary>
public enum RateLimitKind
{
    /// <summary>Not recognised: treated as a passing limit (retried with backoff).</summary>
    Other,
    /// <summary>A per-minute limit: it clears by itself within about a minute.</summary>
    Minute,
    /// <summary>A per-day limit: retrying is useless until it resets.</summary>
    Day,
}

/// <summary>An API answer of 429 that was not (or could not be) retried away.</summary>
public sealed class RateLimitException : HttpRequestException
{
    public RateLimitKind Kind { get; }
    public RateLimitException(string message, RateLimitKind kind) : base(message) { Kind = kind; }
}

public static class RateLimit
{
    /// <summary>
    /// Reads the kind of limit out of a 429 body, for example
    /// <c>{"detail":"1280000 input tokens per day exceeded (remaining: 0)."}</c>. A daily limit wins when
    /// both words appear: it is the one that makes retrying pointless.
    /// </summary>
    public static RateLimitKind Classify(string? body)
    {
        if (string.IsNullOrEmpty(body)) return RateLimitKind.Other;
        var s = body.ToLowerInvariant();
        if (s.Contains("per day") || s.Contains("per_day") || s.Contains("daily") || s.Contains("(tpd)") || s.Contains("(rpd)"))
            return RateLimitKind.Day;
        if (s.Contains("per minute") || s.Contains("per min") || s.Contains("per_minute") || s.Contains("(tpm)") || s.Contains("(rpm)"))
            return RateLimitKind.Minute;
        return RateLimitKind.Other;
    }
}
