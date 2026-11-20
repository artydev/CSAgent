using System.Text.Json.Nodes;
using CsAgent.Core.Llm;
using CsAgent.Shared;

namespace CsAgent.Tests;

static partial class Tests
{
    // "An error occurred while sending the request" is all .NET says when the connection fails before an
    // answer arrives: the call is retried, and the final message names the host and the real cause.
    static async Task ConnectionRetryGroup()
    {
        Group("Lost connections: retried, and the error says why");
        CsAgent.Core.Llm.Loc.Set("en");
        var fast = new RetryPolicy(MaxAttempts: 3, BaseDelayMs: 5, BackoffFactor: 1.0, MaxDelayMs: 20,
                                   ConnectionRetries: 2, ConnectionDelayMs: 5);
        JsonArray Hi() => new() { JsonHelpers.Message("user", "hi") };

        await T("a server that drops the first 2 connections: the 3rd try succeeds", async () =>
        {
            using var mock = new MockLlm { DropFirst = 2, Handler = _ => (200, MockLlm.Text("hello"), 0) };
            using var client = new LlmClient("k", mock.BaseUrl, "m", fast);
            var r = await client.CompleteChatAsync(Hi());
            Assert(r["choices"]![0]!["message"]!["content"]!.GetValue<string>() == "hello", "answer");
            Assert(mock.Dropped == 2 && mock.Requests.Count == 1, $"dropped={mock.Dropped} answered={mock.Requests.Count}");
            return "2 drops, then the answer";
        });

        await T("a server that always drops: HttpRequestException naming the host, the attempts and the cause", async () =>
        {
            using var mock = new MockLlm { DropFirst = 99, Handler = _ => (200, MockLlm.Text("never"), 0) };
            using var client = new LlmClient("k", mock.BaseUrl, "m", fast);
            try { await client.CompleteChatAsync(Hi()); Assert(false, "no exception"); }
            catch (HttpRequestException e)
            {
                var host = new Uri(mock.BaseUrl).Authority;
                Assert(e.Message.StartsWith($"Connection to {host} failed after 3 attempts: "), e.Message);
                Assert(!e.Message.StartsWith("An error occurred"), "the generic text is not the whole message");
                Assert(e.InnerException is not null, "the original exception is kept");
            }
            Assert(mock.Dropped == 3, $"dropped={mock.Dropped}");
            return "3 tries, readable message";
        });

        await T("ConnectionRetries = 0: a single try", async () =>
        {
            using var mock = new MockLlm { DropFirst = 99, Handler = _ => (200, MockLlm.Text("never"), 0) };
            using var client = new LlmClient("k", mock.BaseUrl, "m", fast with { ConnectionRetries = 0 });
            try { await client.CompleteChatAsync(Hi()); Assert(false, "no exception"); }
            catch (HttpRequestException e) { Assert(e.Message.Contains("after 1 attempt:"), e.Message); }
            Assert(mock.Dropped == 1, $"dropped={mock.Dropped}");
            return "1 try";
        });

        await T("an HTTP error status is NOT a lost connection: one request, no retry", async () =>
        {
            using var mock = new MockLlm { Handler = _ => (500, new JsonObject { ["detail"] = "boom" }, 0) };
            using var client = new LlmClient("k", mock.BaseUrl, "m", fast);
            try { await client.CompleteChatAsync(Hi()); Assert(false, "no exception"); }
            catch (HttpRequestException e) { Assert(e.Message.StartsWith("API 500:"), e.Message); }
            Assert(mock.Requests.Count == 1, $"{mock.Requests.Count} requests");
            return "1 request";
        });

        await T("a cancellation by the caller is not retried and stays a cancellation", async () =>
        {
            using var mock = new MockLlm { Handler = _ => (200, MockLlm.Text("late"), 3000) };
            using var client = new LlmClient("k", mock.BaseUrl, "m", fast);
            using var cts = new CancellationTokenSource(150);
            try { await client.CompleteChatAsync(Hi(), null, cts.Token); Assert(false, "no exception"); }
            catch (OperationCanceledException) { }
            Assert(mock.Requests.Count <= 1, $"{mock.Requests.Count} requests");
            return "cancelled";
        });

        await T("DescribeNetworkError: the whole chain of causes, and a hint for the usual ones", async () =>
        {
            await Task.CompletedTask;
            var refused = new HttpRequestException("An error occurred while sending the request.",
                new System.Net.Sockets.SocketException(10061));
            var m1 = LlmClient.DescribeNetworkError(refused, "https://albert.api.etalab.gouv.fr/v1", 3);
            Assert(m1.StartsWith("Connection to albert.api.etalab.gouv.fr failed after 3 attempts: An error occurred while sending the request. -> "), m1);
            Assert(m1.Contains("ConnectionRefused"), m1);
            Assert(m1.Contains("--endpoint"), m1);

            var closed = new HttpRequestException("An error occurred while sending the request.",
                new IOException("The response ended prematurely."));
            var m2 = LlmClient.DescribeNetworkError(closed, "http://localhost:11434/v1", 1);
            Assert(m2.Contains("after 1 attempt:") && m2.Contains("closed the connection") && m2.Contains("localhost:11434"), m2);
            return "ok";
        });

        await T("RetryPolicy: the new values are validated", async () =>
        {
            await Task.CompletedTask;
            Assert(RetryPolicy.Default.ConnectionRetries == 2 && RetryPolicy.Default.ConnectionDelayMs == 2000, "defaults");
            foreach (var bad in new[] { new RetryPolicy(ConnectionRetries: -1), new RetryPolicy(ConnectionDelayMs: -1) })
            {
                try { bad.Validate(); Assert(false, "no exception"); } catch (ArgumentOutOfRangeException) { }
            }
            return "ok";
        });
    }
}
