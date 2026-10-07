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
    // ═════════════════════════ Headless API mode: --api, --yes, --host, --api-key ═════════════════════════
    static async Task ApiMode()
    {
        Group("API mode / arguments");

        // CSAGENT_API_KEY may be set on the machine running the tests: neutralise it for the parsing tests.
        static T WithoutEnvKey<T>(Func<T> f)
        {
            var old = Environment.GetEnvironmentVariable("CSAGENT_API_KEY");
            Environment.SetEnvironmentVariable("CSAGENT_API_KEY", null);
            try { return f(); } finally { Environment.SetEnvironmentVariable("CSAGENT_API_KEY", old); }
        }

        await T("defaults: not API mode, confirmations on, host localhost, no key", async () =>
        {
            var a = WithoutEnvKey(() => ArgumentParser.Parse(Array.Empty<string>()));
            Assert(!a.IsApiMode && !a.AutoApprove && a.Host == "localhost" && a.ApiKey is null, $"{a.IsApiMode}/{a.AutoApprove}/{a.Host}/{a.ApiKey}");
            return "ok";
            await Task.CompletedTask;
        });

        await T("--api --yes --host --api-key are parsed, in any order", async () =>
        {
            var a = WithoutEnvKey(() => ArgumentParser.Parse(new[] { "--host", "0.0.0.0", "--yes", "--port", "7000", "--api-key", "k1", "--api" }));
            Assert(a.IsApiMode && a.AutoApprove && a.Host == "0.0.0.0" && a.ApiKey == "k1" && a.Port == 7000, "parsed wrong");
            return "ok";
            await Task.CompletedTask;
        });

        await T("-y and --auto-approve are aliases of --yes", async () =>
        {
            Assert(ArgumentParser.Parse(new[] { "-y" }).AutoApprove, "-y");
            Assert(ArgumentParser.Parse(new[] { "--auto-approve" }).AutoApprove, "--auto-approve");
            return "ok";
            await Task.CompletedTask;
        });

        await T("--host and --api-key values are not taken as the memory file; a positional file still is", async () =>
        {
            var a = WithoutEnvKey(() => ArgumentParser.Parse(new[] { "--api", "--host", "10.0.0.5", "--api-key", "abc", "mem.json" }));
            Assert(a.MemoryFile == "mem.json", "memory file: " + a.MemoryFile);
            var b = WithoutEnvKey(() => ArgumentParser.Parse(new[] { "--api", "--host", "10.0.0.5", "--api-key", "abc" }));
            Assert(b.MemoryFile == "agent_memory", "memory file: " + b.MemoryFile);
            return "ok";
            await Task.CompletedTask;
        });

        await T("the key comes from CSAGENT_API_KEY when --api-key is absent; the argument wins over it", async () =>
        {
            var old = Environment.GetEnvironmentVariable("CSAGENT_API_KEY");
            try
            {
                Environment.SetEnvironmentVariable("CSAGENT_API_KEY", "from-env");
                Assert(ArgumentParser.Parse(new[] { "--api" }).ApiKey == "from-env", "env key not read");
                Assert(ArgumentParser.Parse(new[] { "--api", "--api-key", "from-arg" }).ApiKey == "from-arg", "argument should win");
                Environment.SetEnvironmentVariable("CSAGENT_API_KEY", "");
                Assert(ArgumentParser.Parse(new[] { "--api" }).ApiKey is null, "empty key should mean no key");
            }
            finally { Environment.SetEnvironmentVariable("CSAGENT_API_KEY", old); }
            return "ok";
            await Task.CompletedTask;
        });

        Group("API mode / host and key policy");

        await T("loopback hosts are local, everything else is network-reachable", async () =>
        {
            foreach (var h in new[] { null, "", "localhost", "LOCALHOST", "127.0.0.1", "127.1.2.3", "::1", "[::1]" })
                Assert(ApiSecurity.IsLoopbackHost(h), $"'{h}' should be loopback");
            foreach (var h in new[] { "0.0.0.0", "*", "+", "192.168.1.10", "10.0.0.5", "myhost.example.com", "::" })
                Assert(!ApiSecurity.IsLoopbackHost(h), $"'{h}' should NOT be loopback");
            return "ok";
            await Task.CompletedTask;
        });

        await T("startup is refused for a network host without a key, allowed otherwise", async () =>
        {
            Assert(ApiSecurity.CheckStartup("0.0.0.0", null) is { } msg && msg.Contains("--api-key"), "0.0.0.0 without key must be refused");
            Assert(ApiSecurity.CheckStartup("192.168.1.10", "") is not null, "empty key counts as no key");
            Assert(ApiSecurity.CheckStartup("0.0.0.0", "s3cret") is null, "0.0.0.0 with key must start");
            Assert(ApiSecurity.CheckStartup("localhost", null) is null, "localhost without key must start");
            return "ok";
            await Task.CompletedTask;
        });

        Group("API mode / authentication");

        await T("no key configured -> every request is accepted", async () =>
        {
            Assert(ApiSecurity.IsAuthorized(null, null, null) && ApiSecurity.IsAuthorized("", "Bearer x", null), "should accept");
            return "ok";
            await Task.CompletedTask;
        });

        await T("Bearer and X-API-Key accepted; wrong, partial, missing or malformed credentials refused", async () =>
        {
            const string k = "s3cret-key";
            Assert(ApiSecurity.IsAuthorized(k, "Bearer s3cret-key", null), "Bearer");
            Assert(ApiSecurity.IsAuthorized(k, "bearer s3cret-key", null), "bearer lowercase scheme");
            Assert(ApiSecurity.IsAuthorized(k, null, "s3cret-key"), "X-API-Key");
            Assert(!ApiSecurity.IsAuthorized(k, null, null), "missing");
            Assert(!ApiSecurity.IsAuthorized(k, "", ""), "empty");
            Assert(!ApiSecurity.IsAuthorized(k, "Bearer wrong", null), "wrong bearer");
            Assert(!ApiSecurity.IsAuthorized(k, null, "wrong"), "wrong x-api-key");
            Assert(!ApiSecurity.IsAuthorized(k, "Bearer s3cret", null), "prefix of the key");
            Assert(!ApiSecurity.IsAuthorized(k, "Bearer s3cret-key-and-more", null), "key plus suffix");
            Assert(!ApiSecurity.IsAuthorized(k, "s3cret-key", null), "no Bearer scheme");
            Assert(!ApiSecurity.IsAuthorized(k, "Basic s3cret-key", null), "wrong scheme");
            return "ok";
            await Task.CompletedTask;
        });

        Group("API mode / auto-approve in the agent loop");

        await T("Confirm:false (--yes) -> destructive tool runs without asking; Confirm:true asks and a refusal blocks it", async () =>
        {
            var d = Tmp(); var memFile = Path.Combine(d, "agent_memory.json");
            var prev = Directory.GetCurrentDirectory(); Directory.SetCurrentDirectory(d);
            try
            {
                async Task<(int asked, bool fileExists)> Run(bool confirm, bool answer, string target)
                {
                    int n = 0;
                    using var mock = new MockLlm();
                    mock.Handler = req => Interlocked.Increment(ref n) switch
                    {
                        1 => (200, MockLlm.ToolCall("write_file", "{\"path\":\"" + target + "\",\"content\":\"hi\"}"), 0),
                        _ => (200, MockLlm.Text("done"), 0),
                    };
                    var msgs = new JsonArray { CodingAgent.SystemMessage(false), JsonHelpers.Message("user", "go") };
                    var obs = new SilentObserver { ConfirmAnswer = answer };
                    using var agent = new CodingAgent("k", mock.BaseUrl, "m", new AgentOptions(Confirm: confirm, Distill: false), obs, null,
                        new HybridMemoryManager(new SemanticMemory(), new ExactMemory()));
                    await agent.RunAsync(msgs, memFile);
                    return (obs.ConfirmCount, File.Exists(Path.Combine(d, target)));
                }

                var auto = await Run(confirm: false, answer: false, "auto.txt");
                Assert(auto.asked == 0 && auto.fileExists, $"auto-approve: asked={auto.asked} file={auto.fileExists}");
                var asked = await Run(confirm: true, answer: false, "refused.txt");
                Assert(asked.asked == 1 && !asked.fileExists, $"confirm+refuse: asked={asked.asked} file={asked.fileExists}");
                var accepted = await Run(confirm: true, answer: true, "accepted.txt");
                Assert(accepted.asked == 1 && accepted.fileExists, $"confirm+accept: asked={accepted.asked} file={accepted.fileExists}");
                return "auto: 0 questions, file written; confirm: asked once";
            }
            finally { Directory.SetCurrentDirectory(prev); }
        });

        await T("auto-approve does NOT disable the shell command filter", async () =>
        {
            var d = Tmp(); var memFile = Path.Combine(d, "agent_memory.json");
            var prev = Directory.GetCurrentDirectory(); Directory.SetCurrentDirectory(d);
            try
            {
                // The filter is platform-aware. Pick a command that the current platform's filter blocks
                // and that would be harmless even if it were not blocked.
                var dangerous = OperatingSystem.IsWindows()
                    ? "reg delete HKCU\\\\Software\\\\CsAgentTestNoSuchKey /f"
                    : "sudo true";
                int n = 0;
                using var mock = new MockLlm();
                mock.Handler = req => Interlocked.Increment(ref n) switch
                {
                    1 => (200, MockLlm.ToolCall("sh", "{\"cmd\":\"" + dangerous + "\"}"), 0),
                    _ => (200, MockLlm.Text("done"), 0),
                };
                var msgs = new JsonArray { CodingAgent.SystemMessage(false), JsonHelpers.Message("user", "go") };
                var obs = new SilentObserver();
                using var agent = new CodingAgent("k", mock.BaseUrl, "m", new AgentOptions(Confirm: false, Distill: false), obs, null,
                    new HybridMemoryManager(new SemanticMemory(), new ExactMemory()));
                await agent.RunAsync(msgs, memFile);
                Assert(obs.Results.Any(r => r.Contains("not allowed")), "dangerous command was not blocked: " + string.Join(" | ", obs.Results));
                return "blocked: " + obs.Results.First(r => r.Contains("not allowed"));
            }
            finally { Directory.SetCurrentDirectory(prev); }
        });
    }
}