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
    record Result(string Group, string Name, bool Pass, string Detail, bool Finding);
    static readonly List<Result> _res = new();
    static readonly List<string> _tmpDirs = new();
    static string _group = "";

    static void Group(string g) => _group = g;
    static string Content(JsonNode? m) => m?["content"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    internal record Case(string Group, string Name, Func<Task<string?>> Body, bool Finding)
    {
        public string Id => Group + " | " + Name;
    }
    // When non-null, T() only registers the test instead of running it (used for test discovery).
    static List<Case>? _collect;
    static List<Case>? _cases;

    static async Task T(string name, Func<Task<string?>> body, bool finding = false)
    {
        if (_collect != null) { _collect.Add(new Case(_group, name, body, finding)); return; }
        try
        {
            var detail = await body();
            _res.Add(new Result(_group, name, true, detail ?? "", finding));
        }
        catch (Exception ex)
        {
            _res.Add(new Result(_group, name, false, ex.Message, finding));
        }
    }
    static void Assert(bool c, string msg) { if (!c) throw new Exception(msg); }

    static string Tmp()
    {
        var d = Path.Combine(Path.GetTempPath(), "memtest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        lock (_tmpDirs) _tmpDirs.Add(d);
        return d;
    }

    static JsonArray Conversation(int exchanges)
    {
        var m = new JsonArray { JsonHelpers.Message("system", "sys") };
        for (int i = 0; i < exchanges; i++)
        {
            m.Add(JsonHelpers.Message("user", $"task step {i}"));
            m.Add(JsonHelpers.Message("assistant", $"working on {i}"));
        }
        return m;
    }

    const string GoodJson = """
        {"decisions":["Use /home for config"],"constraints":["/opt is read-only"],"pending":["Restart service"],"failed_approaches":["Write to /opt/config.json"]}
        """;

    static async Task RunGroups()
    {
        await MemoryLayers();
        await SemanticLifecycle();
        await Persistence();
        await Concurrency();
        await Distillation();
        await NoDistill();
        await AgentEndToEnd();
    }

    /// <summary>Console runner (dotnet run / F5): runs every test and prints a report.</summary>
    public static async Task<int> RunAll()
    {
        _collect = null;
        await RunGroups();
        Report();
        return _res.Any(r => !r.Pass && !r.Finding) ? 1 : 0;
    }

    /// <summary>Test Explorer: lists every test without running any of them.</summary>
    internal static List<Case> Discover()
    {
        lock (_tmpDirs)
        {
            if (_cases != null) return _cases;
            _collect = new List<Case>();
            try { RunGroups().GetAwaiter().GetResult(); _cases = _collect; }
            finally { _collect = null; }
            var dup = _cases.GroupBy(c => c.Id).FirstOrDefault(g => g.Count() > 1);
            if (dup != null) throw new InvalidOperationException("Duplicate test id: " + dup.Key);
            return _cases;
        }
    }

    /// <summary>Runs one discovered test; throws if it fails.</summary>
    internal static async Task RunCase(string id)
    {
        var c = Discover().First(x => x.Id == id);
        try { await c.Body(); }
        catch (Exception ex) when (c.Finding)
        {
            // Known limitations are reported as "inconclusive" rather than failures.
            throw new Microsoft.VisualStudio.TestTools.UnitTesting.AssertInconclusiveException("Known finding: " + ex.Message);
        }
    }

    internal static void CleanupTemp()
    {
        lock (_tmpDirs)
        {
            foreach (var d in _tmpDirs)
                try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
            _tmpDirs.Clear();
        }
    }
}
