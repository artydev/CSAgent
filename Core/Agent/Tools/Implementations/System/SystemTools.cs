using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace CsAgent.Core.Agent;

public static partial class ToolDispatcher
{
    private static async Task<string> ListModelsAsync()
    {
        var apiKey = Environment.GetEnvironmentVariable("ALBERT_API_KEY") ?? "";
        if (string.IsNullOrEmpty(apiKey))
            return "Error: list_models — ALBERT_API_KEY environment variable is not set.";

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

            // ?status=true asks the Albert API to include real-time availability
            // information for each model in the response (additional "status" field
            // on each model object, e.g. "available", "unavailable", "degraded").
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://albert.api.etalab.gouv.fr/v1/models?status=true");

            // AOT-safe: TryAddWithoutValidation avoids any header-type reflection
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return $"Error: list_models — API returned {(int)response.StatusCode}: {body}";

            JsonNode? root;
            try { root = JsonNode.Parse(body); }
            catch { return $"Error: list_models — Could not parse response: {body}"; }

            var data = root?["data"]?.AsArray();
            if (data is null || data.Count == 0)
                return "No models available.";

            // AOT-safe: explicit foreach, no LINQ lambdas, JsonNode traversal only
            var sb = new StringBuilder();
            sb.AppendLine($"Available models ({data.Count}):");
            sb.AppendLine();

            foreach (var model in data)
            {
                var id = model?["id"]?.GetValue<string>() ?? "(unknown)";
                var type = model?["type"]?.GetValue<string>() ?? "";
                var ownedBy = model?["owned_by"]?.GetValue<string>() ?? "";
                var status = model?["status"]?.GetValue<string>() ?? "";

                // Status indicator: ✓ available, ✗ unavailable, ~ degraded, ? unknown
                var statusIcon = status switch
                {
                    "available" => "✓",
                    "unavailable" => "✗",
                    "degraded" => "~",
                    "" => " ",
                    _ => "?"
                };

                sb.Append($"  [{statusIcon}] {id}");
                if (!string.IsNullOrEmpty(type))
                    sb.Append($"  [{type}]");
                if (!string.IsNullOrEmpty(ownedBy))
                    sb.Append($"  ({ownedBy})");
                if (!string.IsNullOrEmpty(status))
                    sb.Append($"  — {status}");
                sb.AppendLine();

                var aliases = model?["aliases"]?.AsArray();
                if (aliases is { Count: > 0 })
                {
                    sb.Append("      aliases: ");
                    for (int i = 0; i < aliases.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        // Alias elements are plain strings — use JsonValue guard for AOT safety
                        sb.Append(aliases[i] is JsonValue av ? av.GetValue<string>() : aliases[i]?.ToJsonString() ?? "");
                    }
                    sb.AppendLine();
                }
            }

            return sb.ToString().TrimEnd();
        }
        catch (TaskCanceledException)
        {
            return "Error: list_models — request timed out after 15 s.";
        }
        catch (HttpRequestException ex)
        {
            return $"Error: list_models — HTTP error: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error: list_models — {ex.Message}";
        }
    }
    /// <summary>
    /// Reads the system clipboard using the appropriate platform command.
    /// AOT-safe: shells out to native clipboard tools, no P/Invoke, no NuGet.
    ///
    /// Platform commands:
    ///   Windows : powershell Get-Clipboard -Raw
    ///   macOS   : pbpaste
    ///   Linux   : xclip -selection clipboard -o
    ///             (falls back to xsel -b -o, then wl-paste for Wayland)
    /// </summary>
    private static async Task<string> ReadClipboardAsync(bool isWindows)
    {
        foreach (var (file, argList) in GetClipboardReadCandidates(isWindows))
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = file,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var a in argList) psi.ArgumentList.Add(a);

                using var proc = new Process { StartInfo = psi };
                proc.Start();

                var output = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();

                if (proc.ExitCode != 0) continue;

                var text = output.TrimEnd('\r', '\n');
                return string.IsNullOrEmpty(text)
                    ? "Clipboard is empty."
                    : $"Clipboard content:\n\n{text}";
            }
            catch (Exception) { /* tool not available, try next */ }
        }

        return "Error: read_clipboard — no clipboard tool available. " +
               "On Linux, install xclip (apt install xclip) or xsel, " +
               "or use a Wayland session with wl-clipboard.";
    }
    /// <summary>
    /// Writes text to the system clipboard using the appropriate platform command.
    /// Content is always passed via stdin to avoid any command-line quoting issues.
    /// AOT-safe: shells out to native clipboard tools, no P/Invoke, no NuGet.
    ///
    /// Platform commands:
    ///   Windows : powershell Set-Clipboard (via stdin pipeline)
    ///   macOS   : pbcopy
    ///   Linux   : xclip -selection clipboard
    ///             (falls back to xsel -b -i, then wl-copy for Wayland)
    /// </summary>
    private static async Task<string> WriteClipboardAsync(string clipContent, bool isWindows)
    {
        if (clipContent.Length > 1_000_000)
            return "Error: write_clipboard — content exceeds 1 MB limit.";

        foreach (var (file, argList) in GetClipboardWriteCandidates(isWindows))
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = file,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var a in argList) psi.ArgumentList.Add(a);

                using var proc = new Process { StartInfo = psi };
                proc.Start();

                await proc.StandardInput.WriteAsync(clipContent);
                proc.StandardInput.Close();
                await proc.WaitForExitAsync();

                if (proc.ExitCode != 0) continue;

                return $"OK: {clipContent.Length} character(s) written to clipboard.";
            }
            catch (Exception) { /* tool not available, try next */ }
        }

        return "Error: write_clipboard — no clipboard tool available. " +
               "On Linux, install xclip (apt install xclip) or xsel, " +
               "or use a Wayland session with wl-clipboard.";
    }
    /// <summary>
    /// Returns platform-ordered read candidates as (executable, argumentList) pairs.
    /// Using ArgumentList avoids all shell quoting issues — each element is passed
    /// verbatim to the OS without any escaping.
    /// </summary>
    private static IEnumerable<(string File, string[] Args)> GetClipboardReadCandidates(bool isWindows)
    {
        if (isWindows)
        {
            // -Command with separate ArgumentList entries: no inner-quote escaping needed
            yield return ("powershell", ["-NoProfile", "-NonInteractive", "-Command", "Get-Clipboard -Raw"]);
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return ("pbpaste", []);
        }
        else
        {
            // Linux: try X11 tools then Wayland
            yield return ("xclip", ["-selection", "clipboard", "-o"]);
            yield return ("xsel", ["-b", "-o"]);
            yield return ("wl-paste", []);
        }
    }
    /// <summary>
    /// Returns platform-ordered write candidates as (executable, argumentList) pairs.
    /// Content is always piped via stdin — no quoting of the content itself is needed.
    /// </summary>
    private static IEnumerable<(string File, string[] Args)> GetClipboardWriteCandidates(bool isWindows)
    {
        if (isWindows)
        {
            // $input is the PowerShell automatic variable for stdin pipeline input
            yield return ("powershell", ["-NoProfile", "-NonInteractive", "-Command", "$input | Set-Clipboard"]);
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return ("pbcopy", []);
        }
        else
        {
            // Linux: try X11 tools then Wayland
            yield return ("xclip", ["-selection", "clipboard"]);
            yield return ("xsel", ["-b", "-i"]);
            yield return ("wl-copy", []);
        }
    }
    private static string SwitchModel(string model, SwitchModelHandler? switchModel)
    {
        if (string.IsNullOrWhiteSpace(model))
            return "Error: switch_model - 'model' argument is required.";

        if (switchModel is null)
            return "Error: switch_model - model switching is not available in this context.";

        return switchModel(model.Trim());
    }
}
