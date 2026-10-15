using System.Diagnostics;
using System.Text.Json.Nodes;
using CsAgent.Core.Agent;
using CsAgent.Shared;

namespace CsAgent.Tests;

/// <summary>
/// A tiny MCP server on standard input and output, started by the tests with this same program
/// (<c>--mcp-echo</c>). Options: <c>--tool name</c>, <c>--noise</c> (log lines and a ping request before each answer),
/// <c>--die</c> (exits after initialize), <c>--slow</c> (never answers tools/list).
/// </summary>
static class McpEchoServer
{
    public static int Run(string[] args)
    {
        string tool = "echo";
        bool noise = false, die = false;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--tool" && i + 1 < args.Length) tool = args[++i];
            else if (args[i] == "--noise") noise = true;
            else if (args[i] == "--die") die = true;
        }
        Console.Error.WriteLine("echo server started");
        string? line;
        while ((line = Console.In.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var req = JsonNode.Parse(line)!;
            if (req["method"] is null) continue;                           // an answer to something we asked
            var method = req["method"]!.GetValue<string>();
            var id = req["id"];
            if (id is null) continue;                                      // a notification
            if (noise)
            {
                Console.Out.WriteLine("log: this is not a JSON message");
                Console.Out.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":\"srv-1\",\"method\":\"ping\"}");
                Console.Out.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\",\"params\":{}}");
            }

            JsonNode result;
            switch (method)
            {
                case "initialize":
                    result = new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["serverInfo"] = new JsonObject { ["name"] = "echo", ["version"] = "1" } };
                    break;
                case "tools/list":
                    if (die) return 3;
                    result = new JsonObject { ["tools"] = new JsonArray(new JsonObject
                    {
                        ["name"] = tool, ["description"] = "echoes its text",
                        ["inputSchema"] = new JsonObject { ["type"] = "object" }
                    }) };
                    break;
                case "tools/call":
                    var text = req["params"]?["arguments"]?["text"]?.GetValue<string>() ?? "";
                    var env = Environment.GetEnvironmentVariable("MCP_TEST_VALUE") ?? "";
                    result = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"{tool}:{text}|env={env}" }) };
                    break;
                default: result = new JsonObject(); break;
            }
            Console.Out.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result }.ToJsonString());
        }
        return 0;
    }
}

static partial class Tests
{
    // ═════════════════════════ MCP: stdio, several servers, headers ═════════════════════════
    static async Task McpStdioGroup()
    {
        Group("MCP / stdio, several servers, authentication headers");

        // The command that starts this program as the echo server.
        static McpServerConfig Echo(string name, string tool = "echo", string? extra = null, Dictionary<string, string>? env = null)
        {
            var exe = Environment.ProcessPath!;
            var args = new List<string>();
            if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                args.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
            args.Add("--mcp-echo"); args.Add("--tool"); args.Add(tool);
            if (extra is not null) args.Add(extra);
            return new McpServerConfig(name, Command: exe, ArgList: args, EnvValues: env);
        }

        await T("stdio: connects, lists the tool, calls it; the server's environment value reaches it", async () =>
        {
            using var hub = new McpHub(new[] { Echo("one", env: new() { ["MCP_TEST_VALUE"] = "from-config" }) });
            var failures = await hub.ConnectAsync();
            Assert(failures.Count == 0, string.Join(";", failures));
            Assert(hub.Tools.ContainsKey("mcp_echo"), string.Join(",", hub.Tools.Keys));
            var r = await hub.CallToolAsync("mcp_echo", "{\"text\":\"hi\"}");
            Assert(r == "echo:hi|env=from-config", r);
            var r2 = await hub.CallToolAsync("mcp_echo", "{\"text\":\"again\"}");
            Assert(r2.StartsWith("echo:again"), r2);
            return r;
        });

        await T("stdio: log lines on stdout, notifications and a ping from the server do not disturb the exchange", async () =>
        {
            using var hub = new McpHub(new[] { Echo("noisy", extra: "--noise") });
            Assert((await hub.ConnectAsync()).Count == 0, "connect");
            var r = await hub.CallToolAsync("mcp_echo", "{\"text\":\"x\"}");
            Assert(r.StartsWith("echo:x"), r);
            return "ok";
        });

        await T("several servers: tools are named mcp_<server>_<tool>, so equal tool names do not clash, and each call reaches its own server", async () =>
        {
            using var hub = new McpHub(new[] { Echo("alpha"), Echo("beta") });
            Assert((await hub.ConnectAsync()).Count == 0, "connect");
            var names = hub.Tools.Keys.OrderBy(k => k).ToArray();
            Assert(names.SequenceEqual(new[] { "mcp_alpha_echo", "mcp_beta_echo" }), string.Join(",", names));
            Assert((await hub.CallToolAsync("mcp_beta_echo", "{\"text\":\"b\"}")).StartsWith("echo:b"), "beta");
            Assert(hub.Summary.Contains("2 server(s)") && hub.Summary.Contains("alpha: 1") && hub.Summary.Contains("beta: 1"), hub.Summary);
            Assert((await hub.CallToolAsync("mcp_nope", "{}")).StartsWith("Error: Unknown MCP tool"), "unknown");
            return hub.Summary;
        });

        await T("a server that cannot start or dies is reported by name; the others stay usable", async () =>
        {
            var missing = new McpServerConfig("ghost", Command: "this-program-does-not-exist-" + Guid.NewGuid().ToString("N"));
            using var hub = new McpHub(new[] { Echo("good"), missing, Echo("dying", extra: "--die") });
            var failures = await hub.ConnectAsync();
            Assert(failures.Count == 2 && failures.Any(f => f.Name == "ghost") && failures.Any(f => f.Name == "dying"), string.Join(";", failures));
            Assert(failures.First(f => f.Name == "dying").Error.Contains("closed its output"), failures.First(f => f.Name == "dying").Error);
            Assert(hub.Tools.ContainsKey("mcp_good_echo") && hub.Connected.Count == 1, "good server lost");
            Assert((await hub.CallToolAsync("mcp_good_echo", "{\"text\":\"ok\"}")).StartsWith("echo:ok"), "call");
            return string.Join(" / ", failures.Select(f => f.Name + ": " + f.Error[..Math.Min(60, f.Error.Length)]));
        });

        await T("one server keeps the names of before (mcp_<tool>)", async () =>
        {
            using var hub = new McpHub(new[] { Echo("only") });
            await hub.ConnectAsync();
            Assert(hub.Tools.Keys.Single() == "mcp_echo" && hub.Summary == "1 tool(s) available.", hub.Summary);
            return "ok";
        });

        await T("the stdio child is stopped when the hub is disposed", async () =>
        {
            var hub = new McpHub(new[] { Echo("tmp") });
            await hub.ConnectAsync();
            var before = Process.GetProcesses().Count(p => { try { return p.ProcessName == Path.GetFileNameWithoutExtension(Environment.ProcessPath!) && p.Id != Environment.ProcessId; } catch { return false; } });
            hub.Dispose();
            await Task.Delay(1000);
            var after = Process.GetProcesses().Count(p => { try { return p.ProcessName == Path.GetFileNameWithoutExtension(Environment.ProcessPath!) && p.Id != Environment.ProcessId; } catch { return false; } });
            Assert(after < before || before == 0, $"before {before}, after {after}");
            return $"{before} -> {after}";
        });

        await T("HTTP: configured headers (Authorization) are sent on every request; the server list may mix HTTP and stdio", async () =>
        {
            using var http = new MockMcp();
            http.Tools.Add(("remote", "r"));
            var hdr = new Dictionary<string, string> { ["Authorization"] = "Bearer s3cret", ["X-Team"] = "po" };
            using var hub = new McpHub(new[] { new McpServerConfig("web", Url: http.Url, HeaderValues: hdr), Echo("local") });
            Assert((await hub.ConnectAsync()).Count == 0, "connect");
            await hub.CallToolAsync("mcp_web_remote", "{}");
            string[] seen; lock (http.RequestHeaders) seen = http.RequestHeaders.ToArray();
            Assert(seen.Length >= 4 && seen.All(h => h.Contains("Bearer s3cret") && h.Contains("X-Team: po")), $"{seen.Length} requests, a header is missing");
            Assert(hub.Tools.ContainsKey("mcp_web_remote") && hub.Tools.ContainsKey("mcp_local_echo"), string.Join(",", hub.Tools.Keys));
            return $"{seen.Length} requests, all with the headers";
        });

        await T("a failed HTTP connection says which server", async () =>
        {
            using var hub = new McpHub(new[] { new McpServerConfig("down", Url: "http://127.0.0.1:1/mcp"), Echo("up") });
            var failures = await hub.ConnectAsync();
            Assert(failures.Count == 1 && failures[0].Name == "down", string.Join(";", failures));
            Assert(hub.Tools.ContainsKey("mcp_up_echo"), "up server");
            return "ok";
        });

        // ── configuration: command line and file ──
        await T("--mcp is repeatable; --mcp-url, --mcp-stdio, --mcp-header and --mcp-config are read and are not taken for the memory name", async () =>
        {
            var a = ArgumentParser.Parse(new[] { "--mcp", "http://a/mcp", "--mcp", "http://b/mcp", "--mcp-stdio", "npx -y srv", "--mcp-header", "X: y", "--mcp-config", "f.json", "mem1" });
            Assert(a.McpUrls!.SequenceEqual(new[] { "http://a/mcp", "http://b/mcp" }) && a.McpUrl == "http://a/mcp", "urls");
            Assert(a.McpStdio!.Single() == "npx -y srv" && a.McpHeaders!.Single() == "X: y" && a.McpConfig == "f.json", "others");
            Assert(a.MemoryFile == "mem1", "memory name: " + a.MemoryFile);
            var none = ArgumentParser.Parse(Array.Empty<string>());
            Assert(none.McpUrls!.Count == 0 && none.McpConfig is null, "defaults");
            await Task.CompletedTask; return "ok";
        });

        await T("command line to servers: names from the URL host or the program, headers applied to the URLs, quotes respected", async () =>
        {
            var a = ArgumentParser.Parse(new[] { "--mcp", "https://api.example.com/mcp", "--mcp-header", "Authorization: Bearer abc",
                "--mcp-stdio", "npx -y @modelcontextprotocol/server-filesystem .", "--mcp-stdio", "\"C:\\Program Files\\srv\\srv.exe\" --root 'my dir'" });
            var s = McpSettings.FromArguments(a);
            Assert(s.Count == 3, "count " + s.Count);
            Assert(s[0].Name == "api_example_com" && s[0].Headers["Authorization"] == "Bearer abc", s[0].Name);
            Assert(s[1].Command == "npx" && s[1].Name == "server-filesystem" && s[1].Args.SequenceEqual(new[] { "-y", "@modelcontextprotocol/server-filesystem", "." }), s[1].Name + " " + string.Join("|", s[1].Args));
            Assert(s[2].Name == "srv" && s[2].Command == "C:\\Program Files\\srv\\srv.exe" && s[2].Args.SequenceEqual(new[] { "--root", "my dir" }), s[2].Command + " " + string.Join("|", s[2].Args));
            await Task.CompletedTask; return string.Join(", ", s.Select(x => x.Name));
        });

        await T("mistakes are explained: a header without a URL, a non-http URL, an empty stdio command", async () =>
        {
            string Msg(params string[] args) { try { McpSettings.FromArguments(ArgumentParser.Parse(args)); return ""; } catch (McpConfigException ex) { return ex.Message; } }
            Assert(Msg("--mcp-header", "X: y").Contains("needs an --mcp"), "header alone");
            Assert(Msg("--mcp", "ftp://x").Contains("not an http(s) URL"), "scheme");
            Assert(Msg("--mcp", "http://x", "--mcp-header", "nocolon").Contains("Name: value"), "format");
            Assert(Msg("--mcp-stdio", "  ").Contains("empty"), "empty");
            await Task.CompletedTask; return "ok";
        });

        await T("${VAR} in headers, arguments and environment is replaced from the environment; a missing variable is named, its value never shown", async () =>
        {
            Environment.SetEnvironmentVariable("CSAGENT_TEST_TOKEN", "tok-123");
            try
            {
                var a = ArgumentParser.Parse(new[] { "--mcp", "http://h/mcp", "--mcp-header", "Authorization: Bearer ${CSAGENT_TEST_TOKEN}" });
                Assert(McpSettings.FromArguments(a)[0].Headers["Authorization"] == "Bearer tok-123", "expanded");
                var file = McpSettings.Parse("""{"mcpServers":{"s":{"command":"x","args":["--key","${CSAGENT_TEST_TOKEN}"],"env":{"K":"${CSAGENT_TEST_TOKEN}"}}}}""");
                Assert(file[0].Args[1] == "tok-123" && file[0].Env["K"] == "tok-123", "file");
                string err = "";
                try { McpSettings.Parse("""{"mcpServers":{"s":{"url":"http://h","headers":{"A":"${CSAGENT_NOT_SET_ANYWHERE}"}}}}"""); }
                catch (McpConfigException ex) { err = ex.Message; }
                Assert(err.Contains("CSAGENT_NOT_SET_ANYWHERE") && err.Contains("server 's'"), err);
                Assert(!McpSettings.Describe("http://h/mcp").Contains("tok-123"), "describe");
            }
            finally { Environment.SetEnvironmentVariable("CSAGENT_TEST_TOKEN", null); }
            await Task.CompletedTask; return "ok";
        });

        await T("config file: mcpServers with url or command; both or neither, bad JSON and a bad shape are explained; names are made unique and clean", async () =>
        {
            var ok = McpSettings.Parse("""
                { // comments and trailing commas are accepted
                  "mcpServers": {
                    "My Server": { "url": "https://x/mcp", "headers": { "Authorization": "Bearer abc" } },
                    "files": { "command": "npx", "args": ["-y", "srv"], "cwd": "work", },
                  } }
                """);
            Assert(ok.Count == 2 && ok[0].Url == "https://x/mcp" && ok[0].Headers["Authorization"] == "Bearer abc", "first");
            Assert(ok[1].IsStdio && ok[1].WorkingDirectory == "work" && ok[1].Args.Count == 2, "second");
            string Err(string json) { try { McpSettings.Parse(json); return ""; } catch (McpConfigException ex) { return ex.Message; } }
            Assert(Err("""{"mcpServers":{"a":{"url":"http://x","command":"y"}}}""").Contains("either"), "both");
            Assert(Err("""{"mcpServers":{"a":{}}}""").Contains("either"), "neither");
            Assert(Err("{ not json").Contains("not valid JSON"), "json");
            Assert(Err("""{"servers":{}}""").Contains("mcpServers"), "shape");
            var dup = McpSettings.FromArguments(ArgumentParser.Parse(new[] { "--mcp", "http://same/a", "--mcp", "http://same/b" }));
            Assert(dup[0].Name == "same" && dup[1].Name == "same_2", dup[0].Name + "," + dup[1].Name);
            await Task.CompletedTask; return "ok";
        });

        await T("--mcp-config reads a file; a missing file is an error that names the option", async () =>
        {
            var dir = Tmp();
            var path = Path.Combine(dir, "mcp.json");
            File.WriteAllText(path, """{"mcpServers":{"a":{"url":"http://h/mcp"}}}""");
            var s = McpSettings.FromArguments(ArgumentParser.Parse(new[] { "--mcp-config", path, "--mcp", "http://other/mcp" }));
            Assert(s.Select(x => x.Name).SequenceEqual(new[] { "a", "other" }), string.Join(",", s.Select(x => x.Name)));
            string err = "";
            try { McpSettings.FromArguments(ArgumentParser.Parse(new[] { "--mcp-config", Path.Combine(dir, "none.json") })); } catch (McpConfigException ex) { err = ex.Message; }
            Assert(err.Contains("--mcp-config"), err);
            await Task.CompletedTask; return "ok";
        });

        await T("nothing is read from the working folder on its own: a mcp.json there starts nothing", async () =>
        {
            var prev = Directory.GetCurrentDirectory();
            var dir = Tmp();
            File.WriteAllText(Path.Combine(dir, "mcp.json"), """{"mcpServers":{"evil":{"command":"calc"}}}""");
            Directory.SetCurrentDirectory(dir);
            try { Assert(McpSettings.FromArguments(ArgumentParser.Parse(Array.Empty<string>())).Count == 0, "a file in the folder was used"); }
            finally { Directory.SetCurrentDirectory(prev); }
            await Task.CompletedTask; return "ok";
        });

        await T("Resolve adds the --mcp URL when it is not configured yet, and does not duplicate it", async () =>
        {
            McpSettings.Configure(new[] { new McpServerConfig("a", Url: "http://h/mcp") });
            try
            {
                Assert(McpSettings.Resolve("http://h/mcp").Count == 1, "duplicate");
                Assert(McpSettings.Resolve("http://other/mcp").Count == 2 && McpSettings.Resolve(null).Count == 1, "added");
                Assert(McpSettings.Describe("http://h/mcp") == "http://h/mcp", McpSettings.Describe("http://h/mcp"));
            }
            finally { McpSettings.Reset(); }
            await Task.CompletedTask; return "ok";
        });

        await T("agent: two stdio servers; a call to each is confirmed, runs, and the failed server is a warning, not the end", InWorkMcp(async () =>
        {
            McpSettings.Configure(new[] { Echo("alpha"), new McpServerConfig("ghost", Command: "no-such-program-" + Guid.NewGuid().ToString("N")), Echo("beta") });
            try
            {
                int n = 0;
                var warnings = new List<string>();
                using var mock = new MockLlm
                {
                    Handler = req => MockLlm.IsDistill(req) ? (200, MockLlm.Text("{}"), 0)
                        : ++n == 1 ? (200, MockLlm.ToolCall("mcp_beta_echo", "{\"text\":\"hello\"}", "c1"), 0)
                        : (200, MockLlm.Text("done"), 0)
                };
                var msgs = new JsonArray { CsAgent.Core.Agent.CodingAgent.SystemMessage(false), JsonHelpers.Message("user", "use the beta server") };
                var obs = new WarnObserver(warnings);
                using var agent = new CodingAgent("k", mock.BaseUrl, "m", new AgentOptions(Confirm: false), obs, null,
                    new CsAgent.Services.HybridMemoryManager(new CsAgent.Services.SemanticMemory(), new CsAgent.Services.ExactMemory()));
                await agent.RunAsync(msgs, "agent_memory.json");
                Assert(warnings.Any(w => w.Contains("'ghost' is not available")), "no warning: " + string.Join(" / ", warnings));
                var last = mock.Requests.Last(r => !MockLlm.IsDistill(r))["messages"]!.AsArray();
                var tool = last.Last(m => m!["role"]!.GetValue<string>() == "tool")!["content"]!.GetValue<string>();
                Assert(tool.StartsWith("echo:hello"), tool);
                return tool;
            }
            finally { McpSettings.Reset(); }
        }));
    }

    static Func<Func<Task<string?>>, Func<Task<string?>>> InWorkMcp = body => async () =>
    {
        var prev = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(Tmp());
        try { return await body(); }
        finally { Directory.SetCurrentDirectory(prev); }
    };

    sealed class WarnObserver : CsAgent.Core.Abstractions.IAgentObserver
    {
        readonly List<string> _w;
        public WarnObserver(List<string> w) { _w = w; }
        public Task OnStep(int n, int max) => Task.CompletedTask;
        public Task OnThought(string text) => Task.CompletedTask;
        public Task OnToolCall(string name, string args) => Task.CompletedTask;
        public Task OnToolResult(string result, bool isError) => Task.CompletedTask;
        public Task OnDone(string message) => Task.CompletedTask;
        public Task OnError(string message) { _w.Add("ERROR " + message); return Task.CompletedTask; }
        public Task OnWarning(string message) { _w.Add(message); return Task.CompletedTask; }
        public Task OnDanger(string message) => Task.CompletedTask;
        public Task<bool> OnConfirm(string toolName) => Task.FromResult(true);
    }
}
