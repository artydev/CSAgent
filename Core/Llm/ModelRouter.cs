using System.Text;
using System.Text.Json.Nodes;

namespace CsAgent.Core.Llm;

/// <summary>Which role a model plays for one run.</summary>
public enum ModelProfile
{
    /// <summary>Coding, files, shell, mail...: the default model.</summary>
    Code,
    /// <summary>General conversation without code or files.</summary>
    Chat,
    /// <summary>The conversation contains an image.</summary>
    Vision,
    /// <summary>The user forced a model with --model.</summary>
    Explicit,
}

/// <summary>The model chosen for a run, and why (shown to the user).</summary>
public sealed record ModelChoice(string Model, ModelProfile Profile, string Reason)
{
    /// <summary>"[model: x]" for the default case, "[model: x (chat: general question)]" otherwise.</summary>
    public string Describe() =>
        Profile == ModelProfile.Code && Reason == "default" ? $"[model: {Model}]" : $"[model: {Model} ({Profile.ToString().ToLowerInvariant()}: {Reason})]";
}

/// <summary>
/// Picks the model for each user message. Deterministic and free (no extra LLM call):
/// <list type="number">
/// <item><c>--model</c> always wins.</item>
/// <item>An image in the conversation selects the vision model.</item>
/// <item>A message about code, files, a shell, mail... (or a short follow-up to a turn that used tools)
/// selects the code model, the default.</item>
/// <item>Anything else is a general conversation and selects the chat model, if the endpoint has one.</item>
/// </list>
/// When in doubt the code model is kept, so behaviour only changes for clearly general questions.
/// AOT-safe: no Regex, manual tokenizing.
/// </summary>
public static class ModelRouter
{
    /// <summary>A follow-up of at most this many words after a tool-using turn stays on the code model.</summary>
    private const int ShortFollowUpWords = 8;

    // Whole words that mark a task on code or on the workspace (compared after lower-casing and removing accents).
    private static readonly HashSet<string> Words = new(StringComparer.Ordinal)
    {
        "code", "coder", "coding", "codage", "bug", "bugs", "fix", "test", "tests", "build", "git", "api", "sql",
        "npm", "pip", "mcp", "json", "xml", "yaml", "yml", "html", "css", "regex", "http", "https", "url",
        "run", "shell", "bash", "powershell", "cmd", "terminal", "docker", "dotnet", "nuget", "csproj", "sln",
        "python", "javascript", "typescript", "java", "rust", "golang", "csharp", "sdk", "cli", "ide",
        "repo", "repos", "merge", "rebase", "diff", "patch", "stash", "lint", "linter", "log", "logs",
        "classe", "class", "variable", "variables", "script", "scripts", "serveur", "server", "endpoint",
        "fonction", "fonctions", "function", "functions", "directory", "directories", "program", "programs",
        "audio", "email", "emails", "mail", "mails", "outlook", "clipboard", "projet", "project", "skills", "skill",
        "zip", "unzip", "pdf", "csv", "xlsx", "docx", "readme", "stderr", "stdout",
    };

    // Word beginnings that mark the same (so one entry covers conjugations and plurals).
    private static readonly string[] Stems =
    {
        "refactor", "compil", "debug", "deboug", "commit", "branch", "depot",
        "implement", "execut", "install", "transcri", "programmation", "programmer", "programming", "algorithm", "develop", "deploy",
        "exception", "fichier", "dossier", "folder", "repertoire", "presse-papier", "pull-request",
        "attach", "piece-jointe", "unitaire", "dependanc", "dependenc", "librairie", "librar", "framework",
        "commande", "command", "pipeline", "base-de-donnee", "database", "stacktrace", "traceback",
    };

    // File extensions: a word such as "Program.cs" or "notes.md" is a file.
    private static readonly HashSet<string> Extensions = new(StringComparer.Ordinal)
    {
        "cs", "csproj", "sln", "py", "js", "ts", "tsx", "jsx", "json", "yaml", "yml", "xml", "html", "css", "md",
        "sh", "ps1", "bat", "go", "rs", "java", "kt", "c", "cpp", "h", "sql", "toml", "ini", "txt", "log", "zip",
        "pdf", "docx", "xlsx", "csv", "pptx", "msg", "eml", "mp3", "wav", "m4a", "ogg", "flac", "webm", "mp4",
        "png", "jpg", "jpeg", "gif", "webp", "svg", "dll", "exe", "env", "lock",
    };

    // ───────────────────────────── choice ─────────────────────────────

    /// <summary>
    /// The model for this message. <paramref name="previousTurnUsedTools"/> tells whether the turn
    /// before this one called tools (see <see cref="LastTurnUsedTools"/>).
    /// </summary>
    public static ModelChoice Choose(string prompt, bool needsVision, string? modelOverride, bool previousTurnUsedTools)
    {
        if (!string.IsNullOrWhiteSpace(modelOverride))
            return new ModelChoice(modelOverride, ModelProfile.Explicit, "--model");

        if (needsVision)
            return new ModelChoice(LlmConfig.VisionModel, ModelProfile.Vision, "image in the conversation");

        var code = LlmConfig.CodeModel;
        var chat = LlmConfig.ChatModel;
        if (!LlmConfig.AutoRoute || chat is null || chat.Equals(code, StringComparison.OrdinalIgnoreCase))
            return new ModelChoice(code, ModelProfile.Code, "default");

        var text = prompt ?? "";
        if (previousTurnUsedTools && CountWords(text) <= ShortFollowUpWords)
            return new ModelChoice(code, ModelProfile.Code, "follows a turn that used tools");

        if (FindWorkSignal(text) is { } signal)
            return new ModelChoice(code, ModelProfile.Code, signal);

        return new ModelChoice(chat, ModelProfile.Chat, "general question");
    }

    /// <summary>
    /// Like <see cref="Choose"/>, then checks the chat model against the endpoint's model list: if it is
    /// unknown, down or not a text model, the code model is used instead and the reason says so.
    /// The list is fetched only for a chat choice (one cached call); with no information the choice stands.
    /// </summary>
    public static async Task<ModelChoice> ResolveAsync(string prompt, bool needsVision, string? modelOverride,
        bool previousTurnUsedTools, string apiKey,
        Func<Task<IReadOnlyList<CatalogEntry>?>>? loadCatalog = null)
    {
        var choice = Choose(prompt, needsVision, modelOverride, previousTurnUsedTools);
        if (choice.Profile != ModelProfile.Chat) return choice;

        loadCatalog ??= () => ModelCatalog.GetCachedAsync(LlmConfig.Endpoint, apiKey);
        IReadOnlyList<CatalogEntry>? catalog;
        try { catalog = await loadCatalog(); }
        catch { catalog = null; }

        if (ModelCatalog.IsUsable(catalog, choice.Model, out var why)) return choice;
        return new ModelChoice(LlmConfig.CodeModel, ModelProfile.Code, $"chat model '{choice.Model}' {why}");
    }

    // ───────────────────────────── history ─────────────────────────────

    /// <summary>
    /// True when the turn before the current one called tools. <paramref name="msgs"/> ends with the
    /// current user message (already added); earlier messages are scanned back to the previous user message.
    /// </summary>
    public static bool LastTurnUsedTools(JsonArray msgs)
    {
        var i = msgs.Count - 1;
        if (i >= 0 && Role(msgs[i]) == "user") i--;

        for (; i >= 0; i--)
        {
            var m = msgs[i];
            var role = Role(m);
            if (role == "user") break;
            if (role == "tool") return true;
            if (m?["tool_calls"] is JsonArray calls && calls.Count > 0) return true;
        }
        return false;
    }

    private static string Role(JsonNode? m) =>
        m?["role"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    // ───────────────────────────── message analysis ─────────────────────────────

    /// <summary>
    /// What makes this message a task on code or on the workspace, or null for a general message.
    /// The text is the reason shown to the user.
    /// </summary>
    internal static string? FindWorkSignal(string text)
    {
        if (text.Contains("```") || text.Contains('`')) return "code in the message";
        if (text.Contains("[Attached", StringComparison.OrdinalIgnoreCase)) return "attached file";
        if (text.Contains("c#", StringComparison.OrdinalIgnoreCase) || text.Contains(".net", StringComparison.OrdinalIgnoreCase))
            return "mentions code";

        // Split on whitespace first, so that file names, paths and URLs stay in one piece.
        foreach (var chunk in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var c = chunk.Trim('(', ')', '[', ']', '"', '\'', ',', ';', ':', '?', '!', '.', '<', '>', '*');
            if (c.Length == 0) continue;

            if (c.Contains("://")) return "link";
            if (c.Contains('\\')) return "file path";
            if (IsUnixPath(c)) return "file path";

            var dot = c.LastIndexOf('.');
            if (dot > 0 && dot < c.Length - 1 && Extensions.Contains(Fold(c[(dot + 1)..]))) return "file name";
        }

        foreach (var word in Tokens(text))
        {
            if (Words.Contains(word)) return $"mentions '{word}'";
            foreach (var stem in Stems)
                if (word.StartsWith(stem, StringComparison.Ordinal)) return $"mentions '{word}'";
        }
        return null;
    }

    private static bool IsUnixPath(string c)
    {
        if (c.StartsWith("./") || c.StartsWith("../") || c.StartsWith("~/")) return true;

        // "src/Core/x", "/etc/hosts"; not "et/ou" or a date such as 10/10/2026.
        var slashes = 0;
        foreach (var ch in c) if (ch == '/') slashes++;
        if (slashes < 2) return false;
        foreach (var part in c.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (part.Length > 0 && char.IsLetter(part[0]) && part.Length > 1) return true;
        return false;
    }

    private static int CountWords(string text)
    {
        var n = 0;
        foreach (var _ in Tokens(text)) n++;
        return n;
    }

    /// <summary>Lower-case words made of letters, digits and "_" or "-", without accents.</summary>
    private static IEnumerable<string> Tokens(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')
            {
                sb.Append(FoldChar(char.ToLowerInvariant(ch)));
            }
            else if (sb.Length > 0)
            {
                yield return sb.ToString().Trim('-');
                sb.Clear();
            }
        }
        if (sb.Length > 0) yield return sb.ToString().Trim('-');
    }

    private static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(FoldChar(char.ToLowerInvariant(ch)));
        return sb.ToString();
    }

    /// <summary>Removes the accents of the French letters, without relying on ICU (works in invariant mode).</summary>
    private static char FoldChar(char c) => c switch
    {
        'é' or 'è' or 'ê' or 'ë' => 'e',
        'à' or 'â' or 'ä' => 'a',
        'î' or 'ï' => 'i',
        'ô' or 'ö' => 'o',
        'ù' or 'û' or 'ü' => 'u',
        'ç' => 'c',
        _ => c,
    };
}
