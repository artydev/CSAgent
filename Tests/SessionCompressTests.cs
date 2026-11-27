using System.Text.Json.Nodes;
using CsAgent.Core.Agent;
using CsAgent.Core.Memory;
using CsAgent.Services;
using CsAgent.Shared;

namespace CsAgent.Tests;

static partial class Tests
{
    // Long sessions: aichat-style compression (cut point, recap protection, .empty, flags).
    static async Task SessionCompressGroup()
    {
        Group("Long sessions: compression cut point, recap kept by TrimHistory, --compress-threshold");
        await Task.CompletedTask;

        JsonArray Convo(int turns)
        {
            var m = new JsonArray { CodingAgent.SystemMessage(false) };
            for (int i = 0; i < turns; i++)
            {
                m.Add(JsonHelpers.Message("user", "request " + i));
                m.Add(new JsonObject { ["role"] = "assistant", ["content"] = "ok " + i });
            }
            return m;
        }

        await T("FindCut lands on a user message and keeps at most keepRecent messages", async () =>
        {
            await Task.CompletedTask;
            var m = Convo(20);                              // 41 messages
            var cut = SessionCompressor.FindCut(m, 12);
            Assert(cut > 1, "cut=" + cut);
            Assert(m[cut]!["role"]!.GetValue<string>() == "user", "cut is not a user message");
            Assert(m.Count - cut <= 13, "kept " + (m.Count - cut));
            return $"cut at {cut}, {m.Count - cut} kept";
        });

        await T("FindCut never separates assistant(tool_calls) from its tool results", async () =>
        {
            await Task.CompletedTask;
            var m = new JsonArray { CodingAgent.SystemMessage(false), JsonHelpers.Message("user", "a"), JsonHelpers.Message("user", "b") };
            m.Add(new JsonObject { ["role"] = "assistant", ["content"] = "", ["tool_calls"] = new JsonArray() });
            for (int i = 0; i < 20; i++) m.Add(ToolMsg("c" + i, "r"));
            var cut = SessionCompressor.FindCut(m, 5);
            Assert(cut < 0 || m[cut]!["role"]!.GetValue<string>() == "user", "cut inside a tool loop: " + cut);
            return "cut=" + cut;
        });

        await T("TrimHistory evicts the recap last", async () =>
        {
            await Task.CompletedTask;
            var m = Convo(10);
            m.Insert(1, JsonHelpers.Message("user", SessionCompressor.Marker + "\nrecap"));
            JsonHelpers.TrimHistory(m, 60);
            Assert(SessionCompressor.IsSummary(m[1]), "recap was evicted first");
            return "recap kept";
        });

        await T("TrimHistory keeps the recap even behind an injected [SESSION CONTEXT] block", async () =>
        {
            await Task.CompletedTask;
            var m = Convo(10);
            m.Insert(1, JsonHelpers.Message("user", SessionCompressor.Marker + "\nrecap"));
            m.Insert(1, JsonHelpers.Message("user", "[SESSION CONTEXT]\nnotes"));
            JsonHelpers.TrimHistory(m, 60);
            Assert(m[1]!["content"]!.GetValue<string>().StartsWith("[SESSION CONTEXT]"), "context block evicted");
            Assert(SessionCompressor.IsSummary(m[2]), "recap evicted");
            return "context block and recap kept";
        });

        await T("FindCut inside one long tool loop cuts at an assistant message, keeping call/result pairs whole", async () =>
        {
            await Task.CompletedTask;
            var m = new JsonArray { CodingAgent.SystemMessage(false), JsonHelpers.Message("user", "do the whole task") };
            for (int i = 0; i < 10; i++)
            {
                m.Add(new JsonObject { ["role"] = "assistant", ["content"] = "", ["tool_calls"] = new JsonArray() });
                m.Add(ToolMsg("c" + i, "result " + i));
            }
            var cut = SessionCompressor.FindCut(m, 12);
            Assert(cut >= 2 && m[cut]!["role"]!.GetValue<string>() == "assistant", "cut=" + cut);
            Assert(m[cut - 1]!["role"]!.GetValue<string>() == "tool", "the message before the cut is not a tool result");
            return "cut at " + cut;
        });

        await T("--no-save: nothing is written, except by an explicit force (.save <name>)", async () =>
        {
            var dir = Directory.CreateTempSubdirectory("csagent-nosave-");
            try
            {
                var conv = Path.Combine(dir.FullName, "conversation.json");
                var exact = Path.Combine(dir.FullName, "exact.json");
                var msgs = Convo(3);
                SessionPersistence.Configure(false);
                await MemoryStore.SaveAsync(conv, msgs);
                await new ExactMemory().SaveAsync(exact);
                Assert(!File.Exists(conv) && !File.Exists(exact), "a file was written under --no-save");
                await MemoryStore.SaveAsync(conv, msgs, force: true);
                Assert(File.Exists(conv), "force did not write");
                SessionPersistence.Configure(true);
                await new ExactMemory().SaveAsync(exact);
                Assert(File.Exists(exact), "normal save does not write");
                return "gated and forced";
            }
            finally { SessionPersistence.Reset(); dir.Delete(true); }
        });

        await T("--no-save is parsed, and turns the end-of-run summary off", async () =>
        {
            await Task.CompletedTask;
            var a = ArgumentParser.Parse(new[] { "--no-save" });
            Assert(a.NoSave && !a.Distill, "NoSave=" + a.NoSave + " Distill=" + a.Distill);
            var b = ArgumentParser.Parse(Array.Empty<string>());
            Assert(!b.NoSave && b.Distill, "defaults changed");
            return "parsed";
        });

        await T("Empty keeps only the system message; ShouldCompress follows the threshold", async () =>
        {
            await Task.CompletedTask;
            var m = Convo(10);
            Assert(SessionCompressor.ShouldCompress(m, 10), "should compress");
            Assert(!SessionCompressor.ShouldCompress(m, 1_000_000), "should not compress");
            SessionCompressor.Empty(m);
            Assert(m.Count == 1, "count=" + m.Count);
            return "emptied";
        });

        await T("--compress-threshold / --no-compress / --session are parsed", async () =>
        {
            await Task.CompletedTask;
            var a = ArgumentParser.Parse(new[] { "--session", "work", "--compress-threshold", "40000", "--no-compress" });
            Assert(a.MemoryFile == "work", "mem=" + a.MemoryFile);
            Assert(a.CompressChars == 40000 && !a.Compress, "flags");
            ContextBudget.Configure(96_000, true);
            ContextBudget.ConfigureCompression(500_000, true);
            Assert(ContextBudget.CompressChars < ContextBudget.MaxChars, "threshold must stay below MaxChars");
            ContextBudget.Reset();
            return "flags parsed";
        });
    }
}
