namespace CsAgent.Core.Llm;

/// <summary>
/// Running total of the tokens the API reports for this process (the <c>usage</c> block of each answer).
/// A conversation is spread over many requests, so this is what shows how fast a quota is being used.
/// </summary>
public static class TokenMeter
{
    private static long _prompt, _completion;

    public static long Prompt => Interlocked.Read(ref _prompt);
    public static long Completion => Interlocked.Read(ref _completion);

    public static void Add(long prompt, long completion)
    {
        Interlocked.Add(ref _prompt, prompt);
        Interlocked.Add(ref _completion, completion);
    }

    /// <summary>For tests.</summary>
    internal static void Reset() { Interlocked.Exchange(ref _prompt, 0); Interlocked.Exchange(ref _completion, 0); }
}
