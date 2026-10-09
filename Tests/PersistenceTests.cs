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
    // ═════════════════════════ Atomic saves and tolerant loading ═════════════════════════
    static async Task Persistence()
    {
        Group("Persistence / atomic saves");

        await T("no .tmp file is left after a save; content is valid JSON", async () =>
        {
            var d = Tmp(); var m = new HybridMemoryManager(new SemanticMemory(), new ExactMemory());
            m.RecordError(1, "sh", "Error: x");
            await m.SaveAsync(Path.Combine(d, "s.json"), Path.Combine(d, "e.json"));
            Assert(!Directory.GetFiles(d, "*.tmp").Any(), "tmp left: " + string.Join(",", Directory.GetFiles(d)));
            Assert(JsonNode.Parse(File.ReadAllText(Path.Combine(d, "s.json"))) is JsonArray, "not json");
            return "ok";
        });

        await T("20 concurrent saves + a reader: the reader NEVER sees a partial file", async () =>
        {
            var d = Tmp(); var p = Path.Combine(d, "s.json");
            var sem = new SemanticMemory();
            for (int i = 0; i < 150; i++) sem.AddPattern("error_pattern", "lesson number " + i + " " + new string('x', 200), new[] { "t" });
            await sem.SaveAsync(p);                       // a valid file exists from the start
            using var stop = new CancellationTokenSource();
            int reads = 0, bad = 0;
            var reader = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try { JsonNode.Parse(await File.ReadAllTextAsync(p)); Interlocked.Increment(ref reads); }
                    catch (JsonException) { Interlocked.Increment(ref bad); }
                    catch (IOException) { /* momentary sharing violation is not corruption */ }
                }
            });
            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => sem.SaveAsync(p)));
            await Task.Delay(50); stop.Cancel(); await reader;
            Assert(bad == 0, $"{bad} partial reads out of {reads + bad}");
            Assert(!Directory.GetFiles(d, "*.tmp").Any(), "tmp left over");
            return $"{reads} clean reads, 0 partial";
        });

        await T("save to an impossible path does not throw (agent keeps running)", async () =>
        {
            var d = Tmp();
            var m = new HybridMemoryManager(new SemanticMemory(), new ExactMemory());
            m.RecordError(1, "sh", "Error: x");
            await m.SaveAsync(Path.Combine(d, "no", "such", "dir", "s.json"), Path.Combine(d, "no", "such", "dir", "e.json"));
            Directory.CreateDirectory(Path.Combine(d, "adir"));
            await m.SaveAsync(Path.Combine(d, "adir"), Path.Combine(d, "adir"));   // target is a directory
            return "no exception";
        });

        Group("Persistence / tolerant loading");

        foreach (var (label, content) in new[]
        {
            ("truncated JSON", "[{\"step\":1,\"type\":\"err"),
            ("empty file", ""),
            ("JSON null", "null"),
            ("object instead of array", "{\"a\":1}"),
            ("binary garbage", "\u0000\u0001\u0002 not json"),
        })
        {
            await T($"corrupt file ({label}): load does not throw, file quarantined as .bad, next save works", async () =>
            {
                var d = Tmp(); var sem = Path.Combine(d, "s.json"); var ex = Path.Combine(d, "e.json");
                File.WriteAllText(sem, content); File.WriteAllText(ex, content);
                var m = new HybridMemoryManager(new SemanticMemory(), new ExactMemory());
                await m.LoadAsync(sem, ex);
                Assert(File.Exists(sem + ".bad") && File.Exists(ex + ".bad"), "not quarantined");
                Assert(File.ReadAllText(sem + ".bad") == content, "quarantined content changed");
                Assert(!File.Exists(sem) && !File.Exists(ex), "corrupt file still in place");
                m.RecordError(1, "sh", "Error: fresh");
                await m.SaveAsync(sem, ex);
                Assert(JsonNode.Parse(File.ReadAllText(sem)) is JsonArray a && a.Count == 1, "fresh save");
                return "quarantined, fresh file written";
            });
        }

        await T("one malformed entry is skipped, the good ones survive", async () =>
        {
            var d = Tmp(); var sem = Path.Combine(d, "s.json"); var ex = Path.Combine(d, "e.json");
            File.WriteAllText(ex, "[{\"step\":1,\"type\":\"error\",\"content\":\"good\",\"toolName\":\"sh\"},{\"step\":\"abc\",\"type\":\"error\"},{\"step\":3,\"type\":\"success\",\"content\":\"also good\"}]");
            File.WriteAllText(sem, "[{\"id\":\"1\",\"type\":\"error_pattern\",\"summary\":\"good lesson\",\"tags\":[\"a\"],\"createdAt\":\"2026-01-01T00:00:00Z\"},{\"id\":\"2\",\"type\":\"error_pattern\",\"summary\":\"bad date\",\"tags\":[\"a\"],\"createdAt\":\"not a date\"},{\"summary\":5}]");
            var m = new HybridMemoryManager(new SemanticMemory(), new ExactMemory());
            await m.LoadAsync(sem, ex);
            var ctx = m.GetContext("lesson");
            Assert(m.GetContext("").RecentSteps.Count == 2, "exact entries: " + m.GetContext("").RecentSteps.Count);
            Assert(ctx.Patterns.Any(p => p.Summary == "good lesson"), "good semantic entry lost");
            Assert(!File.Exists(sem + ".bad") && !File.Exists(ex + ".bad"), "valid files must not be quarantined");
            return "good entries kept";
        });

        await T("Exact timestamps survive Save/Load", async () =>
        {
            var d = Tmp(); var p = Path.Combine(d, "e.json");
            var e = new ExactMemory(); e.Add(1, "success", "x", "sh");
            await e.SaveAsync(p);
            var e2 = new ExactMemory(); await e2.LoadAsync(p);
            Assert(e2.GetRecent(1)[0].Timestamp is not null, "timestamp lost");
            return "ok";
        });

        await T("missing files are fine (first run)", async () =>
        {
            var d = Tmp();
            var m = new HybridMemoryManager(new SemanticMemory(), new ExactMemory());
            await m.LoadAsync(Path.Combine(d, "nope1"), Path.Combine(d, "nope2"));
            Assert(!Directory.GetFiles(d).Any(), "files created by a load");
            return "ok";
        });

        Group("Persistence / MemoryStore (conversation file)");

        await T("corrupt conversation file: load returns [], file kept as .bad (not overwritten)", async () =>
        {
            var d = Tmp(); var p = Path.Combine(d, "agent_memory.json");
            File.WriteAllText(p, "[{\"role\":\"user\",\"content\":\"precious");
            var msgs = await MemoryStore.LoadAsync(p);
            Assert(msgs.Count == 0, "count " + msgs.Count);
            Assert(File.Exists(p + ".bad") && File.ReadAllText(p + ".bad").Contains("precious"), "no .bad copy");
            msgs.Add(JsonHelpers.Message("user", "new"));
            await MemoryStore.SaveAsync(p, msgs);
            Assert(File.ReadAllText(p + ".bad").Contains("precious"), ".bad overwritten by save");
            Assert((await MemoryStore.LoadAsync(p)).Count == 1, "fresh save not loadable");
            Assert(!Directory.GetFiles(d, "*.tmp").Any(), "tmp left over");
            return "ok";
        });

        await T("MemoryStore keeps working: normal round trip and image stripping", async () =>
        {
            var d = Tmp(); var p = Path.Combine(d, "m.json");
            var msgs = new JsonArray { JsonHelpers.Message("system", "s"), JsonHelpers.MultimodalMessage("user", "look", "AAAABBBB", "image/png") };
            await MemoryStore.SaveAsync(p, msgs);
            var raw = File.ReadAllText(p);
            Assert(!raw.Contains("AAAABBBB"), "image data persisted");
            var back = await MemoryStore.LoadAsync(p);
            Assert(back.Count == 2 && back[1]!["content"]!.GetValue<string>() == "look", "round trip");
            return "ok";
        });

        Group("Persistence / locked target file (Windows antivirus, indexer, sync clients)");

        await T("a transient 'access denied' on the final move is retried and the save succeeds", async () =>
        {
            var d = Tmp(); var p = Path.Combine(d, "conversation.json");
            var oldMove = AtomicFile.MoveOverwrite; var oldDelay = AtomicFile.MoveBaseDelayMs;
            var calls = 0;
            try
            {
                AtomicFile.MoveBaseDelayMs = 1;
                AtomicFile.MoveOverwrite = (from, to) =>
                {
                    if (++calls <= 3) throw new UnauthorizedAccessException("Access to the path is denied.");
                    File.Move(from, to, overwrite: true);
                };
                await MemoryStore.SaveAsync(p, new JsonArray { JsonHelpers.Message("user", "hello") });
            }
            finally { AtomicFile.MoveOverwrite = oldMove; AtomicFile.MoveBaseDelayMs = oldDelay; }
            Assert(calls == 4, "move attempts: " + calls);
            Assert((await MemoryStore.LoadAsync(p)).Count == 1, "file not saved after retries");
            Assert(!Directory.GetFiles(d, "*.tmp").Any(), "tmp left over");
            return $"saved after {calls} attempts";
        });

        await T("a target that stays locked: SaveAsync does not throw, the old file is intact, no .tmp left", async () =>
        {
            var d = Tmp(); var p = Path.Combine(d, "conversation.json");
            await MemoryStore.SaveAsync(p, new JsonArray { JsonHelpers.Message("user", "first") });
            var oldMove = AtomicFile.MoveOverwrite; var oldDelay = AtomicFile.MoveBaseDelayMs;
            var calls = 0;
            try
            {
                AtomicFile.MoveBaseDelayMs = 1;
                AtomicFile.MoveOverwrite = (from, to) => { calls++; throw new IOException("The process cannot access the file."); };
                await MemoryStore.SaveAsync(p, new JsonArray { JsonHelpers.Message("user", "second") });   // must not throw
            }
            finally { AtomicFile.MoveOverwrite = oldMove; AtomicFile.MoveBaseDelayMs = oldDelay; }
            Assert(calls == AtomicFile.MoveAttempts, "attempts: " + calls);
            var back = await MemoryStore.LoadAsync(p);
            Assert(back.Count == 1 && back[0]!["content"]!.GetValue<string>() == "first", "previous file damaged");
            Assert(!Directory.GetFiles(d, "*.tmp").Any(), "tmp left over");
            return $"gave up after {calls} attempts, nothing thrown";
        });

        await T("AtomicFile.WriteAllTextAsync still throws when the move keeps failing (callers that need to know)", async () =>
        {
            var d = Tmp(); var p = Path.Combine(d, "x.json");
            var oldMove = AtomicFile.MoveOverwrite; var oldDelay = AtomicFile.MoveBaseDelayMs;
            var threw = false;
            try
            {
                AtomicFile.MoveBaseDelayMs = 1;
                AtomicFile.MoveOverwrite = (from, to) => throw new UnauthorizedAccessException("denied");
                try { await AtomicFile.WriteAllTextAsync(p, "[]"); } catch (UnauthorizedAccessException) { threw = true; }
            }
            finally { AtomicFile.MoveOverwrite = oldMove; AtomicFile.MoveBaseDelayMs = oldDelay; }
            Assert(threw, "no exception");
            return "thrown after retries";
        });
    }
}