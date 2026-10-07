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
    // ═════════════════════════ One folder per memory ═════════════════════════
    static async Task MemoryFolder()
    {
        Group("Memory folder / naming");

        await T("--mem name -> folder with conversation.json, exact.json, semantic.json, summary.json", async () =>
        {
            var p = MemoryPaths.Resolve("my_task");
            Assert(p.Directory == "my_task", "dir: " + p.Directory);
            Assert(p.Conversation == Path.Combine("my_task", "conversation.json"), p.Conversation);
            Assert(p.Exact == Path.Combine("my_task", "exact.json"), p.Exact);
            Assert(p.Semantic == Path.Combine("my_task", "semantic.json"), p.Semantic);
            Assert(p.Summary == Path.Combine("my_task", "summary.json"), p.Summary);
            return "ok";
            await Task.CompletedTask;
        });

        await T("an old-style name ending in .json gives the same folder; separators and blanks are tolerated", async () =>
        {
            Assert(MemoryPaths.Resolve("my_task.json").Directory == "my_task", "x.json");
            Assert(MemoryPaths.Resolve("MY_TASK.JSON").Directory == "MY_TASK", "upper-case extension");
            Assert(MemoryPaths.Resolve("my_task/").Directory == "my_task", "trailing slash");
            Assert(MemoryPaths.Resolve("my_task\\").Directory == "my_task", "trailing backslash");
            Assert(MemoryPaths.Resolve(Path.Combine("a", "b", "task.json")).Directory == Path.Combine("a", "b", "task"), "nested path");
            Assert(MemoryPaths.Resolve("").Directory == "agent_memory" && MemoryPaths.Resolve(null).Directory == "agent_memory" && MemoryPaths.Resolve("  ").Directory == "agent_memory", "blank -> default");
            Assert(MemoryPaths.Resolve(".json").Directory == "agent_memory", "only an extension -> default");
            return "ok";
            await Task.CompletedTask;
        });

        await T("the default memory is the folder agent_memory", async () =>
        {
            Assert(ArgumentParser.Parse(Array.Empty<string>()).MemoryFile == "agent_memory", "default arg");
            Assert(ArgumentParser.Parse(new[] { "--mem", "x.json" }).MemoryFile == "x.json", "--mem is kept as typed");
            Assert(MemoryPaths.Resolve(ArgumentParser.Parse(Array.Empty<string>()).MemoryFile).Conversation == Path.Combine("agent_memory", "conversation.json"), "resolved default");
            return "ok";
            await Task.CompletedTask;
        });

        Group("Memory folder / on disk");

        await T("a save creates the folder (and parents) by itself", async () =>
        {
            var d = Tmp(); var p = MemoryPaths.Resolve(Path.Combine(d, "deep", "er", "task"));
            Assert(!Directory.Exists(p.Directory), "folder should not exist yet");
            var m = new HybridMemoryManager(new SemanticMemory(), new ExactMemory());
            m.RecordError(1, "sh", "Error: x");
            await m.SaveAsync(p);
            await MemoryStore.SaveAsync(p.Conversation, new JsonArray { JsonHelpers.Message("user", "hi") });
            Assert(File.Exists(p.Semantic) && File.Exists(p.Exact) && File.Exists(p.Conversation), "files not created");
            return "created";
        });

        await T("a whole agent run leaves exactly conversation/exact/semantic/summary in the folder, and nothing beside it", async () =>
        {
            var d = Tmp();
            var prev = Directory.GetCurrentDirectory(); Directory.SetCurrentDirectory(d);
            try
            {
                int n = 0;
                using var mock = new MockLlm();
                mock.Handler = req => MockLlm.IsDistill(req) ? (200, MockLlm.Text(GoodJson), 0)
                    : Interlocked.Increment(ref n) switch { 1 => (200, MockLlm.ToolCall("list_dir", "{\"path\":\".\"}"), 0), _ => (200, MockLlm.Text("done"), 0) };
                var msgs = new JsonArray { CodingAgent.SystemMessage(false), JsonHelpers.Message("user", "go") };
                using var agent = new CodingAgent("k", mock.BaseUrl, "m", new AgentOptions(Confirm: false), new SilentObserver(), null,
                    new HybridMemoryManager(new SemanticMemory(), new ExactMemory()));
                await agent.RunAsync(msgs, "my_task");

                var inFolder = Directory.GetFiles("my_task").Select(Path.GetFileName).OrderBy(x => x).ToArray();
                var expected = new[] { "conversation.json", "exact.json", "semantic.json", "summary.json" };
                Assert(inFolder.SequenceEqual(expected), "folder holds: " + string.Join(", ", inFolder));
                var beside = Directory.GetFileSystemEntries(d).Select(Path.GetFileName).Where(x => x != "my_task").ToArray();
                Assert(beside.Length == 0, "stray entries next to the folder: " + string.Join(", ", beside));
                return "4 files in my_task/, nothing else";
            }
            finally { Directory.SetCurrentDirectory(prev); }
        });

        await T("two memory names never share a file", async () =>
        {
            var d = Tmp();
            var a = MemoryPaths.Resolve(Path.Combine(d, "task_a")); var b = MemoryPaths.Resolve(Path.Combine(d, "task_b"));
            var mA = new HybridMemoryManager(new SemanticMemory(), new ExactMemory());
            mA.RecordError(1, "sh", "Error: only in A");
            await mA.SaveAsync(a);
            var mB = new HybridMemoryManager(new SemanticMemory(), new ExactMemory());
            await mB.LoadAsync(b);
            Assert(mB.GetContext("only in A").Patterns.Count == 0, "task B saw a lesson of task A");
            Assert(!Directory.Exists(b.Directory), "loading must not create the folder");
            return "isolated";
        });
    }
}
