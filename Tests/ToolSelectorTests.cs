using System.Text.Json.Nodes;
using CsAgent.Core.Agent;
using CsAgent.Services;
using CsAgent.Shared;

namespace CsAgent.Tests;

static partial class Tests
{
    // ═════════════════════════ Only the useful tools are sent at each step ═════════════════════════
    static async Task ToolSelectorGroup()
    {
        Group("Tool selection: only the useful tools are described to the model");

        static HashSet<string> Names(JsonArray a) => a.Select(t => t!["function"]!["name"]!.GetValue<string>()).ToHashSet();
        static int Size(JsonArray a) => a.Sum(t => t!.ToJsonString().Length);
        var native = ToolDispatcher.ToolDefinitions;

        JsonArray For(string text, params string[] used)
        {
            var groups = ToolSelector.Select(text, used);
            return ToolSelector.Build(native, null, groups, false, all: false);
        }

        await T("every native tool is either in core or in exactly one declared group; --all-tools is parsed", async () =>
        {
            var inGroups = ToolSelector.Groups.Values.SelectMany(x => x).ToList();
            Assert(inGroups.Distinct().Count() == inGroups.Count, "a tool is in two groups");
            var all = Names(native);
            Assert(inGroups.All(all.Contains), "a group names a tool that does not exist: " + string.Join(",", inGroups.Where(t => !all.Contains(t))));
            Assert(!ArgumentParser.Parse(Array.Empty<string>()).AllTools && ArgumentParser.Parse(new[] { "--all-tools" }).AllTools, "--all-tools");
            Assert(ArgumentParser.Parse(new[] { "--all-tools", "mem1" }).MemoryFile == "mem1", "not the memory file");
            await Task.CompletedTask; return "ok";
        });

        await T("an ordinary coding request sends core only, far less than all the tools", async () =>
        {
            var sent = For("fix the null reference in Program.cs and run the build");
            var names = Names(sent);
            Assert(names.Contains("read_file") && names.Contains("edit_file") && names.Contains("sh") && names.Contains("search_files"), "core missing");
            Assert(!names.Contains("git_commit") && !names.Contains("read_msg") && !names.Contains("excel_command") && !names.Contains("transcribe_audio"), string.Join(",", names));
            Assert(names.Contains(ToolSelector.MetaToolName), "enable_tools missing");
            int all = Size(native), now = Size(sent);
            Assert(now < all * 0.5, $"{now} of {all}");
            await Task.CompletedTask; return $"{now} of {all} characters ({100 - now * 100 / all}% less)";
        });

        await T("the words of the request switch groups on, in English and in French", async () =>
        {
            bool Has(string text, string tool) => Names(For(text)).Contains(tool);
            Assert(Has("commit these changes", "git_commit"), "git");
            Assert(Has("cherche les actualités sur le web", "web_search"), "web");
            Assert(Has("range les .msg par expéditeur", "group_msg_by_sender"), "mail");
            Assert(Has("transcris entretien.m4a", "transcribe_audio"), "audio");
            Assert(Has("transforme ventes.csv en rapport Excel", "excel_command"), "office");
            Assert(Has("corrige ce que j'ai copié dans le presse-papiers", "read_clipboard"), "clipboard");
            Assert(Has("unzip the archive", "unzip"), "archive");
            Assert(Has("open a terminal session to the dev server", "run_terminal"), "terminal");
            Assert(Has("which models are available", "list_models"), "models");
            Assert(!Has("rename the variable", "git_commit") && !Has("rename the variable", "web_search"), "false positive");
            await Task.CompletedTask; return "ok";
        });

        await T("a group the conversation already used stays on, even for a short follow-up", async () =>
        {
            Assert(Names(For("and the second one?", "read_msg")).Contains("save_attachment"), "mail group should stay on");
            Assert(!Names(For("and the second one?")).Contains("save_attachment"), "off without history");
            await Task.CompletedTask; return "ok";
        });

        await T("enable_tools: listed while a group is off, switches a group on, rejects an unknown one, absent when all are on", async () =>
        {
            var groups = new HashSet<string>();
            var def = ToolSelector.MetaTool(groups, false);
            var desc = def["function"]!["description"]!.GetValue<string>();
            Assert(desc.Contains("git (git_status") && desc.Contains("mail (read_msg"), desc);
            bool mcp = false;
            var ok = ToolSelector.Enable("git", groups, ref mcp, false);
            Assert(ok.StartsWith("OK:") && groups.Contains("git") && ok.Contains("git_commit"), ok);
            Assert(!ToolSelector.MetaTool(groups, false)["function"]!["description"]!.GetValue<string>().Contains("git ("), "git still listed");
            Assert(ToolSelector.Enable("nope", groups, ref mcp, false).StartsWith("Error: enable_tools"), "unknown group");
            Assert(ToolSelector.Enable("mcp", groups, ref mcp, false).StartsWith("Error:") && !mcp, "mcp without server");
            var every = new HashSet<string>(ToolSelector.Groups.Keys);
            Assert(!Names(ToolSelector.Build(native, null, every, false, false)).Contains(ToolSelector.MetaToolName), "meta tool with everything on");
            Assert(Names(ToolSelector.Build(native, null, new HashSet<string>(), false, all: true)).Count == Names(native).Count, "all=true sends everything");
            await Task.CompletedTask; return "ok";
        });

        await T("MCP tools: sent when few; when many only if the request names one or says MCP; the meta tool offers them", async () =>
        {
            JsonObject Tool(string name, int pad) => new() { ["type"] = "function", ["function"] = new JsonObject {
                ["name"] = name, ["description"] = new string('d', pad), ["parameters"] = new JsonObject { ["type"] = "object" } } };
            var few = new JsonArray { Tool("mcp_calendar_list", 100) };
            var many = new JsonArray(Enumerable.Range(0, 20).Select(i => (JsonNode)Tool(i == 0 ? "mcp_calendar_list" : "mcp_other_tool" + i, 600)).ToArray());
            Assert(ToolSelector.McpWanted(few, "fix a bug", false), "few tools are always sent");
            Assert(!ToolSelector.McpWanted(many, "fix a bug", false), "many tools, unrelated request");
            Assert(ToolSelector.McpWanted(many, "what is on my calendar tomorrow", false), "request names calendar");
            Assert(ToolSelector.McpWanted(many, "use the mcp server", false) && ToolSelector.McpWanted(many, "x", true), "mcp / used before");
            var built = ToolSelector.Build(native, many, new HashSet<string>(), mcpOn: false, all: false);
            Assert(!Names(built).Contains("mcp_calendar_list") && ToolSelector.MetaTool(new HashSet<string>(), true)["function"]!["description"]!.GetValue<string>().Contains("mcp ("), "off");
            Assert(Names(ToolSelector.Build(native, many, new HashSet<string>(), mcpOn: true, all: false)).Contains("mcp_calendar_list"), "on");
            await Task.CompletedTask; return "ok";
        });

        // The model's side: what the requests of a run really contain.
        async Task<List<JsonNode>> Run(string user, Func<int, (int, JsonNode, int)> reply, bool all = false)
        {
            var prev = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(Tmp());
            ToolSelector.Configure(all);
            try
            {
                int n = 0;
                using var mock = new MockLlm { Handler = req => MockLlm.IsDistill(req) ? (200, MockLlm.Text("{}"), 0) : reply(++n) };
                var msgs = new JsonArray { CodingAgent.SystemMessage(false), JsonHelpers.Message("user", user) };
                using var agent = new CodingAgent("k", mock.BaseUrl, "m", new AgentOptions(Confirm: false), new SilentObserver(), null,
                    new HybridMemoryManager(new SemanticMemory(), new ExactMemory()));
                await agent.RunAsync(msgs, "agent_memory.json");
                return mock.Requests.Where(r => !MockLlm.IsDistill(r)).ToList();
            }
            finally { Directory.SetCurrentDirectory(prev); ToolSelector.Reset(); }
        }
        static HashSet<string> Sent(JsonNode req) => Names(req["tools"]!.AsArray());

        await T("agent: the first request carries core only; enable_tools switches a group on for the next step", async () =>
        {
            var reqs = await Run("please look at the project", n => n switch
            {
                1 => (200, MockLlm.ToolCall("enable_tools", "{\"group\":\"web\"}", "c1"), 0),
                _ => (200, MockLlm.Text("done"), 0)
            });
            Assert(reqs.Count == 2, "requests=" + reqs.Count);
            Assert(!Sent(reqs[0]).Contains("web_search") && Sent(reqs[0]).Contains(ToolSelector.MetaToolName), "first request: " + string.Join(",", Sent(reqs[0])));
            Assert(Sent(reqs[1]).Contains("web_search") && Sent(reqs[1]).Contains("fetch_url"), "second request has no web tools");
            var toolMsg = reqs[1]["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "tool")!["content"]!.GetValue<string>();
            Assert(toolMsg.StartsWith("OK:") && toolMsg.Contains("web_search"), toolMsg);
            await Task.CompletedTask; return $"{Sent(reqs[0]).Count} tools, then {Sent(reqs[1]).Count}";
        });

        await T("agent: a tool called although its group was off keeps the group on afterwards", async () =>
        {
            var reqs = await Run("tidy up", n => n switch
            {
                1 => (200, MockLlm.ToolCall("git_status", "{}", "c1"), 0),
                _ => (200, MockLlm.Text("done"), 0)
            });
            Assert(!Sent(reqs[0]).Contains("git_log") && Sent(reqs[1]).Contains("git_log"), "git group should be on at step 2");
            await Task.CompletedTask; return "ok";
        });

        await T("agent: --all-tools sends every tool and no enable_tools", async () =>
        {
            var reqs = await Run("please look at the project", n => (200, MockLlm.Text("done"), 0), all: true);
            var sent = Sent(reqs[0]);
            Assert(sent.Count == Names(native).Count && !sent.Contains(ToolSelector.MetaToolName), "count=" + sent.Count);
            await Task.CompletedTask; return $"{sent.Count} tools";
        });
    }
}
