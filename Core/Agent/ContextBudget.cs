namespace CsAgent.Core.Agent;

/// <summary>
/// How much conversation is sent to the model at each step. Every step re-sends the whole history,
/// so this is what an input-token quota is spent on.
/// <list type="bullet">
/// <item><see cref="MaxChars"/>: above this size the oldest messages are dropped (<c>--context-chars</c>).</item>
/// <item><see cref="Compact"/>: tool results older than the last <see cref="KeepRecentToolResults"/> are cut
/// to <see cref="OldToolResultChars"/> characters (<c>--no-compact</c> turns it off). A file read ten steps ago
/// is rarely needed in full, and the agent can read it again.</item>
/// </list>
/// Set once at start-up from the command line, like the model settings.
/// </summary>
public static class ContextBudget
{
    public const int DefaultMaxChars = 96_000;
    public const int MinMaxChars = 8_000;
    public const int KeepRecentToolResults = 6;
    public const int OldToolResultChars = 1_500;
    /// <summary>Images kept in the history: the newest multimodal message. Older images become a text note (base64 is huge and never evicted by TrimHistory).</summary>
    public const int KeepImages = 1;

    /// <summary>Messages kept verbatim after a compression (<c>--compress-keep</c> is not needed: fixed).</summary>
    public const int KeepRecentMessages = 12;

    public static int MaxChars { get; private set; } = DefaultMaxChars;
    /// <summary>Above this size the oldest messages become one recap (aichat compress_threshold). 0 = two thirds of MaxChars.</summary>
    public static int CompressChars { get; private set; } = DefaultMaxChars * 2 / 3;
    public static bool CompressEnabled { get; private set; } = true;

    public static void ConfigureCompression(int chars, bool enabled)
    {
        CompressEnabled = enabled;
        // Must stay below MaxChars, otherwise TrimHistory would drop messages before they are summarised.
        CompressChars = Math.Min(chars > 0 ? chars : MaxChars * 2 / 3, MaxChars - 1_000);
    }
    public static bool Compact { get; private set; } = true;

    public static void Configure(int maxChars, bool compact)
    {
        MaxChars = Math.Max(MinMaxChars, maxChars);
        Compact = compact;
    }

    /// <summary>For tests.</summary>
    internal static void Reset() { Configure(DefaultMaxChars, true); ConfigureCompression(0, true); }
}
