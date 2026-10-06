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
    // ═════════════════════════ report ═════════════════════════
    static void Report()
    {
        var sb = new StringBuilder();
        int pass = _res.Count(r => r.Pass), fail = _res.Count(r => !r.Pass && !r.Finding), find = _res.Count(r => !r.Pass && r.Finding);
        sb.AppendLine($"TOTAL {_res.Count} | PASS {pass} | FAIL {fail} | FINDINGS {find}");
        foreach (var g in _res.GroupBy(r => r.Group))
        {
            sb.AppendLine($"\n## {g.Key}");
            foreach (var r in g)
                sb.AppendLine($"{(r.Pass ? "PASS   " : r.Finding ? "FINDING" : "FAIL   ")} | {r.Name} | {r.Detail.Replace("\n", " ")}");
        }
        Console.WriteLine(sb);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "results.txt"), sb.ToString());

        foreach (var d in _tmpDirs)
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
    }
}
