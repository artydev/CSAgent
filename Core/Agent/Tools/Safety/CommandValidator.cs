namespace CsAgent.Core.Agent;

public static partial class ToolDispatcher
{
    private static readonly string[] WindowsDangerous =
    {
        "format ", "format.", "del /f", "del /s", "rd /s", "rmdir /s",
        "reg delete", "reg add", "reg import",
        "net user", "net localgroup", "net share", "net use",
        "takeown", "icacls", "cacls",
        "attrib -r -s -h", "bcdedit", "diskpart",
        "powershell start-process -verb runas", "runas",
        "shutdown", "reboot",
        "\\windows\\system32\\", "\\windows\\system\\", "\\program files\\",
    };

    private static readonly string[] UnixDangerous =
    {
        "sudo ", "chmod", "shutdown", "reboot", "dd ", "mkfs",
        "/etc/", "/usr/bin/", "/bin/",
    };

    private static bool IsSafeCommand(string cmd, bool isWindows) => FindDangerousPattern(cmd, isWindows) is null;

    /// <summary>The first blocked pattern found in the command, or null when it is allowed.</summary>
    /// <remarks>
    /// A pattern that starts with a letter only counts at the start of a word: <c>format </c> blocks
    /// <c>format c:</c> but not <c>.NumberFormat = "0.00"</c> (Excel), and <c>dd </c> blocks <c>dd if=…</c>
    /// but not <c>git add file</c>. Patterns that start with a path separator are plain substrings.
    /// </remarks>
    internal static string? FindDangerousPattern(string cmd, bool isWindows)
    {
        var lower = cmd.ToLowerInvariant();
        foreach (var pattern in isWindows ? WindowsDangerous : UnixDangerous)
        {
            int from = 0;
            while (from <= lower.Length)
            {
                int i = lower.IndexOf(pattern, from, StringComparison.Ordinal);
                if (i < 0) break;
                bool wordPattern = char.IsLetter(pattern[0]);
                if (!wordPattern || i == 0 || !IsWordChar(lower[i - 1])) return pattern;
                from = i + 1;
            }
        }
        return null;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '$' or '-';

    // The message names the pattern, so the model (and the user) can tell what to change instead of guessing.
    private static string DangerousMessage(string tool, string? pattern) =>
        $"contains the blocked pattern '{pattern?.Trim()}' (potentially dangerous operations) and is not allowed. " +
        "Rewrite the command without it; the filter looks at the text of the command, including inside quotes.";
}
