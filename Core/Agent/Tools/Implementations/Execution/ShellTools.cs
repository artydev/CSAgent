using System.Diagnostics;
using System.Text;

namespace CsAgent.Core.Agent;

public static partial class ToolDispatcher
{
    private static async Task<string> RunShellAsync(string cmd, bool isWindows)
    {
        try
        {
            if (!IsSafeCommand(cmd, isWindows))
                return $"Error: sh - Command '{cmd}' contains potentially dangerous operations and is not allowed.";

            var (file, shellArgs) = isWindows
                ? ("cmd.exe", $"/d /s /c \"{cmd}\"")
                : ("/bin/sh", $"-c \"{cmd.Replace("\"", "\\\"")}\"");

            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = shellArgs,
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
                return "Error: command timed out (60s).";
            }

            var output = ((await outTask) + (await errTask)).Trim();
            var prefix = proc.ExitCode == 0 ? $"OK (exit 0):\n" : $"Error (exit {proc.ExitCode}):\n";
            return string.IsNullOrWhiteSpace(output)
                ? prefix.TrimEnd()
                : prefix + output;
        }
        catch (Exception ex) { return $"Shell error: {ex.Message}"; }
    }
    private static readonly Dictionary<string, TerminalSession> TerminalSessions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object TerminalLock = new();
    private static async Task<string> RunTerminalAsync(string cmd, string session, int timeoutMs, bool isWindows)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cmd))
                return "Error: run_terminal - 'cmd' argument is required.";

            if (!IsSafeCommand(cmd, isWindows))
                return $"Error: run_terminal - Command '{cmd}' contains potentially dangerous operations and is not allowed.";

            if (timeoutMs < 1) timeoutMs = 1;
            if (timeoutMs > 300_000) timeoutMs = 300_000;

            TerminalSession term;
            lock (TerminalLock)
            {
                if (!TerminalSessions.TryGetValue(session, out term!))
                {
                    term = new TerminalSession(session, isWindows);
                    TerminalSessions[session] = term;
                }
            }

            return await term.RunAsync(cmd, timeoutMs);
        }
        catch (Exception ex) { return $"Error: run_terminal — {ex.Message}"; }
    }
    private static string CloseTerminal(string session)
    {
        try
        {
            lock (TerminalLock)
            {
                if (TerminalSessions.TryGetValue(session, out var term))
                {
                    term.Dispose();
                    TerminalSessions.Remove(session);
                    return $"OK: closed terminal session '{session}'.";
                }
                return $"OK: no active terminal session '{session}' to close.";
            }
        }
        catch (Exception ex) { return $"Error: close_terminal — {ex.Message}"; }
    }
    /// <summary>
    /// A persistent interactive shell process. Commands are written to its stdin
    /// and output is read asynchronously, preserving state (cwd, env) across calls.
    /// </summary>
    private sealed class TerminalSession : IDisposable
    {
        private readonly string _id;
        private readonly bool _isWindows;
        private readonly Process _proc;
        private readonly StringBuilder _output = new();
        private readonly object _lock = new();
        private bool _disposed;

        public TerminalSession(string id, bool isWindows)
        {
            _id = id;
            _isWindows = isWindows;

            var psi = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/bash",
                Arguments = isWindows ? "/Q" : "--norc --noprofile -i",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            _proc = new Process { StartInfo = psi };
            _proc.Start();

            // Continuously drain stdout/stderr into the shared buffer.
            _proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Append(e.Data); };
            _proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Append(e.Data); };
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
        }

        private void Append(string line)
        {
            lock (_lock)
            {
                _output.AppendLine(line);
                // Cap the buffer to avoid unbounded growth.
                if (_output.Length > 512_000)
                    _output.Remove(0, _output.Length - 512_000);
            }
        }

        public async Task<string> RunAsync(string cmd, int timeoutMs)
        {
            if (_disposed || _proc.HasExited)
                return $"Error: run_terminal - session '{_id}' is no longer running. Start a new session.";

            // Snapshot the current buffer position so we only return new output.
            int startPos;
            lock (_lock) { startPos = _output.Length; }

            // Write the command followed by a unique sentinel marker so we know
            // when the command has finished producing output.
            var marker = $"__CSAGENT_DONE_{Guid.NewGuid():N}__";
            var line = _isWindows
                ? $"{cmd} & echo {marker}"
                : $"{cmd}; echo {marker}";

            await _proc.StandardInput.WriteLineAsync(line);
            await _proc.StandardInput.FlushAsync();

            // Wait for the marker to appear in the output, or until timeout.
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                string current;
                lock (_lock) { current = _output.ToString(); }

                if (current.Contains(marker))
                {
                    // Strip the marker line and return everything since startPos.
                    lock (_lock)
                    {
                        var newText = _output.ToString(startPos, _output.Length - startPos);
                        newText = newText.Replace(marker, "").Trim();
                        return string.IsNullOrWhiteSpace(newText)
                            ? $"OK (session '{_id}'): (no output)"
                            : $"OK (session '{_id}'):\n{newText}";
                    }
                }

                await Task.Delay(50);
            }

            return $"Error: run_terminal - command timed out after {timeoutMs} ms in session '{_id}'. The session is still running; you can send another command or close it with close_terminal.";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!_proc.HasExited)
                {
                    try { _proc.StandardInput.WriteLine(_isWindows ? "exit" : "exit"); _proc.StandardInput.Flush(); } catch { }
                    try { _proc.Kill(entireProcessTree: true); } catch { }
                }
                _proc.Dispose();
            }
            catch { /* best effort */ }
        }
    }
}
