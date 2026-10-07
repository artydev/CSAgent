using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsAgent.Core.Abstractions;
using CsAgent.Core.Agent;
using CsAgent.Core.Llm;
using CsAgent.Core.Memory;
using CsAgent.Services;
using CsAgent.Shared;

namespace CsAgent.Tests;

static partial class Tests
{
    // ═════════════════════════ Agent end-to-end with a mock LLM ═════════════════════════
    static async Task AgentEndToEnd()
    {
        Group("End-to-end / CodingAgent with a mock LLM");

        string work = "";
        var memFile = "agent_memory.json";
        var mp = MemoryPaths.Resolve(memFile);   // -> folder agent_memory/
        string Content(JsonNode? m) => m?["content"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

        // Each test gets its own working directory; the current directory is restored afterwards.
        Func<Func<Task<string?>>, Func<Task<string?>>> InWork = body => async () =>
        {
            var prevCwd = Directory.GetCurrentDirectory();
            work = Tmp();
            Directory.SetCurrentDirectory(work);
            try
            {
                await File.WriteAllTextAsync(Path.Combine(work, "exists.txt"), "SECRET-FILE-BODY");
                return await body();
            }
            finally { Directory.SetCurrentDirectory(prevCwd); }
        };

        await T("tool-call turns (no text) retrieve the relevant pattern; ONE [MEMORY] block; solution stored with args only", InWork(async () =>
        {
            if (Directory.Exists(mp.Directory)) Directory.Delete(mp.Directory, recursive: true);
            int n = 0;
            var mock = new MockLlm
            {
                Handler = req =>
                {
                    n++;
                    return n switch
                    {
                        1 => (200, MockLlm.ToolCall("read_file", "{\"path\":\"missing_config.txt\"}"), 0),      // error
                        2 => (200, MockLlm.ToolCall("read_file", "{\"path\":\"exists.txt\"}"), 0),              // success after error -> solution
                        3 => (200, MockLlm.ToolCall("list_dir", "{\"path\":\".\"}"), 0),
                        4 => (200, MockLlm.ToolCall("list_dir", "{\"path\":\".\"}"), 0),
                        5 => (200, MockLlm.ToolCall("read_file", "{\"path\":\"missing_config.txt\"}"), 0),      // same error again
                        6 => (200, MockLlm.ToolCall("read_file", "{\"path\":\"missing_config.txt\"}"), 0),
                        _ => (200, MockLlm.Text("done"), 0),
                    };
                }
            };
            var msgs = new JsonArray { CodingAgent.SystemMessage(false), JsonHelpers.Message("user", "go") };
            using var agent = new CodingAgent("k", mock.BaseUrl, "m", new AgentOptions(Confirm: false), new SilentObserver(), null,
                new HybridMemoryManager(new SemanticMemory(), new ExactMemory()));
            await agent.RunAsync(msgs, memFile);

            // 1.2: step 5 follows read_file(missing_config.txt)
            var blk = Content(mock.Requests[5]["messages"]!.AsArray().Last(m => Content(m).StartsWith("[MEMORY]")));
            Assert(blk.Contains("LEARNED PATTERNS") && blk.Contains("missing_config.txt"), "no relevant pattern:\n" + blk);
            // 1.3
            Assert(msgs.Count(m => Content(m).StartsWith("[MEMORY]")) <= 1, "[MEMORY] accumulated");
            foreach (var r in mock.Requests.Skip(3))
                Assert(r["messages"]!.AsArray().Count(m => Content(m).StartsWith("[MEMORY]")) <= 1, "several [MEMORY] blocks in one request");
            // Phase 2: semantic file content
            var raw = File.ReadAllText(mp.Semantic);
            var arr = JsonNode.Parse(raw)!.AsArray();
            Assert(!raw.Contains("SECRET-FILE-BODY"), "file content leaked into semantic file");
            var errs = arr.Where(x => x!["type"]!.GetValue<string>() == "error_pattern").ToList();
            Assert(errs.Count == 1 && errs[0]!["count"]!.GetValue<int>() == 3, "error entries=" + errs.Count + " count=" + (errs.FirstOrDefault()?["count"]));
            var sols = arr.Where(x => x!["type"]!.GetValue<string>() == "solution").ToList();
            Assert(sols.Count == 1 && sols[0]!["summary"]!.GetValue<string>().Contains("exists.txt"), "solutions=" + sols.Count);
            return $"{arr.Count} semantic entries (1 error x3, 1 solution), no file content, single [MEMORY]";
        }));

        await T("long run: semantic file stays small (30 successful reads, no errors)", InWork(async () =>
        {
            if (Directory.Exists(mp.Directory)) Directory.Delete(mp.Directory, recursive: true);
            int n = 0;
            var mock = new MockLlm { Handler = req => (++n > 30) ? (200, MockLlm.Text("done"), 0) : (200, MockLlm.ToolCall("read_file", "{\"path\":\"exists.txt\"}"), 0) };
            var msgs = new JsonArray { CodingAgent.SystemMessage(false), JsonHelpers.Message("user", "go") };
            using var agent = new CodingAgent("k", mock.BaseUrl, "m", new AgentOptions(MaxSteps: 40, Confirm: false), new SilentObserver(), null,
                new HybridMemoryManager(new SemanticMemory(), new ExactMemory()));
            await agent.RunAsync(msgs, memFile);
            var count = JsonNode.Parse(File.ReadAllText(mp.Semantic))!.AsArray().Count;
            Assert(count == 0, "semantic entries: " + count);
            Assert(msgs.Count(m => Content(m).StartsWith("[MEMORY]")) <= 1, "[MEMORY] accumulated");
            return "0 semantic entries after 30 successes";
        }));

        await T("agent starts and finishes normally with ALL THREE memory files corrupt", InWork(async () =>
        {
            if (Directory.Exists(mp.Directory)) Directory.Delete(mp.Directory, recursive: true);
            Directory.CreateDirectory(mp.Directory);
            File.WriteAllText(mp.Conversation, "[{broken");
            File.WriteAllText(mp.Exact, "");
            File.WriteAllText(mp.Semantic, "{\"not\":\"an array\"}");
            int n = 0;
            var mock = new MockLlm { Handler = req => (++n > 2) ? (200, MockLlm.Text("done"), 0) : (200, MockLlm.ToolCall("list_dir", "{\"path\":\".\"}"), 0) };
            var obs = new SilentObserver();
            var msgs = await MemoryStore.LoadAsync(mp.Conversation);
            if (msgs.Count == 0) msgs.Add(CodingAgent.SystemMessage(false));
            msgs.Add(JsonHelpers.Message("user", "go"));
            using var agent = new CodingAgent("k", mock.BaseUrl, "m", new AgentOptions(Confirm: false), obs, null,
                new HybridMemoryManager(new SemanticMemory(), new ExactMemory()));
            await agent.RunAsync(msgs, memFile);
            Assert(!obs.Results.Any(r => r.StartsWith("AGENT-ERROR")), string.Join(";", obs.Results));
            Assert(File.Exists(mp.Conversation + ".bad") && File.Exists(mp.Exact + ".bad") && File.Exists(mp.Semantic + ".bad"), "not all quarantined");
            Assert(JsonNode.Parse(File.ReadAllText(mp.Conversation)) is JsonArray, "fresh conversation file");
            return "run completed; 3 files quarantined as .bad";
        }));

    }
}
