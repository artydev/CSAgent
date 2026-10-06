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
    // ═════════════════════════ Exact / Semantic / Hybrid basics ═════════════════════════
    static async Task MemoryLayers()
    {
        Group("Hybrid / ExactMemory");

        await T("Add truncates content to 500 chars + ellipsis", async () =>
        {
            var e = new ExactMemory(); e.Add(1, "success", new string('x', 900), "read_file");
            var c = e.GetRecent(1)[0].Content;
            Assert(c.Length == 501 && c.EndsWith("…"), $"len={c.Length}");
            return $"stored length {c.Length}";
        });

        await T("Ring buffer keeps only newest 50 entries", async () =>
        {
            var e = new ExactMemory();
            for (int i = 1; i <= 80; i++) e.Add(i, "success", "r" + i, "t");
            var all = e.GetRecent(1000);
            Assert(all.Count == 50, $"count={all.Count}");
            Assert(all.First().Step == 31 && all.Last().Step == 80, $"range {all.First().Step}..{all.Last().Step}");
            return "50 entries, steps 31..80";
        });

        await T("GetRecentErrors / GetRecentSuccesses filter by type and return newest", async () =>
        {
            var e = new ExactMemory();
            e.Add(1, "error", "e1", "a"); e.Add(2, "success", "s2", "a");
            e.Add(3, "error", "e3", "a"); e.Add(4, "error", "e4", "a"); e.Add(5, "success", "s5", "a");
            var errs = e.GetRecentErrors(2); var oks = e.GetRecentSuccesses(2);
            Assert(errs.All(x => x.Type == "error") && errs.Select(x => x.Step).SequenceEqual(new[] { 3, 4 }), "errors " + string.Join(",", errs.Select(x => x.Step)));
            Assert(oks.All(x => x.Type == "success") && oks.Select(x => x.Step).SequenceEqual(new[] { 2, 5 }), "successes " + string.Join(",", oks.Select(x => x.Step)));
            return "ok";
        });

        await T("Save/Load round-trip preserves entries (reflection disabled)", async () =>
        {
            var d = Tmp(); var p = Path.Combine(d, "e.json");
            var e = new ExactMemory(); e.Add(1, "error", "Permission denied", "write_file"); e.Add(2, "success", "ok", "write_file");
            await e.SaveAsync(p);
            var e2 = new ExactMemory(); await e2.LoadAsync(p);
            var r = e2.GetRecent(10);
            Assert(r.Count == 2 && r[0].ToolName == "write_file" && r[0].Content == "Permission denied", "mismatch");
            return "2 entries restored";
        });

        Group("Hybrid / SemanticMemory");

        await T("AddPattern + Search by summary text and by tag", async () =>
        {
            var s = new SemanticMemory();
            s.AddPattern("error_pattern", "[write_file] Permission denied /opt", new[] { "error", "permission", "file-ops" });
            s.AddPattern("solution", "[write_file] succeeded /home", new[] { "solution", "file-ops" });
            Assert(s.Search("permission").Count == 1, "tag/summary 'permission'");
            Assert(s.Search("file-ops").Count == 2, "tag 'file-ops'");
            Assert(s.Search("/opt").Count == 1, "summary '/opt'");
            Assert(s.Search("zzz").Count == 0, "no match");
            return "ok";
        });

        await T("Save/Load round-trip with tags + context (no JsonSerializer reflection)", async () =>
        {
            var d = Tmp(); var p = Path.Combine(d, "s.json");
            var s = new SemanticMemory();
            s.AddPattern("error_pattern", "boom", new[] { "a", "b" }, new() { { "tool", "sh" }, { "step", "3" } });
            await s.SaveAsync(p);
            var s2 = new SemanticMemory(); await s2.LoadAsync(p);
            var hit = s2.Search("boom").Single();
            Assert(hit.Tags.SequenceEqual(new[] { "a", "b" }), "tags");
            Assert(hit.Context!["tool"] == "sh" && hit.Context["step"] == "3", "context");
            return "restored 1 pattern with tags and context";
        });

        Group("Hybrid / HybridMemoryManager");

        await T("RecordError classifies error type and domain tags", async () =>
        {
            var m = new HybridMemoryManager(new SemanticMemory(), new ExactMemory());
            m.RecordError(1, "write_file", "Error: Permission denied");
            m.RecordError(2, "read_file", "Error: not found 'x'");
            m.RecordError(3, "sh", "timeout after 60s");
            var ctx = m.GetContext("permission");
            Assert(ctx.Patterns.Count == 1 && ctx.Patterns[0].Tags.Contains("permission") && ctx.Patterns[0].Tags.Contains("file-ops"), "permission tags");
            Assert(m.GetContext("not-found").Patterns.Count == 1, "not-found class");
            Assert(m.GetContext("timeout").Patterns.Any(p => p.Tags.Contains("shell")), "shell domain");
            return "permission / not-found / timeout classified";
        });

        await T("Scenario: /opt fails, /home succeeds, prompt text warns and shows success", async () =>
        {
            var m = new HybridMemoryManager(new SemanticMemory(), new ExactMemory());
            m.RecordError(1, "write_file", "Error: Permission denied /opt/config.json");
            m.RecordSuccess(2, "write_file", "OK: wrote /home/config.json");
            m.RecordError(3, "write_file", "Error: Permission denied /opt/data.json");
            var txt = m.ToPromptText(m.GetContext("permission"));
            Assert(txt.Contains("WHAT WORKED RECENTLY") && txt.Contains("/home/config.json"), "success section");
            Assert(txt.Contains("DO NOT REPEAT") && txt.Contains("/opt/data.json") && txt.Contains("/opt/config.json"), "error section");
            Assert(txt.Contains("LEARNED PATTERNS"), "patterns section");
            return txt.Replace("\n", " | ");
        });

        await T("Manager Save/Load round-trip through both files", async () =>
        {
            var d = Tmp(); var s = Path.Combine(d, "s.json"); var e = Path.Combine(d, "e.json");
            var m = new HybridMemoryManager(new SemanticMemory(), new ExactMemory());
            m.RecordError(1, "sh", "Error: syntax"); await m.SaveAsync(s, e);
            var m2 = new HybridMemoryManager(new SemanticMemory(), new ExactMemory()); await m2.LoadAsync(s, e);
            var c = m2.GetContext("syntax");
            Assert(c.RecentErrors.Count == 1 && c.Patterns.Count == 1, "not restored");
            return "restored";
        });

        await T("Retrieval: a realistic assistant thought retrieves the relevant pattern", async () =>
        {
            // CodingAgent passes the ENTIRE last assistant message as the search keyword.
            var m = new HybridMemoryManager(new SemanticMemory(), new ExactMemory());
            m.RecordError(1, "write_file", "Error: Permission denied /opt/config.json");
            var thought = "I'll try writing the settings to /opt/data.json next.";
            var n = m.GetContext(thought).Patterns.Count;
            Assert(n > 0, $"0 patterns returned for a realistic thought (Search does whole-string Contains on {thought.Length} chars)");
            return $"{n} pattern(s)";
        }, finding: true);

        await T("Retrieval: empty thought (tool-call turn) returns only relevant patterns", async () =>
        {
            // Assistant tool-call messages have content=null -> keyword "" -> Contains("") is always true.
            var m = new HybridMemoryManager(new SemanticMemory(), new ExactMemory());
            m.RecordSuccess(1, "read_file", "unrelated A"); m.RecordSuccess(2, "sh", "unrelated B");
            m.RecordSuccess(3, "list_dir", "unrelated C"); m.RecordSuccess(4, "sh", "unrelated D");
            var n = m.GetContext("").Patterns.Count;
            Assert(n == 0, $"{n} unrelated patterns injected for an empty query");
            return "0";
        }, finding: true);

        await T("Growth: repeated identical errors are de-duplicated", async () =>
        {
            var m = new HybridMemoryManager(new SemanticMemory(), new ExactMemory());
            for (int i = 0; i < 20; i++) m.RecordError(i, "write_file", "Error: Permission denied /opt/x");
            var n = m.GetContext("Permission denied /opt/x").Patterns.Count;
            // Search is limited to 3, so count via file
            var d = Tmp(); var sem = new SemanticMemory();
            var m2 = new HybridMemoryManager(sem, new ExactMemory());
            for (int i = 0; i < 20; i++) m2.RecordError(i, "write_file", "Error: Permission denied /opt/x");
            await m2.SaveAsync(Path.Combine(d, "s"), Path.Combine(d, "e"));
            var stored = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(d, "s")))!.AsArray().Count;
            Assert(stored == 1, $"{stored} stored entries for 1 distinct error");
            return "1";
        }, finding: true);

        await T("Growth: semantic file is bounded (1000 successes)", async () =>
        {
            var d = Tmp(); var m = new HybridMemoryManager(new SemanticMemory(), new ExactMemory());
            for (int i = 0; i < 1000; i++) m.RecordSuccess(i, "read_file", "content " + i);
            await m.SaveAsync(Path.Combine(d, "s"), Path.Combine(d, "e"));
            var stored = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(d, "s")))!.AsArray().Count;
            var kb = new FileInfo(Path.Combine(d, "s")).Length / 1024;
            Assert(stored <= 200, $"{stored} entries / {kb} KB stored, no cap");
            return $"{stored}";
        }, finding: true);
    }
}
