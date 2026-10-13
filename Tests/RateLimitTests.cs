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
    sealed class UsageObserver : IAgentObserver
    {
        public List<string> Errors = new(), Warnings = new();
        public List<(long P, long C, long SP, long SC)> Usage = new();
        public Task OnStep(int n, int max) => Task.CompletedTask;
        public Task OnThought(string t) => Task.CompletedTask;
        public Task OnToolCall(string n, string a) => Task.CompletedTask;
        public Task OnToolResult(string r, bool e) => Task.CompletedTask;
        public Task OnDone(string m) => Task.CompletedTask;
        public Task OnError(string m) { Errors.Add(m); return Task.CompletedTask; }
        public Task OnWarning(string m) { Warnings.Add(m); return Task.CompletedTask; }
        public Task OnDanger(string m) => Task.CompletedTask;
        public Task OnUsage(long p, long c, long sp, long sc) { Usage.Add((p, c, sp, sc)); return Task.CompletedTask; }
        public Task<bool> OnConfirm(string t) => Task.FromResult(true);
    }

    static JsonNode WithUsage(JsonNode answer, int prompt, int completion)
    {
        answer["usage"] = new JsonObject { ["prompt_tokens"] = prompt, ["completion_tokens"] = completion, ["total_tokens"] = prompt + completion };
        return answer;
    }

    // HTTP 429: a daily limit is not retried, a per-minute limit is; tokens used are reported.
    static async Task RateLimitGroup()
    {
        Group("API limits (429) and token use");
        CsAgent.Core.Llm.Loc.Set("en");
        var fast = new RetryPolicy(MaxAttempts: 3, BaseDelayMs: 5, BackoffFactor: 1.0, MaxDelayMs: 20);
        var perMinute = new JsonObject { ["detail"] = "128000 input tokens per minute exceeded (remaining: 0)." };
        var perDay = new JsonObject { ["detail"] = "1280000 input tokens per day exceeded (remaining: 0)." };

        await T("Classify: day, minute and other limits (Albert and OpenAI wordings)", async () =>
        {
            await Task.CompletedTask;
            Assert(RateLimit.Classify(perDay.ToJsonString()) == RateLimitKind.Day, "albert day");
            Assert(RateLimit.Classify(perMinute.ToJsonString()) == RateLimitKind.Minute, "albert minute");
            Assert(RateLimit.Classify("Rate limit reached on tokens per min (TPM): Limit 30000").ToString() == "Minute", "openai minute");
            Assert(RateLimit.Classify("Rate limit reached on tokens per day (TPD): Limit 200000").ToString() == "Day", "openai day");
            Assert(RateLimit.Classify("{\"detail\":\"slow down\"}") == RateLimitKind.Other && RateLimit.Classify(null) == RateLimitKind.Other, "other");
            Assert(RateLimit.Classify("per minute and per day").ToString() == "Day", "day wins");
            return "ok";
        });

        await T("a daily 429 is NOT retried: one request, RateLimitException (Day)", async () =>
        {
            using var mock = new MockLlm { Handler = _ => (429, perDay, 0) };
            using var client = new LlmClient("k", mock.BaseUrl, "m", fast);
            try { await client.CompleteChatAsync(new JsonArray { JsonHelpers.Message("user", "hi") }); Assert(false, "no exception"); }
            catch (RateLimitException e)
            {
                Assert(e.Kind == RateLimitKind.Day && e.Message.StartsWith("API 429:"), $"{e.Kind} / {e.Message}");
            }
            Assert(mock.Requests.Count == 1, $"{mock.Requests.Count} requests");
            return "1 request";
        });

        await T("a per-minute 429 is retried and the call then succeeds", async () =>
        {
            int n = 0;
            using var mock = new MockLlm { Handler = _ => ++n < 3 ? (429, perMinute, 0) : (200, MockLlm.Text("hello"), 0) };
            using var client = new LlmClient("k", mock.BaseUrl, "m", fast);
            var r = await client.CompleteChatAsync(new JsonArray { JsonHelpers.Message("user", "hi") });
            Assert(r["choices"]![0]!["message"]!["content"]!.GetValue<string>() == "hello", "answer");
            Assert(mock.Requests.Count == 3, $"{mock.Requests.Count} requests");
            return "3 requests";
        });

        await T("a per-minute 429 that never clears ends after MaxAttempts with RateLimitException (Minute)", async () =>
        {
            using var mock = new MockLlm { Handler = _ => (429, perMinute, 0) };
            using var client = new LlmClient("k", mock.BaseUrl, "m", fast);
            try { await client.CompleteChatAsync(new JsonArray { JsonHelpers.Message("user", "hi") }); Assert(false, "no exception"); }
            catch (RateLimitException e) { Assert(e.Kind == RateLimitKind.Minute, e.Kind.ToString()); }
            Assert(mock.Requests.Count == 3, $"{mock.Requests.Count} requests");
            return "3 requests";
        });

        await T("another error (500) is a plain HttpRequestException, not retried", async () =>
        {
            using var mock = new MockLlm { Handler = _ => (500, new JsonObject { ["error"] = "boom" }, 0) };
            using var client = new LlmClient("k", mock.BaseUrl, "m", fast);
            try { await client.CompleteChatAsync(new JsonArray { JsonHelpers.Message("user", "hi") }); Assert(false, "no exception"); }
            catch (RateLimitException) { Assert(false, "should not be a RateLimitException"); }
            catch (HttpRequestException e) { Assert(e.Message.StartsWith("API 500:"), e.Message); }
            Assert(mock.Requests.Count == 1, $"{mock.Requests.Count} requests");
            return "1 request";
        });

        await T("the client adds up the tokens the API reports (and the process-wide meter)", async () =>
        {
            TokenMeter.Reset();
            using var mock = new MockLlm { Handler = _ => (200, WithUsage(MockLlm.Text("a"), 1200, 30), 0) };
            using var client = new LlmClient("k", mock.BaseUrl, "m", fast);
            await client.CompleteChatAsync(new JsonArray { JsonHelpers.Message("user", "hi") });
            await client.CompleteChatAsync(new JsonArray { JsonHelpers.Message("user", "hi") });
            Assert(client.PromptTokens == 2400 && client.CompletionTokens == 60, $"{client.PromptTokens}/{client.CompletionTokens}");
            Assert(TokenMeter.Prompt == 2400 && TokenMeter.Completion == 60, $"{TokenMeter.Prompt}/{TokenMeter.Completion}");
            return "2400 in / 60 out";
        });

        await T("a server that sends no usage counts as zero, without error", async () =>
        {
            using var mock = new MockLlm { Handler = _ => (200, MockLlm.Text("a"), 0) };
            using var client = new LlmClient("k", mock.BaseUrl, "m", fast);
            await client.CompleteChatAsync(new JsonArray { JsonHelpers.Message("user", "hi") });
            Assert(client.PromptTokens == 0, "prompt tokens");
            return "ok";
        });

        async Task<T> InTmp<T>(Func<Task<T>> body)
        {
            var prev = Directory.GetCurrentDirectory();
            var oldFallback = Environment.GetEnvironmentVariable(QuotaFallback.EnvName);
            Environment.SetEnvironmentVariable(QuotaFallback.EnvName, "off");   // these tests are about stopping, not switching
            QuotaFallback.Reset();
            Directory.SetCurrentDirectory(Tmp());
            try { return await body(); }
            finally
            {
                Directory.SetCurrentDirectory(prev);
                Environment.SetEnvironmentVariable(QuotaFallback.EnvName, oldFallback);
                QuotaFallback.Reset();
            }
        }

        await T("agent: a daily 429 stops at once, says why, and does not call the summary model", async () =>
        {
            return await InTmp(async () =>
            {
                int n = 0;
                using var mock = new MockLlm { Handler = _ => ++n == 1 ? (200, MockLlm.ToolCall("list_dir", "{\"path\":\".\"}"), 0) : (429, perDay, 0) };
                var obs = new UsageObserver();
                var msgs = new JsonArray { CodingAgent.SystemMessage(false), JsonHelpers.Message("user", "go") };
                using var agent = new CodingAgent("k", mock.BaseUrl, "m", new AgentOptions(Confirm: false, Retry: fast), obs, null,
                    new HybridMemoryManager(new SemanticMemory(), new ExactMemory()));
                await agent.RunAsync(msgs, "agent_memory.json");
                Assert(mock.Requests.Count == 2, $"{mock.Requests.Count} requests (a 3rd would be the summary call)");
                Assert(!mock.Requests.Any(MockLlm.IsDistill), "the summary model was called");
                Assert(obs.Errors.Any(e => e.Contains("429")), "no error shown: " + string.Join("|", obs.Errors));
                Assert(obs.Warnings.Any(w => w.Contains("daily token quota")), "no explanation: " + string.Join("|", obs.Warnings));
                Assert(obs.Warnings.Any(w => w.Contains("Session summary not updated")), "no summary notice");
                return "2 requests, no summary call, explained";
            });
        });

        await T("agent: tokens of the run are reported once, at the end, with the session total", async () =>
        {
            return await InTmp(async () =>
            {
                TokenMeter.Reset();
                int n = 0;
                using var mock = new MockLlm { Handler = req => MockLlm.IsDistill(req)
                    ? (200, WithUsage(MockLlm.Text("[]"), 100, 5), 0)
                    : ++n == 1 ? (200, WithUsage(MockLlm.ToolCall("list_dir", "{\"path\":\".\"}"), 1000, 20), 0)
                               : (200, WithUsage(MockLlm.Text("done"), 1100, 10), 0) };
                var obs = new UsageObserver();
                var msgs = new JsonArray { CodingAgent.SystemMessage(false), JsonHelpers.Message("user", "go") };
                using var agent = new CodingAgent("k", mock.BaseUrl, "m", new AgentOptions(Confirm: false, Retry: fast), obs, null,
                    new HybridMemoryManager(new SemanticMemory(), new ExactMemory()));
                await agent.RunAsync(msgs, "agent_memory.json");
                Assert(obs.Usage.Count == 1, $"{obs.Usage.Count} reports");
                var u = obs.Usage[0];
                Assert(u.P >= 2100 && u.C >= 30, $"run {u.P}/{u.C}");
                Assert(u.SP == TokenMeter.Prompt && u.SC == TokenMeter.Completion, "session total");
                return $"in {u.P} / out {u.C}";
            });
        });

        await T("French: the 429 explanations and the token line are translated", async () =>
        {
            await Task.CompletedTask;
            CsAgent.Core.Llm.Loc.Set("fr");
            try
            {
                Assert(CsAgent.Core.Llm.Loc.T("Session summary not updated (rate limit); the previous one is kept.").StartsWith("Résumé de session"), "summary");
                Assert(CsAgent.Core.Llm.Loc.T("The daily token quota is used up: retrying is useless until it resets. Wait for the reset, or use another model (--model) or endpoint (--endpoint).").StartsWith("Le quota"), "daily");
                Assert(CsAgent.Core.Llm.Loc.F($"tokens: this turn in {"1"} / out {"2"} · session in {"3"} / out {"4"}").StartsWith("jetons"), "tokens");
            }
            finally { CsAgent.Core.Llm.Loc.Set("en"); }
            return "ok";
        });
    }
}
