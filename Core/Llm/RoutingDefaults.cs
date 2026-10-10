namespace CsAgent.Core.Llm;

/// <summary>
/// The built-in word lists of the model router. They are the starting point of <see cref="RoutingRules"/>:
/// the files of the LLMRoutingRules folder add to them or remove from them. Words are lower-case and
/// without accents (messages are compared the same way).
/// </summary>
internal static class RoutingDefaults
{
    /// <summary>A reply of at most this many words that starts with a follow-up word continues the previous turn.</summary>
    public const int FollowUpMaxWords = 8;

    /// <summary>
    /// First words of a reply that continues the work in progress ("yes, go ahead", "ok continue with the second one").
    /// A short message that starts any other way is a new request, however few words it has.
    /// </summary>
    public static readonly string[] FollowUpStarters =
    {
        "oui", "non", "ok", "okay", "yes", "no", "yep", "yeah", "sure", "go", "continue", "continues", "continuer",
        "vas-y", "vasy", "allez", "parfait", "merci", "thanks", "thank", "fais", "do", "proceed", "retry",
        "reessaie", "reessayer", "encore", "again", "accord",
    };

    /// <summary>Whole words that mark a task on code or on the workspace.</summary>
    public static readonly string[] CodeWords =
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

    /// <summary>Word beginnings that mark the same (one entry covers conjugations and plurals).</summary>
    public static readonly string[] CodeStems =
    {
        "refactor", "compil", "debug", "deboug", "commit", "branch", "depot",
        "implement", "execut", "install", "transcri", "programmation", "programmer", "programming", "algorithm", "develop", "deploy",
        "exception", "fichier", "dossier", "folder", "repertoire", "presse-papier", "pull-request",
        "attach", "piece-jointe", "unitaire", "dependanc", "dependenc", "librairie", "librar", "framework",
        "commande", "command", "pipeline", "base-de-donnee", "database", "stacktrace", "traceback",
    };

    /// <summary>
    /// Actions on the user's machine: opening or downloading something, and the names of browsers and apps.
    /// They win over a web search ("open the news site in Edge" is an action, not a search).
    /// </summary>
    public static readonly string[] ActionWords =
    {
        "open", "ouvre", "ouvres", "ouvrir", "ouvrez", "download", "telecharge", "telecharger", "telechargez",
        "edge", "chrome", "firefox", "safari", "browser", "navigateur", "notepad", "bloc-notes", "excel",
        "powerpoint", "vscode", "explorateur", "finder",
    };

    /// <summary>Words that mean "search the web / read the news" on their own (the word "web" alone is also "web UI").</summary>
    public static readonly string[] WebWords =
    {
        "news", "actu", "actus", "wikipedia", "meteo", "weather", "google", "bing", "duckduckgo", "browse",
    };

    /// <summary>Word beginnings that mean the same ("actualit" covers actualité, actualités).</summary>
    public static readonly string[] WebStems = { "actualit" };

    /// <summary>Phrases that mean the same, as consecutive words.</summary>
    public static readonly string[] WebPhrases =
    {
        "sur internet", "sur le web", "on the web", "on the internet", "web search", "search web",
        "search the web", "search internet", "search online", "recherche web", "recherche internet",
        "recherche en ligne", "recherches web", "recherche sur le web", "cherche sur le web",
        "cherche sur internet", "cherche en ligne",
    };

    /// <summary>File extensions: a word such as "Program.cs" or "notes.md" is a file.</summary>
    public static readonly string[] FileExtensions =
    {
        "cs", "csproj", "sln", "py", "js", "ts", "tsx", "jsx", "json", "yaml", "yml", "xml", "html", "css", "md",
        "sh", "ps1", "bat", "go", "rs", "java", "kt", "c", "cpp", "h", "sql", "toml", "ini", "txt", "log", "zip",
        "pdf", "docx", "xlsx", "csv", "pptx", "msg", "eml", "mp3", "wav", "m4a", "ogg", "flac", "webm", "mp4",
        "png", "jpg", "jpeg", "gif", "webp", "svg", "dll", "exe", "env", "lock",
    };

    /// <summary>The lists of keywords.json, in the order they are written.</summary>
    public static readonly string[] ListNames =
    {
        "action_words", "web_words", "web_stems", "web_phrases", "code_words", "code_stems", "file_extensions", "follow_up_starters",
    };

    public static string[] List(string name) => name switch
    {
        "action_words" => ActionWords,
        "web_words" => WebWords,
        "web_stems" => WebStems,
        "web_phrases" => WebPhrases,
        "code_words" => CodeWords,
        "code_stems" => CodeStems,
        "file_extensions" => FileExtensions,
        "follow_up_starters" => FollowUpStarters,
        _ => Array.Empty<string>(),
    };
}
