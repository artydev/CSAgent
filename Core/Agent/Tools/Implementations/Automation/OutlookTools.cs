using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace CsAgent.Core.Agent;

public static partial class ToolDispatcher
{
    // ── send_email ───────────────────────────────────────────────────────────
    //
    // One-shot Outlook automation via COM, invoked through powershell.exe
    // -EncodedCommand (base64 UTF-16LE). The generated PowerShell mirrors a
    // tested, working standalone script as closely as possible — same
    // Test-OutlookAvailable pre-check, same permissive attachment handling
    // (any path the file system will accept, not restricted to the working
    // directory), same Test-Path existence check per attachment.

    private static async Task<string> SendEmailAsync(
        string to, string? cc, string? bcc, string subject,
        string? body, string? bodyHtml, JsonArray? attachments,
        bool send, bool saveToDrafts, bool isWindows)
    {
        if (!isWindows)
            return "Error: send_email - only supported on Windows (Outlook COM automation).";

        if (string.IsNullOrWhiteSpace(to))
            return "Error: send_email - 'to' argument is required.";

        if (string.IsNullOrWhiteSpace(subject))
            return "Error: send_email - 'subject' argument is required.";

        var attachmentPaths = new List<string>();
        if (attachments is { Count: > 0 })
        {
            foreach (var item in attachments)
            {
                if (item is not JsonValue val) continue;
                var raw = val.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(raw))
                    attachmentPaths.Add(raw.Trim());
            }
        }

        try
        {
            var script = BuildOutlookScript(to, cc, bcc, subject, body, bodyHtml, attachmentPaths, send, saveToDrafts);
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(encoded);

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            var waitTask = proc.WaitForExitAsync();

            if (await Task.WhenAny(waitTask, Task.Delay(60_000)) != waitTask)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return "Error: send_email — timed out after 60 s.";
            }

            var output = (await outTask).Trim();
            var error = (await errTask).Trim();

            if (proc.ExitCode != 0)
                return $"Error: send_email — PowerShell exited with code {proc.ExitCode}.\n{error}\n{output}".Trim();

            return string.IsNullOrWhiteSpace(output) ? "OK: email processed." : output;
        }
        catch (Exception ex) { return $"Error: send_email — {ex.Message}"; }
    }

    /// <summary>
    /// Generates PowerShell mirroring the tested standalone script: a
    /// Test-OutlookAvailable pre-check, a single Outlook.Application
    /// instance, per-attachment existence check via Test-Path, and the
    /// same Send / SaveToDrafts / Display branching.
    /// </summary>
    private static string BuildOutlookScript(
        string to, string? cc, string? bcc, string subject,
        string? body, string? bodyHtml, List<string> attachmentPaths,
        bool send, bool saveToDrafts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");

        sb.AppendLine("function Test-OutlookAvailable {");
        sb.AppendLine("    try { $null = New-Object -ComObject Outlook.Application; return $true }");
        sb.AppendLine("    catch { return $false }");
        sb.AppendLine("}");

        sb.AppendLine("if (-not (Test-OutlookAvailable)) {");
        sb.AppendLine("    throw \"Outlook is not available on this machine. Check it is installed and configured.\"");
        sb.AppendLine("}");

        sb.AppendLine("$outlook = New-Object -ComObject Outlook.Application");
        sb.AppendLine("try {");
        sb.AppendLine("    $mail = $outlook.CreateItem(0)");
        sb.AppendLine($"    $mail.To = '{Escape(to)}'");

        if (!string.IsNullOrWhiteSpace(cc))
            sb.AppendLine($"    $mail.CC = '{Escape(cc)}'");
        if (!string.IsNullOrWhiteSpace(bcc))
            sb.AppendLine($"    $mail.BCC = '{Escape(bcc)}'");

        sb.AppendLine($"    $mail.Subject = '{Escape(subject)}'");

        if (!string.IsNullOrWhiteSpace(bodyHtml))
            sb.AppendLine($"    $mail.HTMLBody = '{Escape(bodyHtml)}'");
        else if (!string.IsNullOrWhiteSpace(body))
            sb.AppendLine($"    $mail.Body = '{Escape(body)}'");

        foreach (var path in attachmentPaths)
        {
            var escaped = Escape(path);
            sb.AppendLine($"    if (-not (Test-Path -LiteralPath '{escaped}')) {{ throw \"Attachment not found: {escaped}\" }}");
            sb.AppendLine($"    $mail.Attachments.Add('{escaped}') | Out-Null");
        }

        if (send)
        {
            sb.AppendLine("    $mail.Send()");
            sb.AppendLine("    Write-Output 'OK: email sent.'");
        }
        else if (saveToDrafts)
        {
            sb.AppendLine("    $mail.Save()");
            sb.AppendLine("    Write-Output 'OK: email saved to drafts.'");
        }
        else
        {
            sb.AppendLine("    $mail.Display()");
            sb.AppendLine("    Write-Output 'OK: email displayed as draft for review (not sent).'");
        }

        sb.AppendLine("} finally {");
        sb.AppendLine("    if ($mail) { [System.Runtime.InteropServices.Marshal]::ReleaseComObject($mail) | Out-Null }");
        sb.AppendLine("    if ($outlook) { [System.Runtime.InteropServices.Marshal]::ReleaseComObject($outlook) | Out-Null }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // PowerShell single-quoted strings only need '' for literal quotes, and
    // never interpolate $variables — safe even if subject/body/paths happen
    // to contain a literal '$'.
    private static string Escape(string s) => s.Replace("'", "''");
}