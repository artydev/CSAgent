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
    // When a model's DAILY quota is used up, the agent carries on with another model.
    static async Task QuotaFallbackGroup()
    {
        Group("Daily quota: automatic switch to another model");
        CsAgent.Core.Llm.Loc.Set("en");
        var fast = new RetryPolicy(MaxAttempts: 3, BaseDelayMs: 5, BackoffFactor: 1.0, MaxDelayMs: 20);
        var perDay = new JsonObject { ["detail"] = "1280000 input tokens per day exceeded (remaining: 0)." };

        // Runs a body with CSAGENT_FALLBACK_MODEL set (null = unset) and the "used up" memory emptied.
        async Task<T> With<T>(string? env, Func<Task<T>> body)
        {
            var old = Environment.GetEnvironmentVariable(QuotaFallback.EnvName);
            Environment.SetEnvironmentVariable(QuotaFallback.EnvName, env);
            QuotaFallback.Reset();
            LlmConfig.Reset();
            var prev = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(Tmp());
            try { return await body(); }
            finally
            {
                Directory.SetCurrentDirectory(prev);
                Environment.SetEnvironmentVariable(QuotaFallback.EnvName, old);
                QuotaFallback.Reset();
            }
        }

        string Model(JsonNode req) => req["model"]!.GetValue<string>();

        await T("Target: default endpoint -> the code model; other endpoint -> none; same model -> none", () => With<string?>(null, async () =>
        {
            await Task.CompletedTask;
            const string albert = "https://albert.api.etalab.gouv.fr/v1";
            Assert(QuotaFallback.Target("gpt-oss-120b", albert) == "deepseek-v4-flash", "albert");
            Assert(QuotaFallback.Target("gpt-oss-120b", albert + "/") == "deepseek-v4-flash", "trailing slash");
            Assert(QuotaFallback.Target("deepseek-v4-flash", albert) == null, "already on it");
            Assert(QuotaFallback.Target("gpt-oss-120b", "http://localhost:11434/v1") == null, "ollama");
            return "ok";
        }));

        await T("Target: CSAGENT_FALLBACK_MODEL names the model on any endpoint; off / none turn it off", async () =>
        {
            Assert(await With("small", async () => { await Task.CompletedTask; return QuotaFallback.Target("big", "http://localhost:1/v1"); }) == "small", "named");
            Assert(await With("off", async () => { await Task.CompletedTask; return QuotaFallback.Target("big", "https://albert.api.etalab.gouv.fr/v1"); }) == null, "off");
            Assert(await With("None", async () => { await Task.CompletedTask; return QuotaFallback.Target("big", "https://albert.api.etalab.gouv.fr/v1"); }) == null, "none");
            return "ok";
        });

        await T("a model used up is remembered for an hour, then tried again", () => With<string?>("small", async () =>
        {
            await Task.CompletedTask;
            var t0 = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
            QuotaFallback.Now = () => t0;
            QuotaFallback.MarkExhausted("big");
            Assert(QuotaFallback.IsExhausted("big") && QuotaFallback.IsExhausted("BIG"), "just marked");
            QuotaFallback.Now = () => t0.AddMinutes(59);
            Assert(QuotaFallback.IsExhausted("big"), "after 59 min");
            QuotaFallback.Now = () => t0.AddMinutes(61);
            Assert(!QuotaFallback.IsExhausted("big"), "after 61 min");
            QuotaFallback.MarkExhausted("small");
            Assert(QuotaFallback.Target("big", "http://x/v1") == null, "the target is used up too");
            return "ok";
        }));

        await T("agent: daily 429 on the first model -> the same request is sent to the other one and the run completes", () => With("small", async () =>
        {
            using var mock = new MockLlm { Handler = req => Model(req) == "big" ? (429, perDay, 0) : (200, MockLlm.Text("done"), 0) };
            var obs = new UsageObserver();
            var msgs = new JsonArray { CodingAgent.SystemMessage(false), JsonHelpers.Message("user", "go") };
            using var agent = new CodingAgent("k", mock.BaseUrl, "big", new AgentOptions(Confirm: false, Retry: fast), obs, null,
                new HybridMemoryManager(new SemanticMemory(), new ExactMemory()));
            await agent.RunAsync(msgs, "agent_memory.json");
            var models = mock.Requests.Where(r => !MockLlm.IsDistill(r)).Select(Model).ToList();
            Assert(models.SequenceEqual(new[] { "big", "small" }), string.Join(",", models));
            Assert(obs.Errors.Count == 0, "errors: " + string.Join("|", obs.Errors));
            Assert(obs.Warnings.Any(w => w.Contains("'big'") && w.Contains("'small'") && w.Contains("switching")), "no notice: " + string.Join("|", obs.Warnings));
            Assert(QuotaFallback.IsExhausted("big"), "not remembered");
            return string.Join(" -> ", models);
        }));

        await T("agent: the next run starts on the other model at once (no new refused request)", () => With("small", async () =>
        {
            using var mock = new MockLlm { Handler = req => Model(req) == "big" ? (429, perDay, 0) : (200, MockLlm.Text("done"), 0) };
            for (int i = 0; i < 2; i++)
            {
                var obs = new UsageObserver();
                var msgs = new JsonArray { CodingAgent.SystemMessage(false), JsonHelpers.Message("user", "go") };
                using var agent = new CodingAgent("k", mock.BaseUrl, "big", new AgentOptions(Confirm: false, Retry: fast), obs, null,
                    new HybridMemoryManager(new SemanticMemory(), new ExactMemory()));
                await agent.RunAsync(msgs, "agent_memory.json");
                if (i == 1) Assert(obs.Warnings.Any(w => w.Contains("was used up earlier")), "no notice: " + string.Join("|", obs.Warnings));
            }
            var refused = mock.Requests.Count(r => Model(r) == "big");
            Assert(refused == 1, $"{refused} requests to the exhausted model");
            return "1 refused request over 2 runs";
        }));

        await T("agent: if the other model is used up too, it stops with the daily-quota message", () => With("small", async () =>
        {
            using var mock = new MockLlm { Handler = _ => (429, perDay, 0) };
            var obs = new UsageObserver();
            var msgs = new JsonArray { CodingAgent.SystemMessage(false), JsonHelpers.Message("user", "go") };
            using var agent = new CodingAgent("k", mock.BaseUrl, "big", new AgentOptions(Confirm: false, Retry: fast), obs, null,
                new HybridMemoryManager(new SemanticMemory(), new ExactMemory()));
            await agent.RunAsync(msgs, "agent_memory.json");
            var n = mock.Requests.Count(r => !MockLlm.IsDistill(r));
            Assert(n == 2, $"{n} requests");
            Assert(!mock.Requests.Any(MockLlm.IsDistill), "summary call after a 429");
            Assert(obs.Errors.Any(e => e.Contains("429")) && obs.Warnings.Any(w => w.Contains("daily token quota")), "no final message");
            return "2 requests, then stopped";
        }));

        await T("agent: switch turned off (CSAGENT_FALLBACK_MODEL=off) -> stops at the first 429", () => With("off", async () =>
        {
            using var mock = new MockLlm { Handler = _ => (429, perDay, 0) };
            var obs = new UsageObserver();
            var msgs = new JsonArray { CodingAgent.SystemMessage(false), JsonHelpers.Message("user", "go") };
            using var agent = new CodingAgent("k", mock.BaseUrl, "big", new AgentOptions(Confirm: false, Retry: fast), obs, null,
                new HybridMemoryManager(new SemanticMemory(), new ExactMemory()));
            await agent.RunAsync(msgs, "agent_memory.json");
            Assert(mock.Requests.Count == 1, $"{mock.Requests.Count} requests");
            Assert(obs.Warnings.Any(w => w.Contains("daily token quota")), "no message");
            return "1 request";
        }));

        await T("agent: no switch when the conversation holds an image (the other model may not read it)", () => With("small", async () =>
        {
            using var mock = new MockLlm { Handler = _ => (429, perDay, 0) };
            var obs = new UsageObserver();
            var msgs = new JsonArray { CodingAgent.SystemMessage(false), JsonHelpers.MultimodalMessage("user", "look", "AAAA", "image/png") };
            using var agent = new CodingAgent("k", mock.BaseUrl, "big", new AgentOptions(Confirm: false, Retry: fast), obs, null,
                new HybridMemoryManager(new SemanticMemory(), new ExactMemory()));
            await agent.RunAsync(msgs, "agent_memory.json");
            Assert(mock.Requests.Count == 1 && !obs.Warnings.Any(w => w.Contains("switching")), $"{mock.Requests.Count} requests");
            return "stopped, no switch";
        }));

        await T("agent: a per-minute 429 never triggers the switch", () => With("small", async () =>
        {
            int n = 0;
            using var mock = new MockLlm { Handler = _ => ++n < 3 ? (429, new JsonObject { ["detail"] = "128000 input tokens per minute exceeded" }, 0) : (200, MockLlm.Text("done"), 0) };
            var obs = new UsageObserver();
            var msgs = new JsonArray { CodingAgent.SystemMessage(false), JsonHelpers.Message("user", "go") };
            using var agent = new CodingAgent("k", mock.BaseUrl, "big", new AgentOptions(Confirm: false, Retry: fast), obs, null,
                new HybridMemoryManager(new SemanticMemory(), new ExactMemory()));
            await agent.RunAsync(msgs, "agent_memory.json");
            Assert(mock.Requests.Where(r => !MockLlm.IsDistill(r)).All(r => Model(r) == "big"), "switched on a per-minute limit");
            Assert(!QuotaFallback.IsExhausted("big"), "marked");
            return "retried on the same model";
        }));

        await T("French: the switch notices are translated", async () =>
        {
            await Task.CompletedTask;
            CsAgent.Core.Llm.Loc.Set("fr");
            try
            {
                Assert(CsAgent.Core.Llm.Loc.F($"The daily quota of '{"a"}' is used up: switching to '{"b"}' and carrying on.").Contains("bascule"), "switch");
                Assert(CsAgent.Core.Llm.Loc.F($"The daily quota of '{"a"}' was used up earlier: using '{"b"}'.").Contains("plus tôt"), "earlier");
            }
            finally { CsAgent.Core.Llm.Loc.Set("en"); }
            return "ok";
        });
    }
}
