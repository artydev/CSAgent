using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace CsAgent.Core.Agent;

/// <summary>How a JSON-RPC message reaches one MCP server: over HTTP, or over a local program's standard input and output.</summary>
public interface IMcpTransport : IDisposable
{
    /// <summary>Where the server is, for messages: a URL without its query, or the program's name. Never a header or an environment value.</summary>
    string Describe { get; }

    /// <summary>Sends a request and returns the server's whole JSON-RPC answer (<c>result</c> or <c>error</c>).</summary>
    Task<JsonObject> RequestAsync(JsonObject request, CancellationToken ct, bool includeProtocolVersion = true);

    /// <summary>Sends a notification, which has no answer.</summary>
    Task NotifyAsync(JsonObject notification, CancellationToken ct);

    /// <summary>The protocol version the server agreed to (HTTP sends it in a header; stdio ignores it).</summary>
    void SetProtocolVersion(string version);
}

/// <summary>MCP over Streamable HTTP. Extra headers (for example Authorization) are sent with every request.</summary>
public sealed class HttpMcpTransport : IMcpTransport
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private string? _sessionId;
    private string? _protocolVersion;

    public HttpMcpTransport(string endpoint, IReadOnlyDictionary<string, string>? headers = null)
    {
        _endpoint = new Uri(endpoint, UriKind.Absolute);
        _headers = headers ?? new Dictionary<string, string>();
        _http = CsAgent.Shared.HttpClients.Create(TimeSpan.FromSeconds(60));
    }

    public string Describe => _endpoint.GetLeftPart(UriPartial.Path);

    public void SetProtocolVersion(string version) => _protocolVersion = version;

    public async Task<JsonObject> RequestAsync(JsonObject request, CancellationToken ct, bool includeProtocolVersion = true)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json")
        };
        AddHeaders(message, includeProtocolVersion);

        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);

        if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessionValues))
            _sessionId = sessionValues.FirstOrDefault();

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"MCP HTTP {(int)response.StatusCode}: {raw}");

        var json = ExtractJsonResponse(raw, response.Content.Headers.ContentType?.MediaType);
        return json ?? throw new InvalidDataException("MCP server returned an empty response.");
    }

    public async Task NotifyAsync(JsonObject notification, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(notification.ToJsonString(), Encoding.UTF8, "application/json")
        };
        AddHeaders(message);
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var raw = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"MCP notification HTTP {(int)response.StatusCode}: {raw}");
        }
    }

    private void AddHeaders(HttpRequestMessage request, bool includeProtocolVersion = true)
    {
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        foreach (var (name, value) in _headers)
            request.Headers.TryAddWithoutValidation(name, value);
        if (!string.IsNullOrWhiteSpace(_sessionId))
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        if (includeProtocolVersion && !string.IsNullOrWhiteSpace(_protocolVersion))
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocolVersion);
    }

    internal static JsonObject? ExtractJsonResponse(string raw, string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (mediaType?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
        {
            foreach (var line in raw.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                var payload = trimmed[5..].Trim();
                if (string.IsNullOrWhiteSpace(payload) || payload == "[DONE]") continue;
                var node = JsonNode.Parse(payload);
                if (node is JsonObject obj) return obj;
            }
            return null;
        }

        return JsonNode.Parse(raw) as JsonObject;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// MCP over standard input and output: CsAgent starts the server as a child program and exchanges one JSON message
/// per line with it. Requests are answered one at a time. The child is stopped when the transport is disposed.
/// </summary>
public sealed class StdioMcpTransport : IMcpTransport
{
    private const int StderrLines = 20;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private readonly McpServerConfig _config;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<string> _stderr = new();
    private Process? _process;

    public StdioMcpTransport(McpServerConfig config)
    {
        _config = config;
    }

    public string Describe => _config.Command ?? _config.Name;

    public void SetProtocolVersion(string version) { }

    private Process Start()
    {
        if (_process is { HasExited: false }) return _process;

        var command = _config.Command ?? throw new InvalidOperationException("MCP server has no command.");
        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrWhiteSpace(_config.WorkingDirectory)) psi.WorkingDirectory = _config.WorkingDirectory;

        // On Windows, npx, npm and similar are .cmd scripts, which only the command interpreter can start.
        if (OperatingSystem.IsWindows() && string.IsNullOrEmpty(Path.GetExtension(command)))
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
        }
        else psi.FileName = command;
        foreach (var a in _config.Args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in _config.Env) psi.Environment[k] = v;

        Process p;
        try { p = Process.Start(psi) ?? throw new InvalidOperationException("the program did not start"); }
        catch (Exception ex) { throw new InvalidOperationException($"cannot start MCP server '{_config.Name}' ({command}): {ex.Message}"); }

        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_stderr) { _stderr.Enqueue(e.Data); while (_stderr.Count > StderrLines) _stderr.Dequeue(); }
        };
        p.BeginErrorReadLine();
        _process = p;
        return p;
    }

    private string StderrTail()
    {
        lock (_stderr)
        {
            var text = string.Join(" | ", _stderr.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(5));
            return text.Length == 0 ? "" : " Server output: " + (text.Length > 600 ? text[..600] + "…" : text);
        }
    }

    public async Task NotifyAsync(JsonObject notification, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var p = Start();
            await p.StandardInput.WriteLineAsync(notification.ToJsonString().AsMemory(), ct);
            await p.StandardInput.FlushAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<JsonObject> RequestAsync(JsonObject request, CancellationToken ct, bool includeProtocolVersion = true)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var p = Start();
            var id = request["id"]?.ToJsonString();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);

            try
            {
                await p.StandardInput.WriteLineAsync(request.ToJsonString().AsMemory(), timeout.Token);
                await p.StandardInput.FlushAsync(timeout.Token);

                while (true)
                {
                    var line = await p.StandardOutput.ReadLineAsync(timeout.Token);
                    if (line is null)
                        throw new InvalidDataException($"MCP server '{_config.Name}' closed its output{(p.HasExited ? $" (exit code {p.ExitCode})" : "")}.{StderrTail()}");
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    JsonObject? message;
                    try { message = JsonNode.Parse(line) as JsonObject; }
                    catch { continue; }                         // a log line on standard output: not a message
                    if (message is null) continue;

                    var method = message["method"]?.GetValue<string>();
                    var messageId = message["id"]?.ToJsonString();
                    if (method is not null)
                    {
                        // The server asks something of us (ping, roots, sampling...): CsAgent offers none of it.
                        if (messageId is not null)
                        {
                            var reply = new JsonObject
                            {
                                ["jsonrpc"] = "2.0",
                                ["id"] = JsonNode.Parse(messageId),
                                ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" }
                            };
                            await p.StandardInput.WriteLineAsync(reply.ToJsonString().AsMemory(), timeout.Token);
                            await p.StandardInput.FlushAsync(timeout.Token);
                        }
                        continue;                               // a notification: nothing to do
                    }
                    if (messageId == id) return message;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"MCP server '{_config.Name}' did not answer within {(int)RequestTimeout.TotalSeconds} s.{StderrTail()}");
            }
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        var p = _process;
        _process = null;
        if (p is null) return;
        try { p.StandardInput.Close(); } catch { }
        try { if (!p.WaitForExit(500)) p.Kill(entireProcessTree: true); } catch { }
        p.Dispose();
    }
}
