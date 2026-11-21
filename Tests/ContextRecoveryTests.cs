using System.Text.Json.Nodes;
using CsAgent.Core.Agent;
using CsAgent.Core.Llm;
using CsAgent.Shared;

namespace CsAgent.Tests;

static partial class Tests
{
    const string OverflowBody =
        "{\"detail\":{\"error\":{\"message\":\"This model's maximum context length is 131072 tokens. However, your prompt contains at least 131073 input tokens, which exceeds this limit even without any output tokens.\",\"type\":\"BadRequestError\",\"param\":\"input_tokens\",\"code\":400}}}";

    static JsonNode AssistantCall(string id, string argsJson) => new JsonObject
    {
        ["role"] = "assistant",
        ["content"] = null,
        ["tool_calls"] = new JsonArray { new JsonObject
        {
            ["id"] = id, ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = "write_file", ["arguments"] = argsJson }
        } }
    };

    static JsonNode ImageMsg(string data) => new JsonObject
    {
        ["role"] = "user",
        ["content"] = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = "look" },
            new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + data } }
        }
    };

    // A 400 "maximum context length" is not fatal: the history is shortened and the request sent again.
    static async Task ContextRecoveryGroup()
    {
        Group("Context too long (400): history shortened, request retried");
        CsAgent.Core.Llm.Loc.Set("en");
        var fast = new RetryPolicy(MaxAttempts: 3, BaseDelayMs: 5, BackoffFactor: 1.0, MaxDelayMs: 20,
                                   ConnectionRetries: 0, ConnectionDelayMs: 5);

        JsonArray Heavy()
        {
            var big = new string('x', 40_000);
            var m = new JsonArray { JsonHelpers.Message("system", "sys"), JsonHelpers.Message("user", "build the thing") };
            m.Add(ImageMsg(big));
            for (int i = 1; i <= 4; i++)
            {
                m.Add(AssistantCall("c" + i, $"{{\"path\":\"f{i}.cs\",\"content\":\"{big}\"}}"));
                m.Add(ToolMsg("c" + i, big));
            }
            m.Add(JsonHelpers.Message("user", "continue"));
            return m;
        }

        await T("IsContextOverflow reads the limit and the actual size; other 400s are not overflows", async () =>
        {
            await Task.CompletedTask;
            Assert(ContextRecovery.IsContextOverflow(OverflowBody, out var lim, out var act), "not detected");
            Assert(lim == 131072 && act == 131073, $"lim={lim} act={act}");
            Assert(!ContextRecovery.IsContextOverflow("{\"detail\":\"Unexpected role 'tool' after role 'user'\"}", out _, out _), "false positive");
            return "limit 131072, actual 131073";
        });

        await T("the client shortens the history and the second request succeeds", async () =>
        {
            int n = 0;
            using var mock = new MockLlm { Handler = _ => ++n == 1 ? (400, JsonNode.Parse(OverflowBody)!, 0) : (200, MockLlm.Text("ok"), 0) };
            using var client = new LlmClient("k", mock.BaseUrl, "m", fast);
            string? note = null;
            client.ContextShrunk += s => note = s;
            var msgs = Heavy();
            int before = ContextRecovery.FullLength(msgs);
            var r = await client.CompleteChatAsync(msgs);
            Assert(r["choices"]![0]!["message"]!["content"]!.GetValue<string>() == "ok", "answer");
            Assert(mock.Requests.Count == 2, "requests=" + mock.Requests.Count);
            Assert(mock.Requests[1].ToJsonString().Length < mock.Requests[0].ToJsonString().Length * 0.7, "second request not smaller");
            Assert(ContextRecovery.FullLength(msgs) < before * 0.7, "history not shrunk in place");
            Assert(note is not null && note.Contains("131"), "note: " + note);
            Assert(msgs[^1]!["content"]!.GetValue<string>() == "continue", "newest message lost");
            Assert(msgs[0]!["role"]!.GetValue<string>() == "system", "system message lost");
            return "2 requests, smaller";
        });

        await T("a server that always overflows: at most 2 recoveries, then the API 400 error", async () =>
        {
            using var mock = new MockLlm { Handler = _ => (400, JsonNode.Parse(OverflowBody)!, 0) };
            using var client = new LlmClient("k", mock.BaseUrl, "m", fast);
            try { await client.CompleteChatAsync(Heavy()); Assert(false, "no exception"); }
            catch (HttpRequestException e) { Assert(e.Message.StartsWith("API 400:"), e.Message); }
            Assert(mock.Requests.Count == 1 + ContextRecovery.MaxRecoveries, "requests=" + mock.Requests.Count);
            return "3 requests, then the error";
        });

        await T("Shrink: old images become a note, old tool-call arguments stay valid JSON, tool pairs stay valid", async () =>
        {
            await Task.CompletedTask;
            var msgs = Heavy();
            msgs.Insert(2, ImageMsg("OLDIMAGE" + new string('y', 5000)));   // an older image; the one added by Heavy() is newer
            ContextRecovery.Shrink(msgs, 131072, 131073, 1);
            var all = msgs.ToJsonString();
            Assert(!all.Contains("OLDIMAGE"), "old image kept");
            foreach (var m in msgs)
                if (m?["tool_calls"] is JsonArray calls)
                    foreach (var c in calls)
                        Assert(JsonNode.Parse(c!["function"]!["arguments"]!.GetValue<string>()) is JsonObject, "arguments not JSON");
            var copy = LlmClient.SanitizeMessages(msgs);
            Assert(copy.Count == msgs.Count, "repair removed messages: the pairs were broken");
            return "valid";
        });

        await T("StubOldImages: only the newest image message keeps its image; a message left without one gets a plain string again", async () =>
        {
            await Task.CompletedTask;
            var msgs = new JsonArray { JsonHelpers.Message("system", "sys"), ImageMsg("AAA"), JsonHelpers.Message("assistant", "seen"), ImageMsg("BBB") };
            int n = ContextRecovery.StubOldImages(msgs, keepNewest: 1);
            Assert(n == 1, "stubbed=" + n);
            Assert(msgs[1]!["content"] is JsonValue, "old message should be a plain string");
            Assert(msgs[1]!["content"]!.GetValue<string>().Contains(ContextRecovery.ImageNote) && msgs[1]!["content"]!.GetValue<string>().Contains("look"), "text or note lost");
            Assert(msgs[3]!["content"] is JsonArray && msgs.ToJsonString().Contains("BBB") && !msgs.ToJsonString().Contains("AAA"), "wrong image kept");
            Assert(ContextRecovery.StubOldImages(msgs, 1) == 0, "second run changed something");
            Assert(ContextRecovery.StubOldImages(msgs, 0) == 1 && !JsonHelpers.HistoryContainsImage(msgs), "keepNewest 0 should remove all");
            return "1 kept, then 0";
        });

        await T("Shrink: pass 1 keeps the newest image at detail low, pass 2 removes every image", async () =>
        {
            await Task.CompletedTask;
            var msgs = new JsonArray { JsonHelpers.Message("system", "sys"), JsonHelpers.Message("user", "task"), ImageMsg("OLD"), JsonHelpers.Message("assistant", "ok"), ImageMsg("NEW"), JsonHelpers.Message("user", "go") };
            ContextRecovery.Shrink(msgs, 131072, 131073, 1);
            var j = msgs.ToJsonString();
            Assert(j.Contains("NEW") && !j.Contains("OLD") && j.Contains("\"detail\":\"low\""), "pass 1: " + j.Length);
            ContextRecovery.Shrink(msgs, 131072, 131073, 2);
            Assert(!JsonHelpers.HistoryContainsImage(msgs), "pass 2 left an image");
            return "kept low, then none";
        });

        await T("an image-heavy history: the retry succeeds without the old images", async () =>
        {
            int n = 0;
            using var mock = new MockLlm { Handler = _ => ++n == 1 ? (400, JsonNode.Parse(OverflowBody)!, 0) : (200, MockLlm.Text("ok"), 0) };
            using var client = new LlmClient("k", mock.BaseUrl, "m", fast);
            var big = new string('z', 200_000);
            var msgs = new JsonArray { JsonHelpers.Message("system", "sys"), JsonHelpers.Message("user", "task"), ImageMsg(big), JsonHelpers.Message("assistant", "a"), ImageMsg(big), JsonHelpers.Message("user", "go") };
            await client.CompleteChatAsync(msgs);
            int first = mock.Requests[0].ToJsonString().Length, second = mock.Requests[1].ToJsonString().Length;
            Assert(second < first * 0.6, $"first={first} second={second}");
            return $"{first:N0} -> {second:N0} chars";
        });
    }
}