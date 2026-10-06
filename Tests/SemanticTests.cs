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
    // ═════════════════════════ Semantic search, de-duplication, cap ═════════════════════════
    static async Task SemanticLifecycle()
    {
        Group("Semantic / de-duplication");

        static int StoredCount(string file) => JsonNode.Parse(File.ReadAllText(file))!.AsArray().Count;

        await T("20 identical errors -> 1 entry with count 20, shown as (seen 20x)", async () =>
        {
            var d = Tmp(); var m = new HybridMemoryManager(new SemanticMemory(), new ExactMemory());
            for (int i = 0; i < 20; i++) m.RecordError(i, "write_file", "Error: Permission denied /opt/x");
            await m.SaveAsync(Path.Combine(d, "s"), Path.Combine(d, "e"));
            Assert(StoredCount(Path.Combine(d, "s")) == 1, "stored " + StoredCount(Path.Combine(d, "s")));
            var node = JsonNode.Parse(File.ReadAllText(Path.Combine(d, "s")))![0]!;
            Assert(node["count"]!.GetValue<int>() == 20, "count=" + node["count"]);
            var txt = m.ToPromptText(m.GetContext("permission /opt/x"));
            Assert(txt.Contains("(seen 20x)"), "prompt: " + txt);
            return "1 entry, count 20";
        });

        await T("digits are normalised (line 12 == line 40), different paths stay separate", async () =>
        {
            var d = Tmp(); var m = new HybridMemoryManager(new SemanticMemory(), new ExactMemory());
            m.RecordError(1, "sh", "Error: syntax error at line 12");
            m.RecordError(2, "sh", "Error: syntax error at line 40");
            m.RecordError(3, "write_file", "Error: Permission denied /opt/a.json");
            m.RecordError(4, "write_file", "Error: Permission denied /srv/b.json");
            await m.SaveAsync(Path.Combine(d, "s"), Path.Combine(d, "e"));
            Assert(StoredCount(Path.Combine(d, "s")) == 3, "stored " + StoredCount(Path.Combine(d, "s")) + " (expected 3)");
            return "3 entries";
        });

        await T("same text, different type is not merged", async () =>
        {
            var s = new SemanticMemory();
            s.AddPattern("error_pattern", "same text", new[] { "a" });
            s.AddPattern("solution", "same text", new[] { "a" });
            Assert(s.Count == 2, "count " + s.Count);
            return "2";
        });

        Group("Semantic / cap");

        await T("500 distinct patterns -> at most 200 stored", async () =>
        {
            var s = new SemanticMemory();
            for (int i = 0; i < 500; i++) s.AddPattern("error_pattern", "distinct lesson alpha" + new string((char)('a' + i % 26), 1) + "-" + i.ToString("x") + "-" + Guid.NewGuid(), new[] { "t" });
            Assert(s.Count <= SemanticMemory.MaxPatterns, "count " + s.Count);
            return s.Count.ToString();
        });

        await T("eviction keeps frequent lessons and the newest one", async () =>
        {
            var s = new SemanticMemory();
            for (int i = 0; i < 5; i++) s.AddPattern("error_pattern", "FREQUENT lesson zebra", new[] { "t" });
            for (int i = 0; i < 400; i++) s.AddPattern("error_pattern", "one-off " + Guid.NewGuid(), new[] { "t" });
            s.AddPattern("error_pattern", "NEWEST lesson giraffe", new[] { "t" });
            Assert(s.Count <= SemanticMemory.MaxPatterns, "cap " + s.Count);
            Assert(s.Search("zebra").Count == 1, "frequent lesson evicted");
            Assert(s.Search("giraffe").Count == 1, "newest lesson evicted");
            return "frequent + newest survive";
        });

        Group("Hybrid / solutions only after an error, never results");

        await T("success with no preceding error creates NO semantic pattern", async () =>
        {
            var d = Tmp(); var m = new HybridMemoryManager(new SemanticMemory(), new ExactMemory());
            for (int i = 0; i < 50; i++) m.RecordSuccess(i, "read_file", "SECRET-CONTENT " + i, "{\"path\":\"a.txt\"}");
            await m.SaveAsync(Path.Combine(d, "s"), Path.Combine(d, "e"));
            Assert(StoredCount(Path.Combine(d, "s")) == 0, "stored " + StoredCount(Path.Combine(d, "s")));
            Assert(m.GetContext("a.txt").RecentSuccesses.Count == 2, "exact layer should still keep successes");
            return "0 semantic entries, successes still in Exact";
        });

        await T("error then success of the same tool -> one 'solution' with the ARGUMENTS, not the result", async () =>
        {
            var d = Tmp(); var m = new HybridMemoryManager(new SemanticMemory(), new ExactMemory());
            m.RecordError(1, "write_file", "Error: Permission denied /opt/config.json");
            m.RecordSuccess(2, "write_file", "OK wrote SECRET-CONTENT", "{\"path\":\"/home/config.json\"}");
            await m.SaveAsync(Path.Combine(d, "s"), Path.Combine(d, "e"));
            var raw = File.ReadAllText(Path.Combine(d, "s"));
            var arr = JsonNode.Parse(raw)!.AsArray();
            var sol = arr.Single(x => x!["type"]!.GetValue<string>() == "solution")!;
            var summary = sol["summary"]!.GetValue<string>();
            Assert(summary.Contains("/home/config.json") && summary.Contains("permission"), "summary: " + summary);
            Assert(!raw.Contains("SECRET-CONTENT"), "result text leaked into semantic file");
            Assert(sol["tags"]!.AsArray().Any(t => t!.GetValue<string>() == "permission"), "error class tag missing");
            return summary;
        });

        await T("success of a DIFFERENT tool, or outside the 5-step window, creates nothing", async () =>
        {
            var m = new HybridMemoryManager(new SemanticMemory(), new ExactMemory());
            m.RecordError(1, "write_file", "Error: Permission denied");
            m.RecordSuccess(2, "read_file", "ok", "{}");          // other tool
            m.RecordSuccess(20, "write_file", "ok", "{}");        // too late
            var d = Tmp(); await m.SaveAsync(Path.Combine(d, "s"), Path.Combine(d, "e"));
            var types = JsonNode.Parse(File.ReadAllText(Path.Combine(d, "s")))!.AsArray().Select(x => x!["type"]!.GetValue<string>()).ToList();
            Assert(types.Count == 1 && types[0] == "error_pattern", "types: " + string.Join(",", types));
            return "only the error pattern";
        });

        await T("the solution is retrieved by a thought about the same file", async () =>
        {
            var m = new HybridMemoryManager(new SemanticMemory(), new ExactMemory());
            m.RecordError(1, "write_file", "Error: Permission denied /opt/config.json");
            m.RecordSuccess(2, "write_file", "ok", "{\"path\":\"/home/config.json\"}");
            var pats = m.GetContext("write_file {\"path\":\"/home/config.json\"}").Patterns;
            Assert(pats.Any(p => p.Type == "solution"), "solution not retrieved");
            return "retrieved";
        });

        Group("Semantic / persistence and migration");

        await T("count and lastSeen survive Save/Load", async () =>
        {
            var d = Tmp(); var p = Path.Combine(d, "s");
            var s = new SemanticMemory();
            for (int i = 0; i < 4; i++) s.AddPattern("error_pattern", "persist me", new[] { "t" });
            await s.SaveAsync(p);
            var s2 = new SemanticMemory(); await s2.LoadAsync(p);
            var e = s2.Search("persist").Single();
            Assert(e.Count == 4 && e.LastSeen is not null, $"count={e.Count} lastSeen={e.LastSeen}");
            return "count 4";
        });

        await T("old-format file (no count, many duplicates, 300 entries) is compacted on load", async () =>
        {
            var d = Tmp(); var p = Path.Combine(d, "old.json");
            var arr = new JsonArray();
            for (int i = 0; i < 300; i++)
                arr.Add(new JsonObject
                {
                    ["id"] = JsonValue.Create(Guid.NewGuid().ToString()), ["type"] = JsonValue.Create("error_pattern"),
                    ["summary"] = JsonValue.Create(i < 150 ? "[write_file] Error: Permission denied /opt/x" : "[sh] Error: other " + Guid.NewGuid()),
                    ["tags"] = new JsonArray { JsonValue.Create("error") }, ["createdAt"] = JsonValue.Create(DateTime.UtcNow.AddMinutes(-i).ToString("O")), ["context"] = null
                });
            File.WriteAllText(p, arr.ToJsonString());
            var s = new SemanticMemory(); await s.LoadAsync(p);
            Assert(s.Count <= SemanticMemory.MaxPatterns, "count " + s.Count);
            var dup = s.Search("permission").Single();
            Assert(dup.Count == 150, "merged count " + dup.Count + " (expected 150)");
            return $"300 -> {s.Count}, duplicates merged (x150)";
        });

        await T("Search order: best score first, then most recently seen", async () =>
        {
            var s = new SemanticMemory();
            s.AddPattern("error_pattern", "alpha beta", new[] { "t" });
            await Task.Delay(15);
            s.AddPattern("error_pattern", "alpha only", new[] { "t" });
            await Task.Delay(15);
            s.AddPattern("error_pattern", "alpha beta", new[] { "t" });   // re-seen -> newest
            var r = s.Search("alpha beta", limit: 3);
            Assert(r[0].Summary == "alpha beta", "best score first: " + r[0].Summary);
            Assert(r.Count == 2, "count " + r.Count);
            return "ok";
        });
    }
}
