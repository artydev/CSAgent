using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using CsAgent.Shared;

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
        _http    = CsAgent.Shared.HttpClients.Create(TimeSpan.FromMinutes(5));
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
    /// honored when present. Other non-2xx statuses throw immediately. A connection lost before any
    /// answer (reset, closed by the server) is retried <see cref="RetryPolicy.ConnectionRetries"/> times;
    /// when it still fails, the message names the host and the real cause.
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
            ["messages"]    = SanitizeMessages(messages)
        };
        if (tools is { Count: > 0 })
        {
            body["tools"]       = tools.DeepClone();
            body["tool_choice"] = "auto";
        }

        var payload = body.ToJsonString();

        int lost = 0;   // connections lost so far (before any answer); they do not use up the 429 attempts
        for (int attempt = 1; attempt <= _retry.MaxAttempts; attempt++)
        {
            HttpResponseMessage res;
            string raw;
            try
            {
                // StringContent is single-use, so rebuild the request each attempt.
                var req = new StringContent(payload, Encoding.UTF8, "application/json");
                res = await _http.PostAsync($"{_baseUrl}/chat/completions", req, ct);
                raw = await res.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // Not cancelled by the caller: the HttpClient timeout elapsed. Not retried (it would wait as long again).
                throw new HttpRequestException(
                    $"No answer from {Host(_baseUrl)} within {(int)_http.Timeout.TotalMinutes} minutes (timeout). " +
                    "The request may be too large or the model too slow: start a new conversation or send less text.", ex);
            }
            catch (Exception ex) when ((ex is HttpRequestException or IOException) && !ct.IsCancellationRequested)
            {
                // The connection failed before a complete answer arrived (reset, closed by the server, DNS, TLS...).
                if (lost < _retry.ConnectionRetries)
                {
                    lost++;
                    await Task.Delay(_retry.ConnectionDelayMs * (1 << (lost - 1)), ct);
                    attempt--;       // this try does not count as a rate-limit attempt
                    continue;
                }
                throw new HttpRequestException(DescribeNetworkError(ex, _baseUrl, lost + 1), ex);
            }

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

    /// <summary>
    /// A readable message for a failed connection: the host, how many tries were made, the whole chain of
    /// causes (.NET only says "An error occurred while sending the request" on the outer exception) and,
    /// for the usual causes, what to check.
    /// </summary>
    internal static string DescribeNetworkError(Exception ex, string baseUrl, int attempts)
    {
        var causes = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            var m = e.Message.Trim();
            if (e is System.Net.Sockets.SocketException se) m = $"{m} ({se.SocketErrorCode})";
            if (m.Length > 0 && !causes.Contains(m)) causes.Add(m);
        }
        var chain = string.Join(" -> ", causes);
        var lower = chain.ToLowerInvariant();
        string hint =
            lower.Contains("refused")                                   ? "Nothing answers at this address: check the endpoint (--endpoint) and that the server is running."
          : lower.Contains("name or service not known") || lower.Contains("no such host") || lower.Contains("nodename")
                                                                         ? "The server name cannot be resolved: check the endpoint spelling, the network and the VPN."
          : lower.Contains("ssl") || lower.Contains("tls") || lower.Contains("certificate")
                                                                         ? "The secure connection failed: check the proxy, the certificate and the system clock."
          : lower.Contains("prematurely") || lower.Contains("reset") || lower.Contains("forcibly") || lower.Contains("closed")
                                                                         ? "The server or a proxy closed the connection without answering: server overloaded, or request too large or too slow. Try again, or start a new conversation."
          : "Check the network, the proxy and the endpoint.";
        return $"Connection to {Host(baseUrl)} failed after {attempts} attempt{(attempts == 1 ? "" : "s")}: {chain}. {hint}";
    }

    private static string Host(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) ? u.Authority : baseUrl;

    // Keys allowed on a chat message when it is sent back to the server. Anything else
    // (reasoning, refusal, annotations, audio, ...) is echoed by some
    // servers (vLLM / gpt-oss behind Albert) but is not valid input, and some servers crash on it.
    private static readonly HashSet<string> AllowedKeys =
        new(StringComparer.Ordinal) { "role", "content", "name", "tool_calls", "tool_call_id", "reasoning_content" };

    /// <summary>
    /// Returns a clean copy of the history: no null-valued fields (gpt-oss/vLLM iterates
    /// <c>tool_calls</c> and fails with "'NoneType' object is not an iterator" when it is null),
    /// no server-only extras, and a string <c>content</c> on every message.
    /// </summary>
    internal static JsonArray SanitizeMessages(JsonArray messages)
    {
        var result = new JsonArray();
        foreach (var node in messages)
        {
            if (node is not JsonObject msg) { result.Add(node?.DeepClone()); continue; }

            var clean = new JsonObject();
            foreach (var (key, value) in msg)
            {
                if (value is null || !AllowedKeys.Contains(key)) continue;
                if (key == "tool_calls" && value is JsonArray { Count: 0 }) continue;
                clean[key] = value.DeepClone();
            }
            if (clean["content"] is null) clean["content"] = "";
            result.Add(clean);
        }
        // Never send a tool result without its assistant tool call (or the reverse): strict servers answer
        // 400 "Unexpected role 'tool' after role 'user'" (history cut by trimming, or written by another model).
        JsonHelpers.RepairToolPairs(result);
        return result;
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
