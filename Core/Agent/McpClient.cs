using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace CsAgent.Core.Agent;

/// <summary>
/// Minimal MCP Streamable HTTP client. It intentionally depends only on the
/// .NET BCL so CsAgent remains friendly to trimming/AOT and has no MCP SDK
/// dependency. It supports initialize, tools/list and tools/call.
/// </summary>
public sealed class McpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private string? _sessionId;
    private string? _protocolVersion;
    private int _requestId;
    // Keyed by the name the model sees ("mcp_<name>"), never by the server's own name: a server must not be
    // able to shadow a native tool (read_file, sh, ...). The server's real name stays in each tool's "name".
    private readonly Dictionary<string, JsonObject> _tools = new(StringComparer.Ordinal);

    /// <summary>Prefix of every MCP tool name seen by the model.</summary>
    public const string ToolPrefix = "mcp_";
    private const int MaxToolNameLength = 64;          // limit of the OpenAI function-calling name
    private const int MaxDescriptionLength = 1000;     // a server's text goes into the prompt: keep it bounded

    public McpClient(string endpoint)
    {
        _endpoint = new Uri(endpoint, UriKind.Absolute);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public IReadOnlyDictionary<string, JsonObject> Tools => _tools;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var initialize = await SendAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "CsAgent",
                ["version"] = Program.Version
            }
        }, ct, includeProtocolVersion: false);

        var negotiated = initialize?["protocolVersion"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(negotiated))
            throw new InvalidOperationException("MCP server returned no protocolVersion during initialize.");
        _protocolVersion = negotiated;

        await SendNotificationAsync("notifications/initialized", new JsonObject(), ct);

        var list = await SendAsync("tools/list", new JsonObject(), ct);
        var tools = list?["tools"]?.AsArray();
        if (tools is null)
            throw new InvalidOperationException("MCP server returned no tools list.");

        _tools.Clear();
        foreach (var item in tools)
        {
            if (item is not JsonObject tool) continue;
            var name = tool["name"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(name))
                _tools[UniqueExposedName(name)] = tool;
        }
    }

    /// <summary>
    /// The name the model sees for a server tool: "mcp_" + the server's name, with every character that
    /// function-calling APIs refuse replaced by '_', cut to 64 characters, and made unique.
    /// </summary>
    public static string ExposedName(string serverName)
    {
        var clean = new StringBuilder(ToolPrefix, MaxToolNameLength);
        foreach (var c in serverName)
        {
            if (clean.Length >= MaxToolNameLength) break;
            clean.Append(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-' ? c : '_');
        }
        return clean.ToString();
    }

    private string UniqueExposedName(string serverName)
    {
        var name = ExposedName(serverName);
        if (!_tools.ContainsKey(name)) return name;

        // Two server names can collapse to the same text ("a.b" and "a_b"): number the later ones.
        for (var i = 2; ; i++)
        {
            var suffix = "_" + i;
            var candidate = name[..Math.Min(name.Length, MaxToolNameLength - suffix.Length)] + suffix;
            if (!_tools.ContainsKey(candidate)) return candidate;
        }
    }

    public JsonArray GetOpenAiToolDefinitions()
    {
        var result = new JsonArray();
        foreach (var (exposed, tool) in _tools.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            var parameters = tool["inputSchema"]?.DeepClone()
                ?? new JsonObject { ["type"] = "object" };

            var description = tool["description"]?.GetValue<string>() ?? "MCP tool.";
            if (description.Length > MaxDescriptionLength) description = description[..MaxDescriptionLength] + "…";

            result.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = exposed,
                    ["description"] = "(external MCP server tool) " + description,
                    ["parameters"] = parameters
                }
            });
        }
        return result;
    }

    public bool Contains(string name) => _tools.ContainsKey(name);

    /// <summary>Calls a tool by the name the model uses (the "mcp_…" name).</summary>
    public async Task<string> CallToolAsync(string name, string argumentsJson, CancellationToken ct = default)
    {
        if (!_tools.TryGetValue(name, out var definition))
            return $"Error: Unknown MCP tool '{name}'.";
        var serverName = definition["name"]!.GetValue<string>();

        JsonNode arguments;
        try { arguments = JsonNode.Parse(argumentsJson) ?? new JsonObject(); }
        catch (Exception ex) { return $"Error: invalid arguments for MCP tool '{name}': {ex.Message}"; }

        try
        {
            var result = await SendAsync("tools/call", new JsonObject
            {
                ["name"] = serverName,
                ["arguments"] = arguments
            }, ct);

            if (result is null)
                return "Error: MCP server returned an empty tool result.";

            var parts = new List<string>();
            if (result["content"] is JsonArray content)
            {
                foreach (var item in content)
                {
                    if (item is JsonObject block && block["type"]?.GetValue<string>() == "text")
                        parts.Add(block["text"]?.GetValue<string>() ?? string.Empty);
                    else if (item is not null)
                        parts.Add(item.ToJsonString());
                }
            }

            var output = string.Join("\n", parts.Where(x => !string.IsNullOrEmpty(x)));
            if (result["isError"]?.GetValue<bool>() == true)
                return $"Error: MCP tool '{name}' failed: {output}";

            return string.IsNullOrEmpty(output) ? result.ToJsonString() : output;
        }
        catch (Exception ex)
        {
            return $"Error: MCP tool '{name}' failed — {ex.Message}";
        }
    }

    private async Task<JsonObject?> SendAsync(
        string method,
        JsonObject parameters,
        CancellationToken ct,
        bool includeProtocolVersion = true)
    {
        var id = Interlocked.Increment(ref _requestId);
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        AddMcpHeaders(request, includeProtocolVersion);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);

        if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessionValues))
            _sessionId = sessionValues.FirstOrDefault();

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"MCP HTTP {(int)response.StatusCode}: {raw}");

        var json = ExtractJsonResponse(raw, response.Content.Headers.ContentType?.MediaType);
        if (json is null)
            throw new InvalidDataException("MCP server returned an empty response.");

        if (json["error"] is JsonObject error)
            throw new InvalidOperationException($"MCP {method} failed: {error.ToJsonString()}");

        return json["result"] as JsonObject;
    }

    private async Task SendNotificationAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        AddMcpHeaders(request);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var raw = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"MCP notification HTTP {(int)response.StatusCode}: {raw}");
        }
    }

    private void AddMcpHeaders(HttpRequestMessage request, bool includeProtocolVersion = true)
    {
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrWhiteSpace(_sessionId))
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        if (includeProtocolVersion && !string.IsNullOrWhiteSpace(_protocolVersion))
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocolVersion);
    }

    private static JsonObject? ExtractJsonResponse(string raw, string? mediaType)
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
