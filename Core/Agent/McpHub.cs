using System.Text.Json.Nodes;

namespace CsAgent.Core.Agent;

/// <summary>
/// All the MCP servers of a run behind one object: connects to each, offers their tools together and sends
/// each call to the server that owns the tool. With one server the tool names are "mcp_&lt;tool&gt;" (as before);
/// with several they are "mcp_&lt;server&gt;_&lt;tool&gt;", so two servers can have a tool of the same name.
/// A server that cannot be reached is reported and left out: the others stay usable.
/// </summary>
public sealed class McpHub : IDisposable
{
    private readonly List<(string Name, McpClient Client)> _clients = new();
    private readonly Dictionary<string, McpClient> _owner = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> _tools = new(StringComparer.Ordinal);

    public McpHub(IReadOnlyList<McpServerConfig> servers)
    {
        foreach (var s in servers)
        {
            IMcpTransport transport = s.IsStdio ? new StdioMcpTransport(s) : new HttpMcpTransport(s.Url!, s.Headers);
            var prefix = servers.Count == 1 ? McpClient.ToolPrefix : $"{McpClient.ToolPrefix}{s.Name}_";
            _clients.Add((s.Name, new McpClient(transport, prefix)));
        }
    }

    /// <summary>For tests: clients that are already built.</summary>
    internal McpHub(IEnumerable<(string Name, McpClient Client)> clients) => _clients.AddRange(clients);

    public IReadOnlyDictionary<string, JsonObject> Tools => _tools;
    public int ServerCount => _clients.Count;

    /// <summary>Servers that answered, with the number of tools each offers.</summary>
    public List<(string Name, int Tools)> Connected { get; } = new();

    /// <summary>Connects to every server. Returns the ones that failed (name, message); the others are ready.</summary>
    public async Task<List<(string Name, string Error)>> ConnectAsync(CancellationToken ct = default)
    {
        var failures = new List<(string, string)>();
        _tools.Clear(); _owner.Clear(); Connected.Clear();

        var attempts = _clients.Select(async c =>
        {
            try { await c.Client.ConnectAsync(ct); return (c.Name, c.Client, (string?)null); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return (c.Name, c.Client, ex.Message); }
        }).ToList();

        foreach (var (name, client, error) in await Task.WhenAll(attempts))
        {
            if (error is not null) { failures.Add((name, error)); continue; }
            foreach (var (exposed, def) in client.Tools)
            {
                _tools[exposed] = def;
                _owner[exposed] = client;
            }
            Connected.Add((name, client.Tools.Count));
        }
        return failures;
    }

    public JsonArray GetOpenAiToolDefinitions()
    {
        var result = new JsonArray();
        foreach (var (_, client) in _clients)
            foreach (var def in client.GetOpenAiToolDefinitions()) result.Add(def!.DeepClone());
        return result;
    }

    public bool Contains(string name) => _owner.ContainsKey(name);

    public Task<string> CallToolAsync(string name, string argumentsJson, CancellationToken ct = default) =>
        _owner.TryGetValue(name, out var client)
            ? client.CallToolAsync(name, argumentsJson, ct)
            : Task.FromResult($"Error: Unknown MCP tool '{name}'.");

    /// <summary>"3 tool(s) available" for one server; "5 tool(s) from 2 server(s) (a: 3, b: 2)" for several.</summary>
    public string Summary => ServerCount <= 1
        ? $"{_tools.Count} tool(s) available."
        : $"{_tools.Count} tool(s) from {Connected.Count} of {ServerCount} server(s) ({string.Join(", ", Connected.Select(c => $"{c.Name}: {c.Tools}"))}).";

    public void Dispose() { foreach (var (_, c) in _clients) c.Dispose(); }
}
