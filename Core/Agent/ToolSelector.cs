using System.Text.Json.Nodes;

namespace CsAgent.Core.Agent;

/// <summary>
/// Which tools are described to the model at each step. The definitions of all the tools weigh about
/// 20,000 characters and are sent again at every step, whether the request needs them or not. The tools are
/// split into groups: the <c>core</c> group (files, search, shell) is always sent, the others only when the
/// request, or what the conversation has already used, calls for them. A tool the model needs anyway can be
/// switched on with <c>enable_tools</c>; <c>--all-tools</c> sends everything, as before 0.9.8.
/// Set once at start-up from the command line, like <see cref="ContextBudget"/>.
/// </summary>
public static class ToolSelector
{
    public const string Core = "core";
    public const string MetaToolName = "enable_tools";

    /// <summary>MCP tools are all sent when their definitions take up to this many characters.</summary>
    public const int McpAlwaysChars = 4_000;

    public static bool Enabled { get; private set; } = true;
    public static void Configure(bool allTools) => Enabled = !allTools;
    internal static void Reset() => Enabled = true;

    /// <summary>The optional groups, with the tools in each. Everything not listed is in <see cref="Core"/>.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Groups = new Dictionary<string, string[]>
    {
        ["git"] = new[] { "git_status", "git_diff", "git_log", "git_branch", "git_commit" },
        ["web"] = new[] { "web_search", "fetch_url", "http_request", "download_file" },
        ["terminal"] = new[] { "run_terminal", "close_terminal" },
        ["archive"] = new[] { "zip", "unzip" },
        ["models"] = new[] { "switch_model", "list_models" },
        ["mail"] = new[] { "read_msg", "save_attachment", "group_msg_by_sender", "send_email" },
        ["calendar"] = new[] { "read_calendar", "create_appointment" },
        ["audio"] = new[] { "transcribe_audio" },
        ["clipboard"] = new[] { "read_clipboard", "write_clipboard" },
        ["office"] = new[] { "excel_command", "close_excel" },
    };

    /// <summary>Words (lower case) that switch a group on when they appear in the request.</summary>
    static readonly (string Group, string[] Words)[] Triggers =
    {
        ("git", new[] { "git", "commit", "branch", "branche", "diff", "merge", "rebase", "push", "pull request", "repository", "dépôt", "depot", "changelog", "release note", "note de version", "note de release" }),
        ("web", new[] { "http", "www.", "url", "web", "internet", "site", "recherche", "cherche", "search", "google", "api ", "endpoint", "news", "veille", "actualit", "latest", "download", "télécharg", "telecharg", "curl", "fetch", "online", "en ligne" }),
        ("terminal", new[] { "terminal", "repl", "interactive", "interactif", "dev server", "serveur de dev", "background", "arrière-plan", "arriere-plan", "ssh", "psql", "debugger", "débogu" }),
        ("archive", new[] { "zip", "archive", "compress", "extract", "extrai", ".tar", ".gz", ".7z", "décompress" }),
        ("models", new[] { "model", "modèle", "modele", "llm", "openweight", "deepseek", "gemma" }),
        ("mail", new[] { "mail", "courriel", "outlook", ".msg", "pièce jointe", "piece jointe", "attachment", "sender", "expéditeur", "expediteur", "inbox", "boîte de réception", "boite de reception", "stakeholder" }),
        ("calendar", new[] { "agenda", "calendrier", "calendar", "rendez-vous", "rendez vous", "rdv", "appointment", "meeting", "invitation", "planifi", "schedule", "disponib", "availab", "créneau", "creneau", "free slot", "ma semaine", "my week" }),
        ("audio", new[] { "audio", "transcri", "recording", "enregistrement", "mp3", "wav", "m4a", ".ogg", "flac", "whisper", "speech", "voice", "voix", "réunion", "reunion", "dictée", "dictee", "podcast", "vidéo", "video", "mp4", "webm", "entretien", "interview" }),
        ("clipboard", new[] { "clipboard", "presse-papier", "presse papier", "j'ai copié", "j'ai copie", "viens de copier", "just copied", "i copied", "what i copied", "paste", "colle ", "copié", "copied" }),
        ("office", new[] { "excel", "xlsx", ".xls", "spreadsheet", "tableur", "classeur", "workbook", "feuille de calcul", "pivot" }),
    };

    static readonly Dictionary<string, string> GroupOf = BuildGroupOf();

    static Dictionary<string, string> BuildGroupOf()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (group, tools) in Groups) foreach (var t in tools) d[t] = group;
        return d;
    }

    /// <summary>The group of a native tool; a tool that is in no group (including any future one) is in core.</summary>
    public static string GroupFor(string toolName) => GroupOf.TryGetValue(toolName, out var g) ? g : Core;

    /// <summary>The optional groups a request calls for: by its words, and by the tools the conversation has used.</summary>
    public static HashSet<string> Select(string? requestText, IEnumerable<string>? usedTools)
    {
        var groups = new HashSet<string>(StringComparer.Ordinal);
        var text = (requestText ?? "").ToLowerInvariant();
        foreach (var (group, words) in Triggers)
            if (words.Any(w => text.Contains(w, StringComparison.Ordinal))) groups.Add(group);
        if (usedTools is not null)
            foreach (var t in usedTools)
                if (GroupOf.TryGetValue(t, out var g)) groups.Add(g);
        return groups;
    }

    /// <summary>MCP tools are a group of their own: all sent when few, else when the request names one of them.</summary>
    public static bool McpWanted(JsonArray mcpTools, string? requestText, bool usedBefore)
    {
        if (mcpTools.Count == 0) return false;
        if (usedBefore || mcpTools.Sum(t => t?.ToJsonString().Length ?? 0) <= McpAlwaysChars) return true;
        var text = (requestText ?? "").ToLowerInvariant();
        if (text.Contains("mcp", StringComparison.Ordinal)) return true;
        foreach (var t in mcpTools)
        {
            var name = t?["function"]?["name"]?.GetValue<string>() ?? "";
            foreach (var part in name.Split('_', '-', '.'))
                if (part.Length >= 4 && part != "mcp" && text.Contains(part.ToLowerInvariant(), StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// The definitions to send: core, the enabled groups, the MCP tools when wanted, and the small
    /// <c>enable_tools</c> tool while some group is still off. Nothing is filtered when <paramref name="all"/> is true.
    /// </summary>
    public static JsonArray Build(JsonArray native, JsonArray? mcp, ISet<string> enabledGroups, bool mcpOn, bool all)
    {
        var result = new JsonArray();
        foreach (var def in native)
        {
            var name = def?["function"]?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var g = GroupFor(name);
            if (all || g == Core || enabledGroups.Contains(g)) result.Add(def!.DeepClone());
        }
        if (mcp is not null && (all || mcpOn))
            foreach (var def in mcp) if (def is not null) result.Add(def.DeepClone());
        if (!all && Groups.Keys.Any(g => !enabledGroups.Contains(g)) || !all && mcp is { Count: > 0 } && !mcpOn)
            result.Add(MetaTool(enabledGroups, mcp is { Count: > 0 } && !mcpOn));
        return result;
    }

    /// <summary>The <c>enable_tools</c> definition: lists only the groups that are still off.</summary>
    public static JsonObject MetaTool(ISet<string> enabledGroups, bool withMcp)
    {
        var off = Groups.Where(kv => !enabledGroups.Contains(kv.Key)).ToList();
        var names = off.Select(kv => kv.Key).ToList();
        var lines = off.Select(kv => $"{kv.Key} ({string.Join(", ", kv.Value)})").ToList();
        if (withMcp) { names.Add("mcp"); lines.Add("mcp (the tools of the connected MCP server)"); }
        var group = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray(names.Select(n => (JsonNode)n).ToArray()),
            ["description"] = "Group to switch on."
        };
        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = MetaToolName,
                ["description"] = "Some tools are not listed to save space. If you need one, switch its group on with this tool, then call it in your next step. Groups off: " + string.Join("; ", lines) + ".",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["group"] = group },
                    ["required"] = new JsonArray("group")
                }
            }
        };
    }

    /// <summary>Runs <c>enable_tools</c>: adds the group and tells the model what it can use now.</summary>
    public static string Enable(string? group, ISet<string> enabledGroups, ref bool mcpOn, bool hasMcp)
    {
        group = group?.Trim().ToLowerInvariant();
        if (group == "mcp" && hasMcp) { mcpOn = true; return "OK: the MCP server's tools are now available; call them in your next step."; }
        if (group is not null && Groups.TryGetValue(group, out var tools))
        {
            enabledGroups.Add(group);
            return $"OK: '{group}' tools are now available: {string.Join(", ", tools)}. Call them in your next step.";
        }
        var known = string.Join(", ", Groups.Keys.Concat(hasMcp ? new[] { "mcp" } : Array.Empty<string>()));
        return $"Error: enable_tools - unknown group '{group}'. Groups: {known}.";
    }
}
