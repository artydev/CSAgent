using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CsAgent.Core.Abstractions;
using Microsoft.AspNetCore.Http;

namespace CsAgent;

/// <summary>
/// Bridges the SSE confirm event and the POST /api/confirm response.
/// One broker instance is shared per request scope via ApiEndpoints.
/// AOT-safe: no reflection.
/// </summary>
public sealed class ConfirmationBroker
{
    private TaskCompletionSource<bool>? _pending;

    /// <summary>
    /// Called by SseObserver.OnConfirm — suspends the agent loop until
    /// the user clicks Approve or Decline in the UI.
    /// </summary>
    public Task<bool> WaitForAnswerAsync()
    {
        _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _pending.Task;
    }

    /// <summary>
    /// Called by POST /api/confirm — resolves the pending TCS.
    /// Returns false if no confirmation is currently pending.
    /// </summary>
    public bool Resolve(bool allow)
    {
        var tcs = _pending;
        if (tcs is null) return false;
        _pending = null;
        tcs.TrySetResult(allow);
        return true;
    }

    public bool HasPending => _pending is not null;
}

public class SseObserver(HttpResponse res, ConfirmationBroker broker) : IAgentObserver
{
    private static int _msgId = 0;

    private async Task Send(string type, object data)
    {
        var id = Interlocked.Increment(ref _msgId);
        var payload = new SseMessage(id, type, data);
        var json = JsonSerializer.Serialize(payload, WebJsonContext.Default.SseMessage);
        Debug.WriteLine(json);
        await res.WriteAsync($"data: {json}\n\n");
        await res.Body.FlushAsync();
    }

    public Task OnStep(int n, int m) => Send("step", new SseStep(n, m));
    public Task OnThought(string t) => Send("thought", t);
    public Task OnAssistantModel(string m) => Send("model", m);
    public Task OnToolCall(string n, string a) => Send("call", new SseCall(n, a));
    public Task OnToolResult(string r, bool e) => Send("result", new SseResult(r, e));
    public Task OnDone(string m) => Send("done", m);
    public Task OnError(string m) => Send("error", m);
    public Task OnWarning(string m) => Send("warning", m);
    public Task OnDanger(string m) => Send("danger", m);

    /// <summary>
    /// Sends a "confirm" SSE event to the frontend and suspends until the
    /// user clicks Approve or Decline (resolved via POST /api/confirm).
    /// </summary>
    public async Task<bool> OnConfirm(string toolName)
    {
        await Send("confirm", new SseConfirm(toolName));
        return await broker.WaitForAnswerAsync();
    }
}

// ── SSE message types ──

public record SseMessage(int id, string type, object data);
public record SseStep(int n, int m);
public record SseCall(string n, string a);
public record SseResult(string r, bool e);
public record SseConfirm(string tool);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(SseMessage))]
[JsonSerializable(typeof(SseStep))]
[JsonSerializable(typeof(SseCall))]
[JsonSerializable(typeof(SseResult))]
[JsonSerializable(typeof(SseConfirm))]
internal partial class WebJsonContext : JsonSerializerContext { }