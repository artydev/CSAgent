using System.Text.Json.Nodes;
using CsAgent.Core.Llm;
using CsAgent.Shared;

namespace CsAgent.Tests;

static partial class Tests
{
    // An assistant message that calls tools (content may be empty, as DeepSeek sends it).
    static JsonObject AsstCalls(string content, params string[] ids)
    {
        var calls = new JsonArray();
        foreach (var id in ids)
            calls.Add(new JsonObject
            {
                ["id"] = id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = "read_file", ["arguments"] = "{}" }
            });
        var m = new JsonObject { ["role"] = "assistant", ["tool_calls"] = calls };
        if (content.Length > 0) m["content"] = content;
        return m;
    }

    static string PairRoles(JsonArray msgs) =>
        string.Join(",", msgs.Select(m => m?["role"]?.GetValue<string>() ?? "?"));

    // Strict servers (vLLM chat templates: mistral, gemma, ...) answer HTTP 400
    // "Unexpected role 'tool' after role 'user'" when a tool result has lost its assistant tool call.
    static async Task ToolPairGroup()
    {
        Group("Tool-call pairs: no orphan tool result is sent (strict servers)");

        await T("RepairToolPairs: a tool result right after a user message (the reported 400) is removed", async () =>
        {
            await Task.CompletedTask;
            var msgs = new JsonArray
            {
                JsonHelpers.Message("system", "sys"),
                JsonHelpers.Message("user", "[SESSION CONTEXT]\nnotes"),
                ToolMsg("c1", "result 1"),
                ToolMsg("c2", "result 2"),
                JsonHelpers.Message("assistant", "done"),
                JsonHelpers.Message("user", "next")
            };
            int removed = JsonHelpers.RepairToolPairs(msgs);
            Assert(removed == 2, "removed=" + removed);
            Assert(PairRoles(msgs) == "system,user,assistant,user", PairRoles(msgs));
            return "2 orphans removed";
        });

        await T("RepairToolPairs: a valid conversation (several calls in one turn) is left unchanged", async () =>
        {
            await Task.CompletedTask;
            var msgs = new JsonArray
            {
                JsonHelpers.Message("system", "sys"),
                JsonHelpers.Message("user", "go"),
                AsstCalls("", "c1", "c2"),
                ToolMsg("c1", "r1"),
                ToolMsg("c2", "r2"),
                AsstCalls("thinking", "c3"),
                ToolMsg("c3", "r3"),
                JsonHelpers.Message("assistant", "done"),
                JsonHelpers.Message("user", "more")
            };
            var before = msgs.ToJsonString();
            Assert(JsonHelpers.RepairToolPairs(msgs) == 0, "something was removed");
            Assert(msgs.ToJsonString() == before, "the conversation changed");
            return "unchanged";
        });

        await T("RepairToolPairs: an assistant turn with a missing result is dropped with its remaining results", async () =>
        {
            await Task.CompletedTask;
            var msgs = new JsonArray
            {
                JsonHelpers.Message("system", "sys"),
                JsonHelpers.Message("user", "go"),
                AsstCalls("", "c1", "c2"),
                ToolMsg("c1", "r1"),
                JsonHelpers.Message("assistant", "done")
            };
            int removed = JsonHelpers.RepairToolPairs(msgs);
            Assert(removed == 2, "removed=" + removed);
            Assert(PairRoles(msgs) == "system,user,assistant", PairRoles(msgs));
            Assert(Content(msgs[2]) == "done", "the plain assistant message was lost");
            return "turn dropped";
        });

        await T("RepairToolPairs: a trailing assistant tool call without any result, and a result with the wrong id, are removed", async () =>
        {
            await Task.CompletedTask;
            var trailing = new JsonArray { JsonHelpers.Message("system", "sys"), JsonHelpers.Message("user", "go"), AsstCalls("", "c1") };
            Assert(JsonHelpers.RepairToolPairs(trailing) == 1 && PairRoles(trailing) == "system,user", PairRoles(trailing));

            var wrongId = new JsonArray { JsonHelpers.Message("system", "sys"), JsonHelpers.Message("user", "go"), AsstCalls("", "c1"), ToolMsg("zzz", "r") };
            JsonHelpers.RepairToolPairs(wrongId);
            Assert(PairRoles(wrongId) == "system,user", PairRoles(wrongId));
            return "ok";
        });

        await T("TrimHistory: cutting the oldest messages never leaves a tool result without its call", async () =>
        {
            await Task.CompletedTask;
            // total = 1 + 4 + 10 + 100 + 4 = 119 characters; with a limit of 110 the loop stops right after
            // dropping the assistant message, which used to leave the tool result behind it.
            var msgs = new JsonArray
            {
                JsonHelpers.Message("system", "s"),
                JsonHelpers.Message("user", "task"),
                AsstCalls("0123456789", "c1"),
                ToolMsg("c1", new string('r', 100)),
                JsonHelpers.Message("user", "next")
            };
            JsonHelpers.TrimHistory(msgs, 110);
            Assert(!PairRoles(msgs).Contains("tool"), "orphan tool left: " + PairRoles(msgs));
            Assert(PairRoles(msgs) == "system,user", PairRoles(msgs));
            return PairRoles(msgs);
        });

        await T("SanitizeMessages: the copy sent to the server has no orphan tool result", async () =>
        {
            await Task.CompletedTask;
            var msgs = new JsonArray
            {
                JsonHelpers.Message("system", "sys"),
                JsonHelpers.Message("user", "[SESSION CONTEXT]\nnotes"),
                ToolMsg("c1", "orphan"),
                JsonHelpers.Message("assistant", "ok")
            };
            var sent = LlmClient.SanitizeMessages(msgs);
            Assert(PairRoles(sent) == "system,user,assistant", PairRoles(sent));
            Assert(msgs.Count == 4, "the stored history must not be changed");
            return "sent: " + PairRoles(sent);
        });
    }
}