namespace CsAgent.Core.Llm;

/// <summary>
/// What to do when a model's DAILY quota is used up: carry on with another model instead of stopping.
/// <list type="bullet">
/// <item>The model to switch to is <c>CSAGENT_FALLBACK_MODEL</c> when set (<c>off</c> turns the switch off);
/// otherwise the code model (deepseek-v4-flash by default), and only on the default Albert endpoint, because
/// that name means nothing on another server.</item>
/// <item>A model whose daily quota is used up is remembered for an hour, so the following turns go straight
/// to the other model instead of trying the first one again and being refused again.</item>
/// </list>
/// </summary>
public static class QuotaFallback
{
    public const string EnvName = "CSAGENT_FALLBACK_MODEL";

    private static readonly Dictionary<string, DateTime> _exhausted = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    /// <summary>How long a model stays marked (the clock is a seam for tests).</summary>
    public static TimeSpan Memory { get; set; } = TimeSpan.FromHours(1);
    internal static Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    public static void MarkExhausted(string model)
    {
        lock (_lock) _exhausted[model] = Now();
    }

    public static bool IsExhausted(string model)
    {
        lock (_lock)
        {
            if (!_exhausted.TryGetValue(model, out var at)) return false;
            if (Now() - at < Memory) return true;
            _exhausted.Remove(model);
            return false;
        }
    }

    /// <summary>
    /// The model to carry on with when <paramref name="current"/> cannot be used any more, or null when
    /// there is none (switch turned off, same model, unknown endpoint, or the target is used up as well).
    /// </summary>
    public static string? Target(string current, string endpoint)
    {
        string target;
        var env = Environment.GetEnvironmentVariable(EnvName)?.Trim();
        if (!string.IsNullOrEmpty(env))
        {
            if (env.Equals("off", StringComparison.OrdinalIgnoreCase) || env.Equals("none", StringComparison.OrdinalIgnoreCase))
                return null;
            target = env;
        }
        else
        {
            if (!endpoint.TrimEnd('/').Equals(LlmSettings.Endpoint, StringComparison.OrdinalIgnoreCase)) return null;
            target = LlmConfig.CodeModel;
        }

        if (target.Equals(current, StringComparison.OrdinalIgnoreCase) || IsExhausted(target)) return null;
        return target;
    }

    /// <summary>For tests.</summary>
    internal static void Reset()
    {
        lock (_lock) _exhausted.Clear();
        Memory = TimeSpan.FromHours(1);
        Now = () => DateTime.UtcNow;
    }
}
