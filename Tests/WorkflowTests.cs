using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using CsAgent.Core.Agent;
using CsAgent.Core.Llm;
using CsAgent.Presentation.Web;
using CsAgent.Shared;

namespace CsAgent.Tests;

/// <summary>
/// A stand-in for <c>FlowPortal --mcp</c>, started by the tests with this same program (<c>--flow-fake &lt;scenario&gt;</c>).
/// It offers csflow_run and csflow_status and tells a scripted story: <c>ok</c> (a step ends, then the workflow is done;
/// the last answer says whether the agent server of CsAgent answered, port in FLOW_TEST_PORT), <c>fail</c> (a step fails),
/// <c>human</c> (refused: the workflow has a human step), <c>invalid</c> (not started: validation errors),
/// <c>notools</c> (no csflow tool at all).
/// </summary>
static class FlowFakeServer
{
    public static int Run(string[] args)
    {
        var scenario = args.Length > 1 ? args[1] : "ok";
        string? line;
        while ((line = Console.In.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var req = JsonNode.Parse(line)!;
            var method = req["method"]?.GetValue<string>();
            var id = req["id"];
            if (method is null || id is null) continue;

            JsonNode result = new JsonObject();
            switch (method)
            {
                case "initialize":
                    result = new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["serverInfo"] = new JsonObject { ["name"] = "flow", ["version"] = "1" } };
                    break;
                case "tools/list":
                    var names = scenario == "notools" ? new[] { "other" } : new[] { "csflow_run", "csflow_status" };
                    var tools = new JsonArray();
                    foreach (var n in names) tools.Add(new JsonObject { ["name"] = n, ["description"] = n, ["inputSchema"] = new JsonObject { ["type"] = "object" } });
                    result = new JsonObject { ["tools"] = tools };
                    break;
                case "tools/call":
                    result = Call(scenario, req["params"]!["name"]!.GetValue<string>(), req["params"]!["arguments"] as JsonObject);
                    break;
            }
            Console.Out.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result }.ToJsonString());
        }
        return 0;
    }

    static JsonObject Text(string text, bool error = false) =>
        new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["isError"] = error };

    static JsonNode Step(string id, string type, string status, string output = "") =>
        new JsonObject { ["id"] = id, ["type"] = type, ["status"] = status, ["output"] = output };

    static JsonObject Call(string scenario, string tool, JsonObject? args)
    {
        var workflow = args?["workflow"]?.GetValue<string>() ?? "";
        if (tool == "csflow_run")
        {
            if (scenario == "human")
                return Text($"'{workflow}' contains a human step (approval or text input): it is not run over MCP. Tell the user to run it from the CSFlow interface.", error: true);
            if (scenario == "invalid")
                return Text(new JsonObject { ["started"] = false, ["reason"] = "validation errors", ["issues"] = new JsonArray(new JsonObject { ["level"] = "error", ["message"] = "type inconnu : shel" }) }.ToJsonString());
            // Still running: one step ended (with a two-line output), the next one is going on.
            return Text(new JsonObject
            {
                ["run_id"] = "r1", ["workflow"] = workflow, ["status"] = "running",
                ["steps"] = new JsonArray(Step("prepare", "shell", "ok", "ligne 1\nligne 2"), Step("analyse", "agent", "running"))
            }.ToJsonString());
        }

        // csflow_status: the end of the story.
        if (scenario == "fail")
            return Text(new JsonObject
            {
                ["run_id"] = "r1", ["status"] = "error", ["error"] = "Workflow aborted",
                ["steps"] = new JsonArray(Step("prepare", "shell", "ok"), Step("analyse", "agent", "error", "API Key not set.")),
                ["failed_step"] = new JsonObject { ["id"] = "analyse", ["returncode"] = 1, ["output"] = "API Key not set." }
            }.ToJsonString());

        var api = AgentAnswered();
        return Text(new JsonObject
        {
            ["run_id"] = "r1", ["status"] = "done",
            ["steps"] = new JsonArray(Step("prepare", "shell", "ok", "ligne 1\nligne 2"), Step("analyse", "agent", "ok", "fait"), Step("fin", "end", "ok")),
            ["result"] = new JsonObject { ["step"] = "analyse", ["output"] = $"rapport écrit; agent-api={api}" }
        }.ToJsonString());
    }

    // Does the agent server of CsAgent answer on the port the test gave (what an agent step would need)?
    static string AgentAnswered()
    {
        var port = Environment.GetEnvironmentVariable("FLOW_TEST_PORT");
        if (string.IsNullOrEmpty(port)) return "unknown";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            return ((int)http.GetAsync($"http://localhost:{port}/api/skills").GetAwaiter().GetResult().StatusCode).ToString();
        }
        catch (Exception ex) { return "down:" + ex.GetType().Name; }
    }
}

static partial class Tests
{
    // ═════════════════════════ --workflow: run a CSFlow workflow from the command line ═════════════════════════
    static async Task WorkflowGroup()
    {
        Group("CLI / --workflow (CSFlow)");

        static McpServerConfig Flow(string scenario, int port = 0, string name = "csflow")
        {
            // The tests assembly is started directly (not testhost): see McpStdioTests.
            var dll = typeof(FlowFakeServer).Assembly.Location;
            var launcher = Path.Combine(Path.GetDirectoryName(dll)!, Path.GetFileNameWithoutExtension(dll) + (OperatingSystem.IsWindows() ? ".exe" : ""));
            var args = new List<string>();
            string exe;
            if (File.Exists(launcher)) exe = launcher;
            else { exe = "dotnet"; args.Add(dll); }
            args.Add("--flow-fake"); args.Add(scenario);
            return new McpServerConfig(name, Command: exe, ArgList: args,
                EnvValues: new Dictionary<string, string> { ["FLOW_TEST_PORT"] = port.ToString() });
        }

        // Runs the workflow with the fake server and returns (exit code, stdout, stderr).
        static (int Code, string Out, string Err, int Port) Go(string scenario, params McpServerConfig[] servers)
        {
            int port = FreePort();
            var o = new StringWriter(); var e = new StringWriter();
            var (so, se) = (Console.Out, Console.Error);
            McpSettings.Configure(servers.Length > 0 ? servers : new[] { Flow(scenario, port) });
            Console.SetOut(o); Console.SetError(e);
            try
            {
                var args = ArgumentParser.Parse(new[] { "--workflow", "demo.yaml", "--port", port.ToString(), "--mem", Path.Combine(Path.GetTempPath(), "csagent-wf-mem") });
                return (WorkflowHost.Run(args), o.ToString(), e.ToString(), port);
            }
            finally { Console.SetOut(so); Console.SetError(se); McpSettings.Reset(); }
        }

        await T("the option is read; the workflow name is not mistaken for a memory file", async () =>
        {
            var a = ArgumentParser.Parse(new[] { "--workflow", "demo_report.yaml" });
            Assert(a.Workflow == "demo_report.yaml", "workflow: " + a.Workflow);
            Assert(a.MemoryFile != "demo_report.yaml", "the workflow must not become the memory file: " + a.MemoryFile);
            var b = ArgumentParser.Parse(new[] { "mymem", "--workflow", "x.yaml" });
            Assert(b.MemoryFile == "mymem" && b.Workflow == "x.yaml", "positional memory kept: " + b.MemoryFile);
            Assert(ArgumentParser.Parse(Array.Empty<string>()).Workflow is null, "no option, no workflow");
            Assert(ArgumentParser.Parse(new[] { "--workflow" }).Workflow == "", "the flag alone gives an empty value (usage)");
            await Task.CompletedTask; return "ok";
        });

        await T("--fresh empties the memory of the agent steps, and only its four files", async () =>
        {
            Assert(!ArgumentParser.Parse(new[] { "--workflow", "x.yaml" }).Fresh, "off by default");
            Assert(ArgumentParser.Parse(new[] { "--workflow", "x.yaml", "--fresh" }).Fresh, "on");
            var dir = Path.Combine(Path.GetTempPath(), "csagent-wf-fresh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            foreach (var f in new[] { "conversation.json", "exact.json", "semantic.json", "summary.json", "mine.txt" })
                File.WriteAllText(Path.Combine(dir, f), "x");
            int port = FreePort();
            var (so, se) = (Console.Out, Console.Error);
            McpSettings.Configure(new[] { Flow("ok", port) });
            Console.SetOut(new StringWriter()); Console.SetError(new StringWriter());
            try
            {
                var plain = WorkflowHost.Run(ArgumentParser.Parse(new[] { "--workflow", "x.yaml", "--port", port.ToString(), "--mem", dir }));
                Assert(plain == 0 && File.Exists(Path.Combine(dir, "conversation.json")), "without --fresh nothing is deleted");
                var fresh = WorkflowHost.Run(ArgumentParser.Parse(new[] { "--workflow", "x.yaml", "--fresh", "--port", port.ToString(), "--mem", dir }));
                Assert(fresh == 0, "exit " + fresh);
            }
            finally { Console.SetOut(so); Console.SetError(se); McpSettings.Reset(); }
            foreach (var f in new[] { "conversation.json", "exact.json", "semantic.json", "summary.json" })
                Assert(!File.Exists(Path.Combine(dir, f)), f + " should be gone");
            Assert(File.Exists(Path.Combine(dir, "mine.txt")), "other files stay");
            await Task.CompletedTask; return "ok";
        });

        await T("a workflow looks for the agent on port 8080, with its own memory file and no distilling; each can be changed", async () =>
        {
            var a = ArgumentParser.Parse(new[] { "--workflow", "x.yaml" });
            Assert(a.Port == 8080, "port " + a.Port);
            Assert(a.MemoryFile == "csflow_worker" && !a.Distill, $"memory {a.MemoryFile}, distill {a.Distill}");
            var b = ArgumentParser.Parse(new[] { "--workflow", "x.yaml", "--port", "9090", "--mem", "mine", "--yes", "--api-key", "k" });
            Assert(b.Port == 9090 && b.MemoryFile == "mine" && b.AutoApprove && b.ApiKey == "k", $"{b.Port} {b.MemoryFile} {b.AutoApprove} {b.ApiKey}");
            var c = ArgumentParser.Parse(new[] { "--api" });
            Assert(c.Port == 5050 && c.Distill, "without --workflow nothing changes: port 5050, distill on");
            await Task.CompletedTask; return "ok";
        });

        await T("without a server named csflow in --mcp-config, nothing starts and the exit code is 2", async () =>
        {
            var r = Go("ok", Flow("ok", name: "autre"));
            Assert(r.Code == 2 && r.Err.Contains("no MCP server named \"csflow\""), $"{r.Code} {r.Err}");
            Assert(r.Out == "", "nothing was started, nothing printed: " + r.Out);
            await Task.CompletedTask; return "ok";
        });

        await T("a finished workflow: steps printed in order, the result printed, exit code 0, the agent server answered meanwhile and is stopped after", async () =>
        {
            var r = Go("ok");
            Assert(r.Code == 0, $"exit {r.Code}; out: {r.Out}; err: {r.Err}");
            var lines = r.Out.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            int iPrepare = lines.FindIndex(l => l.StartsWith("  ✓ prepare (shell)  ligne 1 ⏎ ligne 2"));
            int iAnalyse = lines.FindIndex(l => l.StartsWith("  ✓ analyse (agent)  fait"));
            int iFin = lines.FindIndex(l => l.StartsWith("  ✓ fin (end)"));
            Assert(iPrepare >= 0 && iAnalyse > iPrepare && iFin > iAnalyse, "steps, each once and in order:\n" + r.Out);
            Assert(r.Out.Contains("Workflow complete."), "end line");
            Assert(r.Out.Contains("rapport écrit; agent-api=200"), "the agent server answered while the workflow ran:\n" + r.Out);
            Assert(r.Out.Split("✓ prepare").Length == 2, "a step is printed once, not at every poll");

            bool stillUp;
            try { using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) }; await http.GetAsync($"http://localhost:{r.Port}/api/skills"); stillUp = true; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { stillUp = false; }   // refused, or (Windows) no answer before the timeout
            Assert(!stillUp, "the agent server must be stopped when the workflow ends");
            return "ok";
        });

        await T("a failed workflow: the failing step and the reason go to stderr, exit code 1", async () =>
        {
            var r = Go("fail");
            Assert(r.Code == 1, $"exit {r.Code}; err: {r.Err}");
            Assert(r.Out.Contains("✗ analyse (agent)  API Key not set."), "the failed step is shown:\n" + r.Out);
            Assert(r.Err.Contains("Workflow failed: Workflow aborted") && r.Err.Contains("analyse: API Key not set."), r.Err);
            await Task.CompletedTask; return "ok";
        });

        await T("a workflow with a human step, an invalid one, and a server without the csflow tools: exit code 2 with the reason", async () =>
        {
            var human = Go("human");
            Assert(human.Code == 2 && human.Err.Contains("contains a human step") && human.Err.Contains("CSFlow interface"), $"{human.Code} {human.Err}");
            var invalid = Go("invalid");
            Assert(invalid.Code == 2 && invalid.Err.Contains("was not started: validation errors") && invalid.Err.Contains("type inconnu : shel"), $"{invalid.Code} {invalid.Err}");
            var none = Go("notools");
            Assert(none.Code == 2 && none.Err.Contains("no csflow_run / csflow_status tool"), $"{none.Code} {none.Err}");
            await Task.CompletedTask; return "ok";
        });

        await T("the French messages exist", async () =>
        {
            Loc.Set("fr");
            try
            {
                Assert(Loc.T("Workflow complete.") == "Workflow terminé.", "complete");
                Assert(Loc.F($"Workflow failed: {"x"}") == "Le workflow a échoué : x", "failed");
                Assert(Loc.F($"Error: cannot start CSFlow: {"x"}") == "Erreur : impossible de démarrer CSFlow : x", "cannot start");
                Assert(Loc.T("Error: no MCP server named \"csflow\". Give the mcp.json of CSFlow with --mcp-config (see the README).").StartsWith("Erreur : aucun serveur MCP"), "no server");
                Assert(Loc.T("Usage: csagent --mcp-config mcp.json --workflow <file>   (runs a CSFlow workflow of its workflows folder)").StartsWith("Usage : csagent --mcp-config"), "usage");
            }
            finally { Loc.Set("en"); }
            await Task.CompletedTask; return "ok";
        });
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }
}
