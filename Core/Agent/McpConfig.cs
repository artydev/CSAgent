using System.Text;
using System.Text.Json.Nodes;
using CsAgent.Shared;

namespace CsAgent.Core.Agent;

/// <summary>One MCP server: reached over HTTP (<see cref="Url"/>, with optional <see cref="Headers"/>) or started as a local program (<see cref="Command"/>).</summary>
public sealed record McpServerConfig(
    string Name,
    string? Url = null,
    IReadOnlyDictionary<string, string>? HeaderValues = null,
    string? Command = null,
    IReadOnlyList<string>? ArgList = null,
    IReadOnlyDictionary<string, string>? EnvValues = null,
    string? WorkingDirectory = null)
{
    public IReadOnlyDictionary<string, string> Headers => HeaderValues ?? new Dictionary<string, string>();
    public IReadOnlyList<string> Args => ArgList ?? Array.Empty<string>();
    public IReadOnlyDictionary<string, string> Env => EnvValues ?? new Dictionary<string, string>();
    public bool IsStdio => !string.IsNullOrWhiteSpace(Command);
}

/// <summary>A mistake in the MCP settings: reported to the user, who fixes the command line or the file.</summary>
public sealed class McpConfigException : Exception
{
    public McpConfigException(string message) : base(message) { }
}

/// <summary>
/// The MCP servers CsAgent connects to. They come from the command line (<c>--mcp</c>, <c>--mcp-stdio</c>,
/// <c>--mcp-header</c>) and from a file given with <c>--mcp-config</c>. Nothing is read from the working folder on its own:
/// a stdio server is a program that runs with the user's rights, so only what the user names is started.
/// Set once at start-up, like <see cref="ContextBudget"/>.
/// </summary>
public static class McpSettings
{
    public static IReadOnlyList<McpServerConfig> Servers { get; private set; } = Array.Empty<McpServerConfig>();

    public static void Configure(IReadOnlyList<McpServerConfig> servers) => Servers = servers;
    internal static void Reset() => Servers = Array.Empty<McpServerConfig>();

    /// <summary>The servers to connect to: the configured ones, plus <paramref name="mcpUrl"/> when it is not one of them.</summary>
    public static IReadOnlyList<McpServerConfig> Resolve(string? mcpUrl)
    {
        if (string.IsNullOrWhiteSpace(mcpUrl)) return Servers;
        if (Servers.Any(s => string.Equals(s.Url, mcpUrl, StringComparison.OrdinalIgnoreCase))) return Servers;
        var list = new List<McpServerConfig>(Servers) { new(UniqueName(NameFromUrl(mcpUrl), Servers.Select(s => s.Name)), Url: mcpUrl) };
        return list;
    }

    /// <summary>A line for the start-up banner: names and kinds, never headers or environment values.</summary>
    public static string Describe(string? mcpUrl)
    {
        var servers = Resolve(mcpUrl);
        if (servers.Count == 0) return "";
        if (servers.Count == 1 && servers[0].Url is { } only) return only;
        return string.Join(", ", servers.Select(s => s.IsStdio ? $"{s.Name} (stdio: {s.Command})" : $"{s.Name} ({s.Url})"));
    }

    /// <summary>Builds the server list from the command line and the optional configuration file.</summary>
    public static List<McpServerConfig> FromArguments(AgentArguments a)
    {
        var servers = new List<McpServerConfig>();
        var names = new List<string>();

        if (!string.IsNullOrWhiteSpace(a.McpConfig))
            foreach (var s in ParseFile(a.McpConfig!)) Add(s);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in a.McpHeaders ?? Array.Empty<string>())
        {
            var i = h.IndexOf(':');
            if (i <= 0) throw new McpConfigException($"--mcp-header '{Redact(h)}': expected \"Name: value\".");
            headers[h[..i].Trim()] = ExpandEnv(h[(i + 1)..].Trim(), "--mcp-header");
        }

        var urls = new List<string>(a.McpUrls ?? Array.Empty<string>());
        if (urls.Count == 0 && !string.IsNullOrWhiteSpace(a.McpUrl)) urls.Add(a.McpUrl!);   // CSAGENT_MCP_URL
        foreach (var url in urls)
        {
            ValidateUrl(url, "--mcp");
            Add(new McpServerConfig(UniqueName(NameFromUrl(url), names), Url: url, HeaderValues: headers.Count > 0 ? new Dictionary<string, string>(headers) : null));
        }
        if (headers.Count > 0 && urls.Count == 0)
            throw new McpConfigException("--mcp-header needs an --mcp <url> to send the headers to (for a server in a file, put its headers in the file).");

        foreach (var line in a.McpStdio ?? Array.Empty<string>())
        {
            var parts = SplitCommandLine(line);
            if (parts.Count == 0) throw new McpConfigException("--mcp-stdio: the command is empty.");
            var args = parts.Skip(1).ToList();
            Add(new McpServerConfig(UniqueName(NameFromCommand(parts[0], args), names), Command: parts[0], ArgList: args));
        }
        return servers;

        void Add(McpServerConfig s)
        {
            // Names must stay unique: they become part of the tool names when several servers are connected.
            var unique = UniqueName(s.Name, names);
            names.Add(unique);
            servers.Add(unique == s.Name ? s : s with { Name = unique });
        }
    }

    /// <summary>Reads a configuration file: <c>{"mcpServers": {"name": {"url": "...", "headers": {...}} | {"command": "...", "args": [...], "env": {...}}}}</c>.</summary>
    public static List<McpServerConfig> ParseFile(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) { throw new McpConfigException($"--mcp-config '{path}': {ex.Message}"); }
        return Parse(text, path);
    }

    public static List<McpServerConfig> Parse(string json, string source = "mcp config")
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json, documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (Exception ex) { throw new McpConfigException($"{source}: not valid JSON ({ex.Message})"); }

        var section = (root as JsonObject)?["mcpServers"] as JsonObject
            ?? throw new McpConfigException($"{source}: expected an object \"mcpServers\".");

        var list = new List<McpServerConfig>();
        foreach (var (name, node) in section)
        {
            if (node is not JsonObject o) throw new McpConfigException($"{source}: server '{name}' must be an object.");
            var where = $"{source}, server '{name}'";
            var url = o["url"]?.GetValue<string>();
            var command = o["command"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(url) == !string.IsNullOrWhiteSpace(command))
                throw new McpConfigException($"{where}: give either \"url\" or \"command\", not both and not neither.");

            if (!string.IsNullOrWhiteSpace(url))
            {
                ValidateUrl(url!, where);
                list.Add(new McpServerConfig(name, Url: url, HeaderValues: StringMap(o["headers"], where, "headers")));
            }
            else
            {
                var args = (o["args"] as JsonArray)?.Select(x => ExpandEnv(x?.GetValue<string>() ?? "", where)).ToList() ?? new List<string>();
                list.Add(new McpServerConfig(name, Command: command, ArgList: args,
                    EnvValues: StringMap(o["env"], where, "env"), WorkingDirectory: o["cwd"]?.GetValue<string>()));
            }
        }
        return list;
    }

    static Dictionary<string, string>? StringMap(JsonNode? node, string where, string what)
    {
        if (node is null) return null;
        if (node is not JsonObject obj) throw new McpConfigException($"{where}: \"{what}\" must be an object.");
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in obj) map[k] = ExpandEnv(v?.GetValue<string>() ?? "", where);
        return map;
    }

    static void ValidateUrl(string url, string where)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            throw new McpConfigException($"{where}: '{url}' is not an http(s) URL. For a program, use --mcp-stdio \"<command>\".");
    }

    /// <summary>Replaces <c>${NAME}</c> by the environment variable NAME, so that a token never has to be written in a file or a script.</summary>
    public static string ExpandEnv(string value, string where)
    {
        if (!value.Contains("${", StringComparison.Ordinal)) return value;
        var sb = new StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '$' && i + 1 < value.Length && value[i + 1] == '{')
            {
                var end = value.IndexOf('}', i + 2);
                if (end > 0)
                {
                    var name = value[(i + 2)..end];
                    var v = Environment.GetEnvironmentVariable(name);
                    if (string.IsNullOrEmpty(v)) throw new McpConfigException($"{where}: the environment variable {name} is not set.");
                    sb.Append(v);
                    i = end;
                    continue;
                }
            }
            sb.Append(value[i]);
        }
        return sb.ToString();
    }

    /// <summary>Splits a command line into words; double or single quotes group words that contain spaces.</summary>
    public static List<string> SplitCommandLine(string line)
    {
        var parts = new List<string>();
        var cur = new StringBuilder();
        char quote = '\0';
        bool started = false;
        foreach (var c in line)
        {
            if (quote != '\0') { if (c == quote) quote = '\0'; else cur.Append(c); }
            else if (c is '"' or '\'') { quote = c; started = true; }
            else if (char.IsWhiteSpace(c)) { if (started || cur.Length > 0) { parts.Add(cur.ToString()); cur.Clear(); started = false; } }
            else { cur.Append(c); started = true; }
        }
        if (started || cur.Length > 0) parts.Add(cur.ToString());
        return parts;
    }

    static string Redact(string header) => header.Length > 12 ? header[..12] + "…" : header;

    /// <summary>A short name made of letters, digits, '_' and '-': it is used in the tool names.</summary>
    internal static string CleanName(string raw)
    {
        var sb = new StringBuilder();
        foreach (var c in raw)
            sb.Append(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' ? c : '_');
        var s = sb.ToString().Trim('_');
        while (s.Contains("__", StringComparison.Ordinal)) s = s.Replace("__", "_", StringComparison.Ordinal);
        if (s.Length > 24) s = s[..24].TrimEnd('_');
        return s.Length == 0 ? "server" : s;
    }

    static string NameFromUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? CleanName(u.Host) : "server";

    static readonly HashSet<string> Launchers = new(StringComparer.OrdinalIgnoreCase)
        { "npx", "uvx", "uv", "node", "bunx", "bun", "deno", "pnpm", "yarn", "python", "python3", "py", "dotnet", "java", "docker", "cmd" };

    static string NameFromCommand(string command, IReadOnlyList<string> args)
    {
        var program = Path.GetFileNameWithoutExtension(command.Replace('\\', '/'));
        // "npx -y @modelcontextprotocol/server-filesystem ." is the server-filesystem, not npx.
        if (Launchers.Contains(program))
            foreach (var a in args)
            {
                if (a.StartsWith('-') || !a.Any(char.IsLetter)) continue;
                var last = a.Replace('\\', '/').Split('/').Last();
                if (last.Length > 0) return CleanName(Path.GetFileNameWithoutExtension(last) is { Length: > 0 } stem ? stem : last);
            }
        return CleanName(program);
    }

    internal static string UniqueName(string name, IEnumerable<string> taken)
    {
        var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        var clean = CleanName(name);
        if (!set.Contains(clean)) return clean;
        for (int i = 2; ; i++) { var c = clean + "_" + i; if (!set.Contains(c)) return c; }
    }
}
