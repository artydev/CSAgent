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
    static async Task Concurrency()
    {
        Group("Concurrency / shared memory manager");

        await T("32 parallel tasks record/search/save on one HybridMemoryManager without exception", async () =>
        {
            var d = Tmp();
            var m = new HybridMemoryManager(new SemanticMemory(), new ExactMemory());
            var sp = Path.Combine(d, "s.json"); var ep = Path.Combine(d, "e.json");
            var tasks = Enumerable.Range(0, 32).Select(t => Task.Run(async () =>
            {
                for (int i = 0; i < 300; i++)
                {
                    int step = t * 1000 + i;
                    m.RecordError(step, "sh", $"Error: permission denied on /opt/dir{t}_{i % 40}");
                    m.RecordSuccess(step + 1, "sh", "ok", $"cd /home/{t}");
                    m.ToPromptText(m.GetContext("permission denied opt"));
                    if (i % 50 == 0) { await m.SaveAsync(sp, ep); await m.LoadAsync(sp, ep); }
                }
            })).ToArray();
            await Task.WhenAll(tasks);
            await m.SaveAsync(sp, ep);
            Assert(JsonNode.Parse(File.ReadAllText(sp)) is JsonArray a && a.Count <= SemanticMemory.MaxPatterns, "semantic file invalid or over cap");
            Assert(JsonNode.Parse(File.ReadAllText(ep)) is JsonArray, "exact file invalid");
            return "no exception, files valid, cap respected";
        });
    }
}
