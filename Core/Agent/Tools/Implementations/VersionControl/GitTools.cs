using System.Diagnostics;
using System.Text;

namespace CsAgent.Core.Agent;

public static partial class ToolDispatcher
{
    private static async Task<string> GitStatusAsync(string path)
    {
        var full = Path.GetFullPath(path);
        if (!IsSafePath(full))
            return $"Error: git_status - Path '{full}' is not allowed. Only paths in the current working directory are permitted.";

        return await RunGitAsync(full, "status --short --branch");
    }
    private static async Task<string> GitDiffAsync(string path, bool staged)
    {
        var full = Path.GetFullPath(path);
        if (!IsSafePath(full))
            return $"Error: git_diff - Path '{full}' is not allowed. Only paths in the current working directory are permitted.";

        var args = staged ? "diff --cached" : "diff";
        return await RunGitAsync(full, args);
    }
    private static async Task<string> GitLogAsync(string path, int count)
    {
        var full = Path.GetFullPath(path);
        if (!IsSafePath(full))
            return $"Error: git_log - Path '{full}' is not allowed. Only paths in the current working directory are permitted.";

        if (count < 1) count = 1;
        if (count > 100) count = 100;
        return await RunGitAsync(full, $"log --oneline -n {count}");
    }
    private static async Task<string> GitBranchAsync(string path)
    {
        var full = Path.GetFullPath(path);
        if (!IsSafePath(full))
            return $"Error: git_branch - Path '{full}' is not allowed. Only paths in the current working directory are permitted.";

        return await RunGitAsync(full, "branch --list");
    }
    private static async Task<string> GitCommitAsync(string path, string message)
    {
        var full = Path.GetFullPath(path);
        if (!IsSafePath(full))
            return $"Error: git_commit - Path '{full}' is not allowed. Only paths in the current working directory are permitted.";

        if (string.IsNullOrWhiteSpace(message))
            return "Error: git_commit - 'message' argument is required.";

        // Stage all changes, then commit.
        var addResult = await RunGitAsync(full, "add -A");
        if (addResult.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
            return addResult;

        var commitResult = await RunGitAsync(full, $"commit -m \"{message.Replace("\"", "\\\"")}\"");
        return commitResult;
    }
    /// <summary>
    /// Runs a git command in the given working directory and returns its output.
    /// </summary>
    private static async Task<string> RunGitAsync(string workingDir, string gitArgs)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = gitArgs,
                    WorkingDirectory = workingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };

            proc.Start();
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            var waitTask = proc.WaitForExitAsync();

            if (await Task.WhenAny(waitTask, Task.Delay(ShellTimeoutMs)) != waitTask)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return "Error: git command timed out (60s).";
            }

            var output = ((await outTask) + (await errTask)).Trim();
            var prefix = proc.ExitCode == 0 ? $"OK (exit 0):\n" : $"Error (exit {proc.ExitCode}):\n";
            return string.IsNullOrWhiteSpace(output)
                ? prefix.TrimEnd()
                : prefix + output;
        }
        catch (Exception ex) { return $"Git error: {ex.Message}"; }
    }
}
