namespace CsAgent.Core.Agent;

public static partial class ToolDispatcher
{
    private static bool IsSafeCommand(string cmd, bool isWindows)
    {
        var lowerCmd = cmd.ToLowerInvariant();

        if (isWindows)
        {
            var windowsDangerous = new[]
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
            foreach (var pattern in windowsDangerous)
                if (lowerCmd.Contains(pattern)) return false;
        }
        else
        {
            var unixDangerous = new[]
            {
                "sudo ", "chmod", "shutdown", "reboot", "dd ", "mkfs",
                "/etc/", "/usr/bin/", "/bin/",
            };
            foreach (var pattern in unixDangerous)
                if (lowerCmd.Contains(pattern)) return false;
        }

        return true;
    }
}
