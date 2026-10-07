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
    static async Task NoDistill()
    {
        Group("Distillation / --no-distill");

        await T("ArgumentParser: --no-distill off by default, parsed anywhere, not taken as the memory file", async () =>
        {
            Assert(ArgumentParser.Parse(Array.Empty<string>()).Distill, "default should be on");
            var a = ArgumentParser.Parse(new[] { "--no-distill" });
            Assert(!a.Distill && a.MemoryFile == "agent_memory", "flag alone: " + a.MemoryFile);
            var b = ArgumentParser.Parse(new[] { "--ui", "--no-distill", "--mem", "x.json", "--port", "6000" });
            Assert(!b.Distill && b.MemoryFile == "x.json" && b.Port == 6000 && b.IsUiMode, "combined");
            var c = ArgumentParser.Parse(new[] { "my.json", "--no-distill" });
            Assert(!c.Distill && c.MemoryFile == "my.json", "positional + flag: " + c.MemoryFile);
            return "ok";
            await Task.CompletedTask;
        });

        await T("agent run with Distill:false -> no distillation call, memory layers still saved, existing summary still injected, no summary written", async () =>
        {
            var d = Tmp(); var memFile = Path.Combine(d, "agent_memory.json");
            var mp = MemoryPaths.Resolve(memFile); var sumPath = mp.Summary;
            Directory.CreateDirectory(mp.Directory);
            var seed = "{\"decisions\":[\"PRIOR-DECISION\"],\"constraints\":[],\"pending\":[],\"failed_approaches\":[],\"created_at\":\"2026-01-01T00:00:00.0000000Z\"}";
            File.WriteAllText(sumPath, seed);
            var prev = Directory.GetCurrentDirectory(); Directory.SetCurrentDirectory(d);
            try
            {
                int n = 0;
                using var mock = new MockLlm();
                mock.Handler = req => Interlocked.Increment(ref n) switch
                {
                    1 => (200, MockLlm.ToolCall("list_dir", "{\"path\":\".\"}"), 0),
                    _ => (200, MockLlm.Text("done"), 0),
                };
                var msgs = new JsonArray { CodingAgent.SystemMessage(false), JsonHelpers.Message("user", "go") };
                var obs = new SilentObserver();
                using var agent = new CodingAgent("k", mock.BaseUrl, "m", new AgentOptions(Confirm: false, Distill: false), obs, null,
                    new HybridMemoryManager(new SemanticMemory(), new ExactMemory()));
                await agent.RunAsync(msgs, memFile);

                Assert(!mock.Requests.Any(MockLlm.IsDistill), "a distillation request was sent");
                Assert(mock.Requests.Count == 2, "expected 2 chat requests, got " + mock.Requests.Count);
                Assert(mock.Requests.All(r => Content(r["messages"]![1]).StartsWith("[SESSION CONTEXT]")), "existing summary not injected");
                Assert(File.ReadAllText(sumPath) == seed, "summary file was modified");
                Assert(File.Exists(mp.Semantic) && File.Exists(mp.Exact), "memory layers not saved");
                Assert(!obs.Warnings.Any(w => w.Contains("updated") || w.Contains("unchanged") || w.Contains("not updated")), "distillation notice shown: " + string.Join("|", obs.Warnings));
                return "2 chat requests, 0 distill requests, summary untouched but injected";
            }
            finally { Directory.SetCurrentDirectory(prev); }
        });

        await T("agent run with default options still distils (control)", async () =>
        {
            var d = Tmp(); var memFile = Path.Combine(d, "agent_memory.json");
            var prev = Directory.GetCurrentDirectory(); Directory.SetCurrentDirectory(d);
            try
            {
                int n = 0;
                using var mock = new MockLlm();
                mock.Handler = req => MockLlm.IsDistill(req) ? (200, MockLlm.Text(SumGood()), 0)
                    : Interlocked.Increment(ref n) switch { 1 => (200, MockLlm.ToolCall("list_dir", "{\"path\":\".\"}"), 0), _ => (200, MockLlm.Text("done"), 0) };
                var msgs = new JsonArray { CodingAgent.SystemMessage(false), JsonHelpers.Message("user", "go") };
                using var agent = new CodingAgent("k", mock.BaseUrl, "m", new AgentOptions(Confirm: false), new SilentObserver(), null,
                    new HybridMemoryManager(new SemanticMemory(), new ExactMemory()));
                await agent.RunAsync(msgs, memFile);
                Assert(mock.Requests.Any(MockLlm.IsDistill) && File.Exists(MemoryPaths.Resolve(memFile).Summary), "default run did not distil");
                return "distilled";
            }
            finally { Directory.SetCurrentDirectory(prev); }
        });
    }
}
