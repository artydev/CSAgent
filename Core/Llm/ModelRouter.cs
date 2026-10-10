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

/// <summary>What the turn before the current message did.</summary>
public enum TurnKind
{
    /// <summary>No tool was called (plain conversation, or the first message).</summary>
    Plain,
    /// <summary>Only web tools were called (web_search, fetch_url): a search.</summary>
    Web,
    /// <summary>Any other tool was called: work on files, shell, git, mail...</summary>
    Work,
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
/// <item>A message about code, files, a shell, mail... selects the code model, the default.</item>
/// <item>Searches (web, news, a link to read) and general conversation select the chat model, if the
/// endpoint has one. An explicit "search the web / sur internet" or "news" wins over code words.</item>
/// <item>A short acknowledgement ("yes, go ahead") continues the previous turn: the chat model after a
/// web search, the code model after any other tool use.</item>
/// </list>
/// AOT-safe: no Regex, manual tokenizing.
/// </summary>
public static class ModelRouter
{
    /// <summary>An acknowledgement of at most this many words after a tool-using turn stays on the code model.</summary>
    private const int ShortFollowUpWords = 8;

    // First words of a reply that continues the work in progress ("yes, go ahead", "ok continue with the second one").
    // A short message that starts any other way is a new request, however few words it has.
    private static readonly HashSet<string> FollowUpStarters = new(StringComparer.Ordinal)
    {
        "oui", "non", "ok", "okay", "yes", "no", "yep", "yeah", "sure", "go", "continue", "continues", "continuer",
        "vas-y", "vasy", "allez", "parfait", "merci", "thanks", "thank", "fais", "do", "proceed", "retry",
        "reessaie", "reessayer", "encore", "again", "accord",
    };

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
        "launch", "lance", "lancer", "lancez", "close", "ferme", "fermer", "kill", "delete", "supprime", "supprimer",
        "efface", "effacer", "rename", "renomme", "renommer", "move", "deplace", "deplacer", "copie", "copier", "copy",
        "application", "applications", "app", "apps", "fenetre", "window", "windows", "process", "processus",
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

    // Actions on the user's machine: opening or downloading something, and the names of browsers and apps.
    // They win over a web search ("open the news site in Edge" is an action, not a search).
    private static readonly HashSet<string> ActionWords = new(StringComparer.Ordinal)
    {
        "open", "ouvre", "ouvres", "ouvrir", "ouvrez", "download", "telecharge", "telecharger", "telechargez",
        "edge", "chrome", "firefox", "safari", "browser", "navigateur", "notepad", "bloc-notes", "excel",
        "powerpoint", "vscode", "explorateur", "finder",
    };

    // Words that mean "search the web / read the news" on their own (the word "web" alone is not enough:
    // it is also "web UI"). "actualit*" is matched as a stem.
    private static readonly HashSet<string> WebWords = new(StringComparer.Ordinal)
    {
        "news", "actu", "actus", "wikipedia", "meteo", "weather", "google", "bing", "duckduckgo", "browse",
    };

    // Phrases that mean the same, as consecutive words.
    private static readonly string[][] WebPhrases =
    {
        new[] { "sur", "internet" }, new[] { "sur", "le", "web" }, new[] { "on", "the", "web" },
        new[] { "on", "the", "internet" }, new[] { "web", "search" }, new[] { "search", "web" },
        new[] { "search", "the", "web" }, new[] { "search", "internet" }, new[] { "search", "online" },
        new[] { "recherche", "web" }, new[] { "recherche", "internet" }, new[] { "recherche", "en", "ligne" },
        new[] { "recherches", "web" }, new[] { "recherche", "sur", "le", "web" }, new[] { "cherche", "sur", "le", "web" },
        new[] { "cherche", "sur", "internet" }, new[] { "cherche", "en", "ligne" },
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

    /// <summary>Same as the <see cref="TurnKind"/> overload; true means the previous turn did work with tools.</summary>
    public static ModelChoice Choose(string prompt, bool needsVision, string? modelOverride, bool previousTurnUsedTools) =>
        Choose(prompt, needsVision, modelOverride, previousTurnUsedTools ? TurnKind.Work : TurnKind.Plain);

    /// <summary>
    /// The model for this message. <paramref name="previousTurn"/> is what the turn before this one did
    /// (see <see cref="LastTurnKind"/>).
    /// </summary>
    public static ModelChoice Choose(string prompt, bool needsVision, string? modelOverride, TurnKind previousTurn)
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
        if (previousTurn != TurnKind.Plain && IsFollowUp(text))
            return previousTurn == TurnKind.Web
                ? new ModelChoice(chat, ModelProfile.Chat, "follows a web search")
                : new ModelChoice(code, ModelProfile.Code, "follows a turn that used tools");

        if (FindHardSignal(text) is { } hard)
            return new ModelChoice(code, ModelProfile.Code, hard);

        if (FindWebIntent(text) is { } web)
            return new ModelChoice(chat, ModelProfile.Chat, web);

        if (FindSoftSignal(text) is { } soft)
            return new ModelChoice(code, ModelProfile.Code, soft);

        return new ModelChoice(chat, ModelProfile.Chat, "general question");
    }

    /// <summary>
    /// Like <see cref="Choose"/>, then checks the chat model against the endpoint's model list: if it is
    /// unknown, down or not a text model, the code model is used instead and the reason says so.
    /// The list is fetched only for a chat choice (one cached call); with no information the choice stands.
    /// </summary>
    public static Task<ModelChoice> ResolveAsync(string prompt, bool needsVision, string? modelOverride,
        bool previousTurnUsedTools, string apiKey,
        Func<Task<IReadOnlyList<CatalogEntry>?>>? loadCatalog = null) =>
        ResolveAsync(prompt, needsVision, modelOverride,
            previousTurnUsedTools ? TurnKind.Work : TurnKind.Plain, apiKey, loadCatalog);

    /// <inheritdoc cref="ResolveAsync(string,bool,string?,bool,string,Func{Task{IReadOnlyList{CatalogEntry}?}}?)"/>
    public static async Task<ModelChoice> ResolveAsync(string prompt, bool needsVision, string? modelOverride,
        TurnKind previousTurn, string apiKey,
        Func<Task<IReadOnlyList<CatalogEntry>?>>? loadCatalog = null)
    {
        var choice = Choose(prompt, needsVision, modelOverride, previousTurn);
        if (choice.Profile != ModelProfile.Chat) return choice;

        loadCatalog ??= () => ModelCatalog.GetCachedAsync(LlmConfig.Endpoint, apiKey);
        IReadOnlyList<CatalogEntry>? catalog;
        try { catalog = await loadCatalog(); }
        catch { catalog = null; }

        if (ModelCatalog.IsUsable(catalog, choice.Model, out var why)) return choice;
        return new ModelChoice(LlmConfig.CodeModel, ModelProfile.Code, $"chat model '{choice.Model}' {why}");
    }

    // ───────────────────────────── display ─────────────────────────────

    /// <summary>
    /// The model name shown under an assistant message: the requested model, followed by the one
    /// the server reports when it is a different name (an alias such as "openweight-large").
    /// </summary>
    public static string Label(string requested, string? served)
    {
        if (string.IsNullOrWhiteSpace(served) || served.Equals(requested, StringComparison.OrdinalIgnoreCase))
            return requested;
        return $"{requested} (served: {served})";
    }

    // ───────────────────────────── history ─────────────────────────────

    /// <summary>
    /// True when the turn before the current one called tools. <paramref name="msgs"/> ends with the
    /// current user message (already added); earlier messages are scanned back to the previous user message.
    /// </summary>
    public static bool LastTurnUsedTools(JsonArray msgs) => LastTurnKind(msgs) != TurnKind.Plain;

    /// <summary>
    /// What the turn before the current message did: nothing special, only web tools (a search),
    /// or other tools (work). Same scan as <see cref="LastTurnUsedTools"/>.
    /// </summary>
    public static TurnKind LastTurnKind(JsonArray msgs)
    {
        var i = msgs.Count - 1;
        if (i >= 0 && Role(msgs[i]) == "user") i--;

        var usedTools = false;
        var onlyWeb = true;
        for (; i >= 0; i--)
        {
            var m = msgs[i];
            var role = Role(m);
            if (role == "user") break;
            if (role == "tool") usedTools = true;
            if (m?["tool_calls"] is JsonArray calls)
                foreach (var c in calls)
                {
                    usedTools = true;
                    var name = c?["function"]?["name"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
                    if (name is not ("web_search" or "fetch_url")) onlyWeb = false;
                }
        }
        return !usedTools ? TurnKind.Plain : onlyWeb ? TurnKind.Web : TurnKind.Work;
    }

    private static string Role(JsonNode? m) =>
        m?["role"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    // ───────────────────────────── message analysis ─────────────────────────────

    /// <summary>
    /// Signs that cannot be about anything but the workspace or the machine: code in the message, an attached
    /// file, a file name, a path, an action such as "open" or "download", a browser or an app. They win over everything else. Null when there are none; the text is the reason shown.
    /// Links are skipped: reading a web page is a search, not work on files.
    /// </summary>
    internal static string? FindHardSignal(string text)
    {
        if (text.Contains("```") || text.Contains('`')) return "code in the message";
        if (text.Contains("[Attached", StringComparison.OrdinalIgnoreCase)) return "attached file";

        // Split on whitespace first, so that file names, paths and URLs stay in one piece.
        foreach (var chunk in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var c = chunk.Trim('(', ')', '[', ']', '"', '\'', ',', ';', ':', '?', '!', '.', '<', '>', '*');
            if (c.Length == 0 || c.Contains("://")) continue;

            if (c.Contains('\\')) return "file path";
            if (IsUnixPath(c)) return "file path";

            var dot = c.LastIndexOf('.');
            if (dot > 0 && dot < c.Length - 1 && Extensions.Contains(Fold(c[(dot + 1)..]))) return "file name";
        }

        // An action on the machine: open, download, a browser or an app by name.
        foreach (var word in Tokens(WithoutLinks(text)))
            if (ActionWords.Contains(word)) return $"action on your computer ('{word}')";
        return null;
    }

    /// <summary>
    /// An explicit request to search the web or to read the news ("sur internet", "search the web",
    /// "actualités", "news", "météo", "wikipedia"...). It wins over words that only suggest code
    /// ("search the web for C# async tips" is a search), but not over <see cref="FindHardSignal"/>.
    /// Null when there is none; the text is the reason shown.
    /// </summary>
    internal static string? FindWebIntent(string text)
    {
        var t = new List<string>();
        foreach (var w in Tokens(WithoutLinks(text))) t.Add(w);

        foreach (var w in t)
            if (WebWords.Contains(w) || w.StartsWith("actualit", StringComparison.Ordinal))
                return $"web search ('{w}')";

        foreach (var phrase in WebPhrases)
            if (ContainsSequence(t, phrase))
                return $"web search ('{string.Join(' ', phrase)}')";
        return null;
    }

    /// <summary>
    /// Words that suggest a task on code or on the workspace (git, tests, shell, mail, audio...).
    /// Null for a general message; the text is the reason shown.
    /// </summary>
    internal static string? FindSoftSignal(string text)
    {
        text = WithoutLinks(text);
        if (text.Contains("c#", StringComparison.OrdinalIgnoreCase) || text.Contains(".net", StringComparison.OrdinalIgnoreCase))
            return "mentions code";

        foreach (var word in Tokens(text))
        {
            if (Words.Contains(word)) return $"mentions '{word}'";
            foreach (var stem in Stems)
                if (word.StartsWith(stem, StringComparison.Ordinal)) return $"mentions '{word}'";
        }
        return null;
    }

    /// <summary>The text without its links, so that "https" and the host name do not look like code words.</summary>
    private static string WithoutLinks(string text)
    {
        if (!text.Contains("://")) return text;
        var kept = new List<string>();
        foreach (var chunk in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (!chunk.Contains("://")) kept.Add(chunk);
        return string.Join(' ', kept);
    }

    private static bool ContainsSequence(List<string> tokens, string[] seq)
    {
        for (var i = 0; i + seq.Length <= tokens.Count; i++)
        {
            var ok = true;
            for (var k = 0; k < seq.Length && ok; k++) ok = tokens[i + k] == seq[k];
            if (ok) return true;
        }
        return false;
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

    /// <summary>
    /// A short reply that carries on the work in progress: it starts with an acknowledgement
    /// ("oui", "ok", "vas-y", "d'accord", "yes"...) and has few words. "fetch latest scientific news"
    /// is short too, but it is a new request, so it is not a follow-up.
    /// </summary>
    internal static bool IsFollowUp(string text)
    {
        var n = 0;
        string? first = null, second = null;
        foreach (var t in Tokens(text))
        {
            if (n == 0) first = t; else if (n == 1) second = t;
            n++;
            if (n > ShortFollowUpWords) return false;
        }
        if (first is null) return false;
        if (FollowUpStarters.Contains(first)) return true;
        var dash = first.IndexOf('-');
        if (dash > 0 && FollowUpStarters.Contains(first[..dash])) return true; // "fais-le", "vas-y", "ok-go"
        return first == "d" && second == "accord"; // d'accord
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
