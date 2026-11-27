namespace CsAgent.Core.Memory;

/// <summary>
/// Whether the memory folder is written. <c>--no-save</c> turns it off: the conversation, the step memory,
/// the lessons and the session summary are then read (an existing session is still resumed) but never written.
/// Set once at start-up from the command line, like <see cref="CsAgent.Core.Agent.ContextBudget"/>.
/// </summary>
public static class SessionPersistence
{
    public static bool Enabled { get; private set; } = true;

    public static void Configure(bool enabled) => Enabled = enabled;

    /// <summary>For tests.</summary>
    internal static void Reset() => Enabled = true;
}
