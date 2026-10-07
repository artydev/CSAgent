using System.Text.Json.Nodes;
using CsAgent.Core.Llm;
using CsAgent.Shared;

namespace CsAgent.Tests;

static partial class Tests
{
    // ═════════════════════════ --endpoint / --vision-model / local LLM (Ollama) ═════════════════════════
    static async Task LlmEndpoint()
    {
        Group("LLM endpoint / Ollama");

        static T Clean<T>(Func<T> f)
        {
            var names = new[] { "CSAGENT_ENDPOINT", "CSAGENT_VISION_MODEL", "ALBERT_API_KEY" };
            var old = names.Select(Environment.GetEnvironmentVariable).ToArray();
            foreach (var n in names) Environment.SetEnvironmentVariable(n, null);
            LlmConfig.Reset();
            try { return f(); }
            finally
            {
                for (int i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], old[i]);
                LlmConfig.Reset();
            }
        }

        await T("--endpoint and --vision-model are parsed; defaults are null", async () =>
        {
            var d = Clean(() => ArgumentParser.Parse(Array.Empty<string>()));
            Assert(d.Endpoint is null && d.VisionModel is null, "defaults");
            var a = Clean(() => ArgumentParser.Parse(new[] { "--endpoint", "http://localhost:11434/v1", "--vision-model", "llava", "--model", "qwen2.5-coder:14b" }));
            Assert(a.Endpoint == "http://localhost:11434/v1" && a.VisionModel == "llava" && a.ModelOverride == "qwen2.5-coder:14b", "parsed wrong");
            return "ok";
            await Task.CompletedTask;
        });

        await T("environment variables CSAGENT_ENDPOINT / CSAGENT_VISION_MODEL are used; the argument wins", async () =>
        {
            var r = Clean(() =>
            {
                Environment.SetEnvironmentVariable("CSAGENT_ENDPOINT", "http://env:1/v1");
                Environment.SetEnvironmentVariable("CSAGENT_VISION_MODEL", "env-vision");
                var e = ArgumentParser.Parse(Array.Empty<string>());
                var o = ArgumentParser.Parse(new[] { "--endpoint", "http://arg:2/v1" });
                return (e, o);
            });
            Assert(r.e.Endpoint == "http://env:1/v1" && r.e.VisionModel == "env-vision", "env not read");
            Assert(r.o.Endpoint == "http://arg:2/v1", "argument should win");
            return "ok";
            await Task.CompletedTask;
        });

        await T("option values are not taken as the memory file; a positional file still is", async () =>
        {
            var a = Clean(() => ArgumentParser.Parse(new[] { "--endpoint", "http://localhost:11434/v1", "--vision-model", "llava" }));
            Assert(a.MemoryFile == "agent_memory", a.MemoryFile);
            var b = Clean(() => ArgumentParser.Parse(new[] { "--endpoint", "http://localhost:11434/v1", "mem1" }));
            Assert(b.MemoryFile == "mem1", b.MemoryFile);
            return "ok";
            await Task.CompletedTask;
        });

        await T("LlmConfig: defaults, Configure, blank keeps current, trailing slash trimmed", async () =>
        {
            Clean(() =>
            {
                Assert(LlmConfig.Endpoint == LlmSettings.Endpoint && LlmConfig.VisionModel == LlmSettings.VisionModel, "defaults");
                LlmConfig.Configure("http://localhost:11434/v1/", "llava");
                Assert(LlmConfig.Endpoint == "http://localhost:11434/v1" && LlmConfig.VisionModel == "llava", "configure");
                LlmConfig.Configure(null, "  ");
                Assert(LlmConfig.Endpoint == "http://localhost:11434/v1" && LlmConfig.VisionModel == "llava", "blank must keep");
                return 0;
            });
            return "ok";
            await Task.CompletedTask;
        });

        await T("IsLocalEndpoint: localhost / 127.x / ::1 are local; remote hosts and garbage are not", async () =>
        {
            foreach (var u in new[] { "http://localhost:11434/v1", "http://127.0.0.1:8080/v1", "http://[::1]:11434/v1", "http://LOCALHOST/v1" })
                Assert(LlmConfig.IsLocalEndpoint(u), u);
            foreach (var u in new[] { "https://albert.api.etalab.gouv.fr/v1", "http://192.168.1.5:11434/v1", "http://localhost.evil.com/v1", "not a url", "" })
                Assert(!LlmConfig.IsLocalEndpoint(u), u);
            return "ok";
            await Task.CompletedTask;
        });

        await T("API key: local endpoint without key gets a placeholder; remote without key stays empty; a real key is kept", async () =>
        {
            Clean(() =>
            {
                Assert(LlmConfig.ResolveApiKey() == "", "default endpoint needs a key");
                LlmConfig.Configure("http://localhost:11434/v1", null);
                Assert(LlmConfig.ResolveApiKey() == LlmConfig.LocalPlaceholderKey, "local placeholder");
                Environment.SetEnvironmentVariable("ALBERT_API_KEY", "real");
                Assert(LlmConfig.ResolveApiKey() == "real", "real key must win");
                LlmConfig.Configure("http://192.168.1.5:11434/v1", null);
                Environment.SetEnvironmentVariable("ALBERT_API_KEY", null);
                Assert(LlmConfig.ResolveApiKey() == "", "remote endpoint needs a key");
                return 0;
            });
            return "ok";
            await Task.CompletedTask;
        });

        await T("the configured endpoint receives the chat request (mock server standing in for Ollama)", async () =>
        {
            using var mock = new MockLlm { Handler = _ => (200, MockLlm.Text("pong"), 0) };
            LlmConfig.Reset();
            try
            {
                LlmConfig.Configure(mock.BaseUrl, null);
                using var client = new LlmClient(LlmConfig.ResolveApiKey(), LlmConfig.Endpoint, "qwen2.5-coder:14b");
                var msgs = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "ping" } };
                var res = await client.CompleteChatAsync(msgs);
                Assert(res["choices"]?[0]?["message"]?["content"]?.GetValue<string>() == "pong", "reply");
                Assert(mock.Requests.Count == 1 && mock.Requests[0]["model"]?.GetValue<string>() == "qwen2.5-coder:14b", "request/model");
            }
            finally { LlmConfig.Reset(); }
            return "ok";
        });
    }
}
