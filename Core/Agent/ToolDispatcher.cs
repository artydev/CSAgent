using System.Text.Json.Nodes;

namespace CsAgent.Core.Agent;

/// <summary>
/// Pure tool execution logic — no observer, no agent loop.
/// Routing only; implementations live in Tools/Implementations/*.
/// Safety checks live in Tools/Safety/*.
/// </summary>
public static partial class ToolDispatcher
{
    /// <summary>
    /// Delegate used by the switch_model tool to change the active model at runtime.
    /// Returns a human-readable confirmation/error message.
    /// </summary>
    public delegate string SwitchModelHandler(string model);
    /// <summary>
    /// Dispatch a tool call by name with the given JSON arguments.
    /// </summary>
    /// <param name="name">The tool name.</param>
    /// <param name="argsJson">JSON string of the tool arguments.</param>
    /// <param name="isWindows">Whether the host OS is Windows.</param>
    /// <param name="switchModel">Optional callback invoked by the switch_model tool.</param>
    public static async Task<string> DispatchAsync(
        string name,
        string argsJson,
        bool isWindows,
        SwitchModelHandler? switchModel = null)
    {
        try
        {
            var args = JsonNode.Parse(argsJson) ?? new JsonObject();
            return name switch
            {
                "write_file" => WriteFile(
                    args["path"]!.GetValue<string>(),
                    args["content"]!.GetValue<string>()),

                "read_file" => ReadFile(
                    args["path"]!.GetValue<string>()),

                "read_json" => ReadJson(
                    args["path"]!.GetValue<string>(),
                    args["query"]?.GetValue<string>()),

                "list_dir" => ListDir(
                    args["path"]?.GetValue<string>() ?? ".",
                    args["recursive"]?.GetValue<bool>() ?? false),

                "tree" => Tree(
                    args["path"]?.GetValue<string>() ?? ".",
                    args["depth"]?.GetValue<int>() ?? -1),

                "search_files" => SearchFiles(
                    args["pattern"]!.GetValue<string>(),
                    args["path"]?.GetValue<string>() ?? ".",
                    args["glob"]?.GetValue<string>() ?? "*"),

                "edit_file" => EditFile(
                    args["path"]!.GetValue<string>(),
                    args["edits"]),

                "copy_file" => CopyFile(
                    args["source"]!.GetValue<string>(),
                    args["destination"]!.GetValue<string>()),

                "move_file" => MoveFile(
                    args["source"]!.GetValue<string>(),
                    args["destination"]!.GetValue<string>()),

                "delete_file" => DeleteFile(
                    args["path"]!.GetValue<string>()),

                "zip" => Zip(
                    args["source"]!.GetValue<string>(),
                    args["destination"]!.GetValue<string>()),

                "unzip" => Unzip(
                    args["archive"]!.GetValue<string>(),
                    args["destination"]!.GetValue<string>()),

                "parse_output" => ParseOutput(
                    args["output"]!.GetValue<string>(),
                    args["format"]?.GetValue<string>() ?? "auto",
                    args["query"]?.GetValue<string>()),

                "git_status" => await GitStatusAsync(
                    args["path"]?.GetValue<string>() ?? "."),

                "git_diff" => await GitDiffAsync(
                    args["path"]?.GetValue<string>() ?? ".",
                    args["staged"]?.GetValue<bool>() ?? false),

                "git_log" => await GitLogAsync(
                    args["path"]?.GetValue<string>() ?? ".",
                    args["count"]?.GetValue<int>() ?? 20),

                "git_branch" => await GitBranchAsync(
                    args["path"]?.GetValue<string>() ?? "."),

                "git_commit" => await GitCommitAsync(
                    args["path"]?.GetValue<string>() ?? ".",
                    args["message"]!.GetValue<string>()),

                "sh" => await RunShellAsync(
                    args["cmd"]!.GetValue<string>(), isWindows),

                "http_request" => await HttpRequestAsync(
                    args["url"]!.GetValue<string>(),
                    args["method"]?.GetValue<string>() ?? "GET",
                    args["headers"] as JsonObject,
                    args["body"]?.GetValue<string>(),
                    args["timeoutMs"]?.GetValue<int>() ?? 30_000),

                "web_search" => await WebSearchAsync(
                    args["query"]!.GetValue<string>(),
                    args["maxResults"]?.GetValue<int>() ?? 5),

                "fetch_url" => await FetchUrlAsync(
                    args["url"]!.GetValue<string>(),
                    args["maxChars"]?.GetValue<int>() ?? 20_000),

                "run_terminal" => await RunTerminalAsync(
                    args["cmd"]!.GetValue<string>(),
                    args["session"]?.GetValue<string>() ?? "default",
                    args["timeoutMs"]?.GetValue<int>() ?? 60_000,
                    isWindows),

                "close_terminal" => CloseTerminal(
                    args["session"]?.GetValue<string>() ?? "default"),

                "switch_model" => SwitchModel(
                    args["model"]!.GetValue<string>(),
                    switchModel),

                "list_models" => await ListModelsAsync(),

                "read_clipboard" => await ReadClipboardAsync(isWindows),

                "write_clipboard" => await WriteClipboardAsync(
                    args["content"]!.GetValue<string>(), isWindows),

                "excel_command" => await ExcelCommandAsync(
                     args["command"]!.GetValue<string>(),
                     args["session"]?.GetValue<string>() ?? "default",
                     args["timeoutMs"]?.GetValue<int>() ?? 60_000,
                     isWindows),

                "close_excel" => CloseExcel(
                    args["session"]?.GetValue<string>() ?? "default"),


                "send_email" => await SendEmailAsync(
                   args["to"]!.GetValue<string>(),
                   args["cc"]?.GetValue<string>(),
                   args["bcc"]?.GetValue<string>(),
                   args["subject"]!.GetValue<string>(),
                   args["body"]?.GetValue<string>(),
                   args["bodyHtml"]?.GetValue<string>(),
                   args["attachments"] as JsonArray,
                   args["send"]?.GetValue<bool>() ?? false,
                   args["saveToDrafts"]?.GetValue<bool>() ?? false,
                   isWindows),

                _ => $"Error: Unknown tool '{name}'"
            };
        }
        catch (Exception ex)
        {
            return $"Error: dispatch failed — {ex.Message}";
        }
    }
    /// <summary>
    /// Returns true if the tool name is considered destructive (requires user confirmation).
    /// </summary>
    public static bool IsDestructive(string name) =>
        name is "write_file" or "edit_file" or "git_commit" or "move_file" or "delete_file" or "unzip" or "write_clipboard" or "send_email";
}