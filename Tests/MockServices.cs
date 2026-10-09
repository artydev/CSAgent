using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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

// ───────────────────────── mock OpenAI-compatible server ─────────────────────────
sealed class MockLlm : IDisposable
{
    // A tiny HTTP/1.1 server on a raw TcpListener rather than HttpListener: on Windows,
    // HttpListener needs a URL reservation or administrator rights, and a fixed/random
    // port can already be taken. Port 0 lets the OS pick a free port.
    readonly TcpListener _l;
    public string BaseUrl { get; }
    public List<JsonNode> Requests { get; } = new();
    public Func<JsonNode, (int status, JsonNode body, int delayMs)> Handler { get; set; } = _ => (500, new JsonObject(), 0);
    readonly CancellationTokenSource _cts = new();

    public MockLlm()
    {
        _l = new TcpListener(IPAddress.Loopback, 0);
        _l.Start();
        BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_l.LocalEndpoint).Port}/v1";
        _ = Task.Run(Loop);
    }

    async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _l.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var node = JsonNode.Parse(await ReadBodyAsync(stream))!;

                (int status, JsonNode resp, int delay) r;
                lock (Requests) { Requests.Add(node); r = Handler(node); }
                if (r.delay > 0) await Task.Delay(r.delay, _cts.Token);

                var payload = Encoding.UTF8.GetBytes(r.resp.ToJsonString());
                var head = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {r.status} {(r.status == 200 ? "OK" : "Error")}\r\n" +
                    $"Content-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head);
                await stream.WriteAsync(payload);
                await stream.FlushAsync();
            }
            catch { /* the client went away (timeout, cancellation) */ }
        }
    }

    // Reads one request and returns its body (the client always sends Content-Length).
    internal static async Task<string> ReadBodyAsync(NetworkStream stream)
    {
        var data = new List<byte>();
        var chunk = new byte[8192];
        int headerEnd = -1;

        while (headerEnd < 0)
        {
            int n = await stream.ReadAsync(chunk);
            if (n == 0) throw new IOException("connection closed");
            data.AddRange(chunk.AsSpan(0, n).ToArray());
            headerEnd = IndexOfHeaderEnd(data);
        }

        var header = Encoding.ASCII.GetString(data.GetRange(0, headerEnd).ToArray());
        int length = 0;
        foreach (var line in header.Split("\r\n"))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(line["Content-Length:".Length..].Trim());

        int bodyStart = headerEnd + 4;
        while (data.Count - bodyStart < length)
        {
            int n = await stream.ReadAsync(chunk);
            if (n == 0) throw new IOException("connection closed");
            data.AddRange(chunk.AsSpan(0, n).ToArray());
        }
        return Encoding.UTF8.GetString(data.GetRange(bodyStart, length).ToArray());
    }

    static int IndexOfHeaderEnd(List<byte> d)
    {
        for (int i = 0; i + 3 < d.Count; i++)
            if (d[i] == 13 && d[i + 1] == 10 && d[i + 2] == 13 && d[i + 3] == 10) return i;
        return -1;
    }

    public static bool IsDistill(JsonNode req) =>
        req["messages"]?[0]?["content"]?.GetValue<string>()?.Contains("long-term memory of an autonomous coding agent") == true;

    public static JsonNode Text(string content, string finish = "stop") => new JsonObject
    {
        ["choices"] = new JsonArray { new JsonObject {
            ["finish_reason"] = finish,
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content } } }
    };

    public static JsonNode ToolCall(string name, string argsJson, string id = "c1") => new JsonObject
    {
        ["choices"] = new JsonArray { new JsonObject {
            ["finish_reason"] = "tool_calls",
            ["message"] = new JsonObject {
                ["role"] = "assistant", ["content"] = null,
                ["tool_calls"] = new JsonArray { new JsonObject {
                    ["id"] = id, ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = name, ["arguments"] = argsJson } } } } } }
    };

    public void Dispose() { _cts.Cancel(); try { _l.Stop(); } catch { } }
}

sealed class SilentObserver : IAgentObserver
{
    public List<string> Results = new();
    public Task OnStep(int n, int max) => Task.CompletedTask;
    public Task OnThought(string t) => Task.CompletedTask;
    public Task OnToolCall(string n, string a) => Task.CompletedTask;
    public Task OnToolResult(string r, bool e) { Results.Add((e ? "ERR " : "OK  ") + r); return Task.CompletedTask; }
    public Task OnDone(string m) => Task.CompletedTask;
    public Task OnError(string m) { Results.Add("AGENT-ERROR " + m); return Task.CompletedTask; }
    public List<string> Warnings = new();
    public Task OnWarning(string m) { lock (Warnings) Warnings.Add(m); return Task.CompletedTask; }
    public Task OnDanger(string m) => Task.CompletedTask;
    /// <summary>What OnConfirm answers (default: allow).</summary>
    public bool ConfirmAnswer = true;
    int _confirmCount;
    /// <summary>How many times the agent asked for a confirmation.</summary>
    public int ConfirmCount => _confirmCount;
    public Task<bool> OnConfirm(string t) { Interlocked.Increment(ref _confirmCount); return Task.FromResult(ConfirmAnswer); }
}
