using System.Text.Json.Nodes;
using CsAgent.Core.Agent;
using CsAgent.Core.Llm;
using CsAgent.Core.Memory;
using CsAgent.Shared;

namespace CsAgent.Presentation.Web;

/// <summary>
/// <c>--workflow &lt;file&gt;</c>: runs a CSFlow workflow from the command line, without a model in between.
/// CsAgent starts its API server (for the <c>agent</c> steps of the workflow), starts CSFlow as the MCP server
/// named "csflow" of <c>--mcp-config</c> (<c>FlowPortal --mcp</c>), calls its <c>csflow_run</c> and <c>csflow_status</c>
/// tools until the workflow ends, prints the steps as they finish and exits with 0 (done) or 1 (failed).
/// </summary>
public static class WorkflowHost
{
    public const string ServerName = "csflow";
    private const string RunTool = "mcp_csflow_run";
    private const string StatusTool = "mcp_csflow_status";
    private const int WaitSeconds = 5;      // each call waits this long at most, so the steps show up as they finish
    private const int PreviewChars = 120;

    public static int Run(AgentArguments args)
    {
        var csflow = McpSettings.Servers.FirstOrDefault(s => s.Name.Equals(ServerName, StringComparison.OrdinalIgnoreCase));
        if (csflow is null)
        {
            Console.Error.WriteLine(Loc.T("Error: no MCP server named \"csflow\". Give the mcp.json of CSFlow with --mcp-config (see the README)."));
            return 2;
        }

        if (args.Fresh) ForgetMemory(args.MemoryFile);

        var agent = ApiHost.Create(args);
        if (agent is null) return 2;

        var url = ApiHost.Url(args);
        try { agent.Urls.Add(url); agent.StartAsync().GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Loc.F($"Error: cannot start the agent on {url}: {ex.Message}"));
            return 2;
        }

        try
        {
            Console.WriteLine(Loc.F($"--- CSAgent for the agent steps: {url} ---"));
            if (string.IsNullOrEmpty(LlmConfig.ResolveApiKey()))
                Console.Error.WriteLine("--- WARNING: ALBERT_API_KEY is not set; the agent steps will fail ---");

            return RunWorkflowAsync(csflow, args.Workflow!).GetAwaiter().GetResult();
        }
        finally
        {
            agent.StopAsync().GetAwaiter().GetResult();
        }
    }

    /// <summary>--fresh: the agent steps start with an empty memory. Only the memory files (and the archive) are deleted, never the folder.</summary>
    private static void ForgetMemory(string memory)
    {
        var paths = MemoryPaths.Resolve(memory);
        foreach (var file in new[] { paths.Conversation, paths.Exact, paths.Semantic, paths.Summary, paths.Archive })
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static async Task<int> RunWorkflowAsync(McpServerConfig csflow, string workflow)
    {
        // Ctrl+C stops the wait; closing the connection below stops CSFlow and with it the workflow.
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            using var hub = new McpHub(new[] { csflow });
            var failures = await hub.ConnectAsync(cancel.Token);
            if (failures.Count > 0)
            {
                Console.Error.WriteLine(Loc.F($"Error: cannot start CSFlow: {failures[0].Error}"));
                return 2;
            }
            if (!hub.Contains(RunTool) || !hub.Contains(StatusTool))
            {
                Console.Error.WriteLine(Loc.T("Error: the \"csflow\" MCP server has no csflow_run / csflow_status tool. It must be FlowPortal --mcp."));
                return 2;
            }

            var answer = await hub.CallToolAsync(RunTool,
                new JsonObject { ["workflow"] = workflow, ["wait_seconds"] = WaitSeconds }.ToJsonString(), cancel.Token);
            var shown = 0;
            while (true)
            {
                var snapshot = AsObject(answer);
                if (snapshot is null)
                {
                    Console.Error.WriteLine(answer);        // "Error: MCP tool ... failed: <the reason CSFlow gave>"
                    return 2;
                }
                if (snapshot["started"]?.GetValue<bool>() == false)
                {
                    Console.Error.WriteLine(Loc.F($"Error: the workflow was not started: {snapshot["reason"]?.GetValue<string>()}"));
                    foreach (var issue in snapshot["issues"] as JsonArray ?? new JsonArray())
                        Console.Error.WriteLine("  " + issue?["message"]?.GetValue<string>());
                    return 2;
                }

                shown = PrintFinishedSteps(snapshot, shown);
                var status = snapshot["status"]?.GetValue<string>();
                if (status != "running") return Finish(snapshot, status);

                var runId = snapshot["run_id"]?.GetValue<string>() ?? "";
                answer = await hub.CallToolAsync(StatusTool,
                    new JsonObject { ["run_id"] = runId, ["wait_seconds"] = WaitSeconds }.ToJsonString(), cancel.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine(Loc.T("Stopped."));
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static JsonObject? AsObject(string text)
    {
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>Prints the steps that ended since the last call, in order; a step still running waits for the next call.</summary>
    private static int PrintFinishedSteps(JsonObject snapshot, int shown)
    {
        var steps = snapshot["steps"] as JsonArray ?? new JsonArray();
        while (shown < steps.Count && steps[shown] is JsonObject step && step["status"]?.GetValue<string>() != "running")
        {
            var mark = step["status"]?.GetValue<string>() switch { "ok" => "✓", "error" => "✗", _ => "-" };
            var type = step["type"]?.GetValue<string>();
            var line = $"  {mark} {step["id"]?.GetValue<string>()}" + (type is null or "?" ? "" : $" ({type})");
            var output = OneLine(step["output"]?.GetValue<string>());
            Console.WriteLine(output.Length == 0 ? line : line + "  " + output);
            shown++;
        }
        return shown;
    }

    private static int Finish(JsonObject snapshot, string? status)
    {
        if (status == "done")
        {
            Console.WriteLine(Loc.T("Workflow complete."));
            var result = snapshot["result"]?["output"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(result)) Console.WriteLine(result);
            return 0;
        }

        Console.Error.WriteLine(Loc.F($"Workflow failed: {snapshot["error"]?.GetValue<string>() ?? status}"));
        var failed = snapshot["failed_step"] as JsonObject;
        var detail = failed?["output"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(detail) && failed?["returncode"] is { } code) detail = Loc.F($"exit code {code}");
        if (!string.IsNullOrWhiteSpace(detail))
            Console.Error.WriteLine($"  {failed?["id"]?.GetValue<string>()}: {detail}");
        return 1;
    }

    private static string OneLine(string? text)
    {
        var line = (text ?? "").Replace("\r", "").Replace("\n", " ⏎ ");
        return line.Length <= PreviewChars ? line : line[..PreviewChars] + "…";
    }
}
