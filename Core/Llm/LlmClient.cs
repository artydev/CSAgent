using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace CsAgent.Core.Llm;

public sealed class LlmClient : IDisposable
{
    private readonly HttpClient _http;
    private string     _model;
    private readonly string     _baseUrl;
    private readonly RetryPolicy _retry;

    public LlmClient(string apiKey, string baseUrl, string model, RetryPolicy? retry = null)
    {
        _model   = model;
        _baseUrl = baseUrl.TrimEnd('/');
        _retry   = (retry ?? RetryPolicy.Default).Validate();
        _http    = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", apiKey);
    }

    /// <summary>
    /// The currently active model identifier. Can be changed at runtime
    /// (e.g. via the switch_model tool) to switch models mid-session.
    /// </summary>
    public string Model
    {
        get => _model;
        set => _model = value;
    }

    /// <summary>
    /// Sends a chat-completions request, retrying transient HTTP 429 (rate limit)
    /// responses with exponential backoff. The server's <c>Retry-After</c> header is
    /// honored when present. Other non-2xx statuses throw immediately.
    /// </summary>
    public async Task<JsonNode> CompleteChatAsync(
        JsonArray messages,
        JsonArray? tools = null,
        CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"]       = _model,
            ["temperature"] = 0.1,
            ["messages"]    = messages.DeepClone()
        };
        if (tools is { Count: > 0 })
        {
            body["tools"]       = tools.DeepClone();
            body["tool_choice"] = "auto";
        }

        var payload = body.ToJsonString();

        for (int attempt = 1; attempt <= _retry.MaxAttempts; attempt++)
        {
            // StringContent is single-use, so rebuild the request each attempt.
            var req = new StringContent(payload, Encoding.UTF8, "application/json");
            var res = await _http.PostAsync($"{_baseUrl}/chat/completions", req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);

            if (res.IsSuccessStatusCode)
                return JsonNode.Parse(raw) ?? throw new InvalidDataException("Empty API response");

            // Only retry 429 (rate limit). Other errors fail fast.
            if ((int)res.StatusCode != 429 || attempt == _retry.MaxAttempts)
                throw new HttpRequestException($"API {(int)res.StatusCode}: {raw}");

            var delay = ComputeDelay(res, attempt);
            await Task.Delay(delay, ct);
        }

        throw new HttpRequestException("API retry loop exhausted unexpectedly.");
    }

    /// <summary>
    /// Computes the delay before the next retry. Prefers the server's
    /// <c>Retry-After</c> header; otherwise applies exponential backoff
    /// (<c>BaseDelayMs * BackoffFactor^(attempt-1)</c>) capped at <c>MaxDelayMs</c>.
    /// </summary>
    private TimeSpan ComputeDelay(HttpResponseMessage res, int attempt)
    {
        if (res.Headers.RetryAfter is { } ra)
        {
            if (ra.Delta is { } delta)
                return Clamp(delta);
            if (ra.Date is { } date)
            {
                var diff = date - DateTimeOffset.UtcNow;
                if (diff > TimeSpan.Zero)
                    return Clamp(diff);
            }
        }

        var backoff = _retry.BaseDelayMs * Math.Pow(_retry.BackoffFactor, attempt - 1);
        return Clamp(TimeSpan.FromMilliseconds(backoff));
    }

    private TimeSpan Clamp(TimeSpan t)
    {
        var max = TimeSpan.FromMilliseconds(_retry.MaxDelayMs);
        return t > max ? max : t;
    }

    public void Dispose() => _http.Dispose();
}
