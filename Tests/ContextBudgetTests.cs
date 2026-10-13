using System.Text.Json.Nodes;
using CsAgent.Core.Agent;
using CsAgent.Services;
using CsAgent.Shared;

namespace CsAgent.Tests;

static partial class Tests
{
    static JsonNode ToolMsg(string id, string content) => new JsonObject
    {
        ["role"] = "tool", ["tool_call_id"] = id, ["content"] = content
    };

    // Old tool results are cut so that each step re-sends less history; --context-chars / --no-compact.
    static async Task ContextBudgetGroup()
    {
        Group("Context size: old tool results, --context-chars, --no-compact");
        CsAgent.Core.Llm.Loc.Set("en");

        await T("CompactToolResults: only the old, long results are cut; the last ones, short ones and other roles are untouched", async () =>
        {
            await Task.CompletedTask;
            var big = new string('x', 5000);
            var msgs = new JsonArray { CodingAgent.SystemMessage(false), JsonHelpers.Message("user", big) };
            for (int i = 1; i <= 8; i++) msgs.Add(ToolMsg("c" + i, i == 2 ? "short" : big));
            int changed = JsonHelpers.CompactToolResults(msgs, keepRecent: 3, maxChars: 1500);
            // tool messages 1..5 are old; number 2 is short, so 4 are cut
            Assert(changed == 4, "changed=" + changed);
            string C(int i) => msgs[1 + i]!["content"]!.GetValue<string>();   // tool message i is at index 1 + i
            foreach (var i in new[] { 1, 3, 4, 5 })
                Assert(C(i).StartsWith(new string('x', 1500)) && C(i).Contains("3,500 of 5,000 characters left out") && C(i).Length < 1800, $"tool {i}: {C(i).Length}");
            Assert(C(2) == "short", "short result changed");
            foreach (var i in new[] { 6, 7, 8 }) Assert(C(i) == big, $"recent tool {i} changed");
            Assert(msgs[1]!["content"]!.GetValue<string>() == big && msgs[0]!["role"]!.GetValue<string>() == "system", "user/system changed");
            Assert(Enumerable.Range(1, 8).All(i => msgs[1 + i]!["tool_call_id"]!.GetValue<string>() == "c" + i), "ids or order changed");
            return "4 cut, 4 kept";
        });

        await T("CompactToolResults: running it again changes nothing", async () =>
        {
            await Task.CompletedTask;
            var msgs = new JsonArray { CodingAgent.SystemMessage(false) };
            for (int i = 1; i <= 6; i++) msgs.Add(ToolMsg("c" + i, new string('y', 4000)));
            Assert(JsonHelpers.CompactToolResults(msgs, 2, 1000) == 4, "first pass");
            var before = msgs.ToJsonString();
            Assert(JsonHelpers.CompactToolResults(msgs, 2, 1000) == 0 && msgs.ToJsonString() == before, "second pass changed something");
            return "idempotent";
        });

        await T("CompactToolResults: a cut never splits a surrogate pair", async () =>
        {
            await Task.CompletedTask;
            var msgs = new JsonArray { CodingAgent.SystemMessage(false), ToolMsg("a", new string('a', 999) + string.Concat(Enumerable.Repeat("😀", 1500))), ToolMsg("b", "x") };
            JsonHelpers.CompactToolResults(msgs, 1, 1000);
            var s = msgs[1]!["content"]!.GetValue<string>();
            var head = s[..s.IndexOf('\n')];
            Assert(!char.IsHighSurrogate(head[^1]), "ends on half a character");
            return "ok";
        });

        await T("Arguments: --context-chars and --no-compact, with defaults, and the value is not taken for the memory name", async () =>
        {
            await Task.CompletedTask;
            var d = ArgumentParser.Parse(Array.Empty<string>());
            Assert(d.ContextChars == 96_000 && d.CompactToolResults, "defaults");
            var a = ArgumentParser.Parse(new[] { "--context-chars", "20000", "--no-compact", "mymem" });
            Assert(a.ContextChars == 20000 && !a.CompactToolResults, "values");
            Assert(a.MemoryFile == "mymem", "memory name = " + a.MemoryFile);
            Assert(ArgumentParser.Parse(new[] { "--context-chars", "abc" }).ContextChars == 96_000, "bad value not ignored");
            return "ok";
        });

        await T("ContextBudget: a tiny value is raised to the minimum", async () =>
        {
            await Task.CompletedTask;
            try
            {
                ContextBudget.Configure(100, true);
                Assert(ContextBudget.MaxChars == ContextBudget.MinMaxChars, "max=" + ContextBudget.MaxChars);
                ContextBudget.Configure(50_000, false);
                Assert(ContextBudget.MaxChars == 50_000 && !ContextBudget.Compact, "set");
            }
            finally { ContextBudget.Reset(); }
            return "ok";
        });

        // Nine reads of a 10 000-character file: with the default, the three oldest results are cut in what the
        // model receives at the end; with --no-compact, none is cut (the oldest is dropped by the size limit instead).
        async Task<List<string>> RunNineReads(bool compact)
        {
            var prev = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(Tmp());
            try
            {
                ContextBudget.Configure(ContextBudget.DefaultMaxChars, compact);
                File.WriteAllText("big.txt", new string('z', 10_000));
                int n = 0;
                using var mock = new MockLlm
                {
                    Handler = req => MockLlm.IsDistill(req) ? (200, MockLlm.Text("{}"), 0)
                        : ++n <= 9 ? (200, MockLlm.ToolCall("read_file", "{\"path\":\"big.txt\"}", "c" + n), 0)
                        : (200, MockLlm.Text("done"), 0)
                };
                var msgs = new JsonArray { CodingAgent.SystemMessage(false), JsonHelpers.Message("user", "read it often") };
                using var agent = new CodingAgent("k", mock.BaseUrl, "m", new AgentOptions(Confirm: false), new SilentObserver(), null,
                    new HybridMemoryManager(new SemanticMemory(), new ExactMemory()));
                await agent.RunAsync(msgs, "agent_memory.json");
                var last = mock.Requests.Last(r => !MockLlm.IsDistill(r))["messages"]!.AsArray();
                return last.Where(m => m!["role"]?.GetValue<string>() == "tool").Select(m => m!["content"]!.GetValue<string>()).ToList();
            }
            finally { Directory.SetCurrentDirectory(prev); ContextBudget.Reset(); }
        }

        await T("agent: by default, the model receives the oldest tool results cut and the last six whole", async () =>
        {
            var tools = await RunNineReads(compact: true);
            Assert(tools.Count == 9, "tool messages=" + tools.Count);
            Assert(tools.Take(3).All(t => t.Contains("characters left out") && t.Length < 2000), "old results not cut: " + string.Join(",", tools.Select(t => t.Length)));
            Assert(tools.Skip(3).All(t => !t.Contains("characters left out") && t.Length >= 10_000), "recent results cut: " + string.Join(",", tools.Select(t => t.Length)));
            return string.Join(",", tools.Select(t => t.Length));
        });

        await T("agent: --no-compact keeps the results it sends whole", async () =>
        {
            var tools = await RunNineReads(compact: false);
            // Nothing is cut, so 9 x 10 000 characters pass the 96 000 limit and the oldest result is dropped instead.
            Assert(tools.Count == 8 && tools.All(t => !t.Contains("characters left out") && t.Length >= 10_000), string.Join(",", tools.Select(t => t.Length)));
            return "8 whole (the oldest dropped by the size limit)";
        });
    }
}
