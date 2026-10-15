using System.Text;
using System.Text.Json.Nodes;

namespace CsAgent.Core.Agent;

/// <summary>
/// Minimal MCP client for ONE server, over any <see cref="IMcpTransport"/> (HTTP or standard input/output).
/// It intentionally depends only on the .NET BCL so CsAgent remains friendly to trimming/AOT and has no MCP SDK
/// dependency. It supports initialize, tools/list and tools/call. <see cref="McpHub"/> joins several of them.
/// </summary>
public sealed class McpClient : IDisposable
{
    private readonly IMcpTransport _transport;
    private readonly string _prefix;
    private int _requestId;
    // Keyed by the name the model sees ("mcp_<name>"), never by the server's own name: a server must not be
    // able to shadow a native tool (read_file, sh, ...). The server's real name stays in each tool's "name".
    private readonly Dictionary<string, JsonObject> _tools = new(StringComparer.Ordinal);

    /// <summary>Prefix of every MCP tool name seen by the model.</summary>
    public const string ToolPrefix = "mcp_";
    private const int MaxToolNameLength = 64;          // limit of the OpenAI function-calling name
    private const int MaxDescriptionLength = 1000;     // a server's text goes into the prompt: keep it bounded

    /// <summary>A server reached over HTTP, without extra headers.</summary>
    public McpClient(string endpoint) : this(new HttpMcpTransport(endpoint)) { }

    /// <param name="transport">How to reach the server.</param>
    /// <param name="prefix">Start of the tool names: "mcp_", or "mcp_&lt;server&gt;_" when several servers are connected.</param>
    public McpClient(IMcpTransport transport, string prefix = ToolPrefix)
    {
        _transport = transport;
        _prefix = prefix;
    }

    public string Describe => _transport.Describe;

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
        _transport.SetProtocolVersion(negotiated);

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
    public static string ExposedName(string serverName) => ExposedName(ToolPrefix, serverName);

    public static string ExposedName(string prefix, string serverName)
    {
        var clean = new StringBuilder(prefix.Length > 40 ? prefix[..40] : prefix, MaxToolNameLength);
        foreach (var c in serverName)
        {
            if (clean.Length >= MaxToolNameLength) break;
            clean.Append(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-' ? c : '_');
        }
        return clean.ToString();
    }

    private string UniqueExposedName(string serverName)
    {
        var name = ExposedName(_prefix, serverName);
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

        var json = await _transport.RequestAsync(body, ct, includeProtocolVersion);

        if (json["error"] is JsonObject error)
            throw new InvalidOperationException($"MCP {method} failed: {error.ToJsonString()}");

        return json["result"] as JsonObject;
    }

    private Task SendNotificationAsync(string method, JsonObject parameters, CancellationToken ct) =>
        _transport.NotifyAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters
        }, ct);

    public void Dispose() => _transport.Dispose();
}
