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
            {
                var ok = JsonNode.Parse(raw) ?? throw new InvalidDataException("Empty API response");
                CountUsage(ok);
                return ok;
            }

            // Only retry 429 (rate limit). Other errors fail fast. A daily limit is not retried either:
            // it will not clear before it resets, so waiting only delays the message.
            if ((int)res.StatusCode == 429)
            {
                var kind = RateLimit.Classify(raw);
                if (kind == RateLimitKind.Day || attempt == _retry.MaxAttempts)
                    throw new RateLimitException($"API 429: {raw}", kind);
            }
            else
                throw new HttpRequestException($"API {(int)res.StatusCode}: {raw}");

            var delay = ComputeDelay(res, attempt);
            await Task.Delay(delay, ct);
        }

        throw new HttpRequestException("API retry loop exhausted unexpectedly.");
    }

    /// <summary>Tokens the API reported for the requests of this client (0 when the server sends no usage).</summary>
    public long PromptTokens { get; private set; }
    public long CompletionTokens { get; private set; }

    private void CountUsage(JsonNode response)
    {
        var u = response["usage"];
        if (u is null) return;
        long p = u["prompt_tokens"] is JsonValue pv && pv.TryGetValue<long>(out var pl) ? pl : 0;
        long c = u["completion_tokens"] is JsonValue cv && cv.TryGetValue<long>(out var cl) ? cl : 0;
        PromptTokens += p;
        CompletionTokens += c;
        TokenMeter.Add(p, c);
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
