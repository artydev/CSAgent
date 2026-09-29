using System.Diagnostics;
using System.Text;

namespace CsAgent.Core.Agent;

public static partial class ToolDispatcher
{
    // ── excel_command / close_excel ─────────────────────────────────────────
    //
    // Live, interactive Excel automation. Unlike a one-shot script, this keeps
    // a single persistent powershell.exe process per session alive across
    // multiple tool calls, with $excel / $wb / $ws surviving between calls —
    // exactly the same pattern as run_terminal's TerminalSession.
    //
    // UserControl is genuinely load-bearing: restarting csagent.exe spawns a
    // fresh PowerShell process with no memory of the previous one, so Excel
    // (kept alive only via UserControl) must survive that disconnect on its
    // own. HealthCheckAndRepair() then re-attaches and reuses whatever
    // workbook is already active, rather than piling up a new blank one on
    // every restart.

    private static readonly Dictionary<string, ExcelSession> ExcelSessions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ExcelLock = new();

    private static async Task<string> ExcelCommandAsync(string command, string session, int timeoutMs, bool isWindows)
    {
        try
        {
            if (!isWindows)
                return "Error: excel_command - only supported on Windows (Excel COM automation).";

            if (string.IsNullOrWhiteSpace(command))
                return "Error: excel_command - 'command' argument is required.";

            if (!IsSafeCommand(command, isWindows))
                return $"Error: excel_command - command contains potentially dangerous operations and is not allowed.";

            if (timeoutMs < 1) timeoutMs = 1;
            if (timeoutMs > 300_000) timeoutMs = 300_000;

            ExcelSession sess;
            lock (ExcelLock)
            {
                if (!ExcelSessions.TryGetValue(session, out sess!))
                {
                    sess = new ExcelSession(session);
                    ExcelSessions[session] = sess;
                }
            }

            return await sess.RunAsync(command, timeoutMs);
        }
        catch (Exception ex) { return $"Error: excel_command — {ex.Message}"; }
    }

    private static string CloseExcel(string session)
    {
        try
        {
            lock (ExcelLock)
            {
                if (ExcelSessions.TryGetValue(session, out var sess))
                {
                    sess.Dispose();
                    ExcelSessions.Remove(session);
                    return $"OK: closed Excel session '{session}'. Excel itself was left running (UserControl mode) — quit it explicitly with a '$excel.Quit()' command first if you want it to close too.";
                }
                return $"OK: no active Excel session '{session}' to close.";
            }
        }
        catch (Exception ex) { return $"Error: close_excel — {ex.Message}"; }
    }

    /// <summary>
    /// A persistent, interactive PowerShell process driving Excel via COM.
    /// $excel / $wb / $ws live inside this process's memory and remain valid
    /// across every excel_command call routed to this session — no re-attach
    /// needed between calls, since it's the same living process the whole
    /// time. HealthCheckAndRepair() runs before every command to detect and
    /// transparently recover from a dropped COM link, and to reattach to
    /// whichever workbook is already active rather than creating a new one.
    /// </summary>
    private sealed class ExcelSession : IDisposable
    {
        private readonly string _id;
        private readonly Process _proc;
        private readonly StringBuilder _output = new();
        private readonly object _lock = new();
        private bool _disposed;

        public ExcelSession(string id)
        {
            _id = id;

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NoLogo -NonInteractive",
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
                if (_output.Length > 512_000)
                    _output.Remove(0, _output.Length - 512_000);
            }
        }

        public async Task<string> RunAsync(string command, int timeoutMs)
        {
            if (_disposed || _proc.HasExited)
                return $"Error: excel_command - session '{_id}' is no longer running. Send another command to start a new one.";

            var toSend = HealthCheckAndRepair() + "; " + command;

            // Base64-encode the whole thing and decode+run it atomically via
            // Invoke-Expression on ONE physical stdin line. This matters
            // because 'command' may itself contain embedded newlines (a
            // multi-line array literal, a for-loop) — sending that text
            // directly would arrive at the child process as several
            // separate physical lines, which -NonInteractive PowerShell
            // parses through its interactive continuation-prompt logic
            // (visible as '>>' in raw output). That path has proven
            // unreliable for reliably executing loop bodies against COM
            // objects: no error surfaces, but writes silently don't land.
            // Invoke-Expression runs in the current scope, so $excel/$wb/$ws
            // set here are still visible to later calls, same as before.
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(toSend));

            int startPos;
            lock (_lock) { startPos = _output.Length; }

            var marker = $"__CSAGENT_EXCEL_DONE_{Guid.NewGuid():N}__";
            var line = $"$__csagentCmd = [System.Text.Encoding]::Unicode.GetString([System.Convert]::FromBase64String('{encoded}')); Invoke-Expression $__csagentCmd; Write-Output '{marker}'";

            await _proc.StandardInput.WriteLineAsync(line);
            await _proc.StandardInput.FlushAsync();

            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                string current;
                lock (_lock) { current = _output.ToString(); }

                if (current.Contains(marker))
                {
                    lock (_lock)
                    {
                        var newText = _output.ToString(startPos, _output.Length - startPos);
                        newText = newText.Replace(marker, "").Trim();
                        return string.IsNullOrWhiteSpace(newText)
                            ? $"OK (excel session '{_id}'): (no output)"
                            : $"OK (excel session '{_id}'):\n{newText}";
                    }
                }

                await Task.Delay(50);
            }

            return $"Error: excel_command - command timed out after {timeoutMs} ms in session '{_id}'. The session is still running; you can send another command or close it with close_excel.";
        }

        /// <summary>
        /// Runs before every user command: cheaply checks whether $excel is
        /// still alive (a dropped/crashed COM link throws on property
        /// access), and transparently repairs the session if not — either
        /// re-attaching to a running Excel or starting a new one. Reuses the
        /// existing active workbook/worksheet when one is already open
        /// (important across csagent process restarts, since Excel itself —
        /// kept alive via UserControl — survives even though a fresh
        /// PowerShell process has no memory of $wb/$ws from before); only
        /// creates a new blank workbook when none exist at all.
        /// </summary>
        private static string HealthCheckAndRepair() => string.Join("; ", new[]
        {
            "$needsBootstrap = $true",
            "if ($excel) { try { $null = $excel.Visible; $needsBootstrap = $false } catch { $needsBootstrap = $true } }",
            "if ($needsBootstrap) {" +
                " $reused = $true;" +
                " try { $excel = [Runtime.InteropServices.Marshal]::GetActiveObject('Excel.Application') }" +
                " catch { $reused = $false; $excel = New-Object -ComObject Excel.Application };" +
                " if (-not $reused) { $excel.UserControl = $true };" +
                " $excel.Visible = $true;" +
                " $excel.DisplayAlerts = $false;" +
                " $excel.AskToUpdateLinks = $false;" +
                " if ($excel.Workbooks.Count -gt 0) {" +
                    " $wb = $excel.ActiveWorkbook;" +
                    " if (-not $wb) { $wb = $excel.Workbooks.Item(1) }" +
                " } else {" +
                    " $wb = $excel.Workbooks.Add()" +
                " };" +
                " $ws = $wb.ActiveSheet;" +
                " if (-not $ws) { $ws = $wb.Worksheets.Item(1) }" +
            " }"
        });

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!_proc.HasExited)
                {
                    // Only end the PowerShell driver — Excel itself (UserControl
                    // mode) is deliberately left running for the user.
                    try { _proc.StandardInput.WriteLine("exit"); _proc.StandardInput.Flush(); } catch { }
                    try { _proc.Kill(entireProcessTree: true); } catch { }
                }
                _proc.Dispose();
            }
            catch { /* best effort */ }
        }
    }
}