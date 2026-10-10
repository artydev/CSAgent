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
    /// <summary>Problems found in the LLMRoutingRules files, to show once (see <see cref="RoutingRules.Warnings"/>).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>"[model: x]" for the default case, "[model: x (chat: general question)]" otherwise.</summary>
    public string Describe() =>
        Profile == ModelProfile.Code && Reason == Loc.T("default") ? $"[model: {Model}]" : $"[model: {Model} ({Profile.ToString().ToLowerInvariant()}: {Reason})]";
}

/// <summary>
/// Picks the model for each user message. Deterministic and free (no extra LLM call), in this order:
/// <list type="number">
/// <item><c>--model</c> always wins.</item>
/// <item>An image in the conversation selects the vision model.</item>
/// <item>The user's own rules (LLMRoutingRules/rules.json): the first that matches decides.</item>
/// <item>A message about code, files, a shell, mail... selects the code model, the default.</item>
/// <item>Searches (web, news, a link to read) and general conversation select the chat model, if the
/// endpoint has one. An explicit "search the web / sur internet" or "news" wins over code words.</item>
/// <item>A short acknowledgement ("yes, go ahead") continues the previous turn: the chat model after a
/// web search, the code model after any other tool use.</item>
/// </list>
/// The models and the word lists come from <see cref="RoutingRules"/> (built-in values, changed by the
/// files of the LLMRoutingRules folder).
/// AOT-safe: no Regex, manual tokenizing.
/// </summary>
public static class ModelRouter
{
    // ───────────────────────────── the models ─────────────────────────────

    // Order of precedence for each profile: the command line / environment, then models.json, then the built-in value.
    internal static string CodeModel(RoutingRules r) => LlmConfig.ExplicitCodeModel ?? r.CodeModel ?? LlmSettings.Model;

    /// <summary>The chat model, or null when there is none (a custom endpoint where nothing was set).</summary>
    internal static string? ChatModel(RoutingRules r) =>
        LlmConfig.ExplicitChatModel ?? r.ChatModel
        ?? (LlmConfig.Endpoint == LlmSettings.Endpoint ? LlmSettings.ChatModel : null);

    internal static string VisionModel(RoutingRules r) =>
        LlmConfig.VisionIsExplicit ? LlmConfig.VisionModel : r.VisionModel ?? LlmConfig.VisionModel;

    // ───────────────────────────── choice ─────────────────────────────

    /// <summary>Same as the <see cref="TurnKind"/> overload; true means the previous turn did work with tools.</summary>
    public static ModelChoice Choose(string prompt, bool needsVision, string? modelOverride, bool previousTurnUsedTools,
        RoutingRules? rules = null) =>
        Choose(prompt, needsVision, modelOverride, previousTurnUsedTools ? TurnKind.Work : TurnKind.Plain, rules);

    /// <summary>
    /// The model for this message. <paramref name="previousTurn"/> is what the turn before this one did
    /// (see <see cref="LastTurnKind"/>). <paramref name="rules"/> defaults to <see cref="RoutingRules.Current"/>.
    /// </summary>
    public static ModelChoice Choose(string prompt, bool needsVision, string? modelOverride, TurnKind previousTurn,
        RoutingRules? rules = null)
    {
        if (!string.IsNullOrWhiteSpace(modelOverride))
            return new ModelChoice(modelOverride, ModelProfile.Explicit, "--model");

        rules ??= RoutingRules.Current();
        var code = CodeModel(rules);
        var chat = ChatModel(rules);

        if (needsVision)
            return new ModelChoice(VisionModel(rules), ModelProfile.Vision, Loc.T("image in the conversation"));

        if (!LlmConfig.AutoRoute)
            return new ModelChoice(code, ModelProfile.Code, Loc.T("default"));

        var text = prompt ?? "";

        if (FindUserRule(text, rules, code, chat) is { } custom)
            return custom;

        if (chat is null || chat.Equals(code, StringComparison.OrdinalIgnoreCase))
            return new ModelChoice(code, ModelProfile.Code, Loc.T("default"));

        if (previousTurn != TurnKind.Plain && IsFollowUp(text, rules))
            return previousTurn == TurnKind.Web
                ? new ModelChoice(chat, ModelProfile.Chat, Loc.T("follows a web search"))
                : new ModelChoice(code, ModelProfile.Code, Loc.T("follows a turn that used tools"));

        if (FindHardSignal(text, rules) is { } hard)
            return new ModelChoice(code, ModelProfile.Code, hard);

        if (FindWebIntent(text, rules) is { } web)
            return new ModelChoice(chat, ModelProfile.Chat, web);

        if (FindSoftSignal(text, rules) is { } soft)
            return new ModelChoice(code, ModelProfile.Code, soft);

        return new ModelChoice(chat, ModelProfile.Chat, Loc.T("general question"));
    }

    /// <summary>The first rule of rules.json that matches the message, as a choice; null when none does.</summary>
    private static ModelChoice? FindUserRule(string text, RoutingRules rules, string code, string? chat)
    {
        if (rules.Rules.Count == 0) return null;
        var tokens = RoutingText.MessageTokens(text);

        foreach (var rule in rules.Rules)
        {
            if (!rule.Matches(tokens)) continue;
            var why = Loc.F($"rule '{rule.Name}'");
            switch (rule.Use.ToLowerInvariant())
            {
                case "code": return new ModelChoice(code, ModelProfile.Code, why);
                case "chat":
                    return chat is null
                        ? new ModelChoice(code, ModelProfile.Code, why + Loc.T(" (no chat model on this endpoint)"))
                        : new ModelChoice(chat, ModelProfile.Chat, why);
                case "vision": return new ModelChoice(VisionModel(rules), ModelProfile.Vision, why);
                default: return new ModelChoice(rule.Use, ModelProfile.Explicit, why);
            }
        }
        return null;
    }

    /// <summary>
    /// Like <see cref="Choose"/>, then checks the chat model against the endpoint's model list: if it is
    /// unknown, down or not a text model, the code model is used instead and the reason says so.
    /// The list is fetched only for a chat choice (one cached call); with no information the choice stands.
    /// Problems found in the LLMRoutingRules files are returned once, in <see cref="ModelChoice.Notes"/>.
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
        var rules = RoutingRules.Current();
        var choice = Choose(prompt, needsVision, modelOverride, previousTurn, rules);
        var notes = rules.TakeNewWarnings();
        if (notes.Count > 0) choice = choice with { Notes = notes };
        if (choice.Profile != ModelProfile.Chat) return choice;

        loadCatalog ??= () => ModelCatalog.GetCachedAsync(LlmConfig.Endpoint, apiKey);
        IReadOnlyList<CatalogEntry>? catalog;
        try { catalog = await loadCatalog(); }
        catch { catalog = null; }

        if (ModelCatalog.IsUsable(catalog, choice.Model, out var why)) return choice;
        return new ModelChoice(CodeModel(rules), ModelProfile.Code, Loc.F($"chat model '{choice.Model}' {why}")) { Notes = notes };
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
    /// file, a file name, a path, an action such as "open" or "download", a browser or an app. They win over
    /// everything else. Null when there are none; the text is the reason shown.
    /// Links are skipped: reading a web page is a search, not work on files.
    /// </summary>
    internal static string? FindHardSignal(string text, RoutingRules? rules = null)
    {
        rules ??= RoutingRules.Current();
        if (text.Contains("```") || text.Contains('`')) return Loc.T("code in the message");
        if (text.Contains("[Attached", StringComparison.OrdinalIgnoreCase)) return Loc.T("attached file");

        // Split on whitespace first, so that file names, paths and URLs stay in one piece.
        foreach (var chunk in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var c = chunk.Trim('(', ')', '[', ']', '"', '\'', ',', ';', ':', '?', '!', '.', '<', '>', '*');
            if (c.Length == 0 || c.Contains("://")) continue;

            if (c.Contains('\\')) return Loc.T("file path");
            if (IsUnixPath(c)) return Loc.T("file path");

            var dot = c.LastIndexOf('.');
            if (dot > 0 && dot < c.Length - 1 && rules.FileExtensions.Contains(RoutingText.Fold(c[(dot + 1)..]))) return Loc.T("file name");
        }

        // An action on the machine: open, download, a browser or an app by name.
        foreach (var word in RoutingText.Tokens(RoutingText.WithoutLinks(text)))
            if (rules.ActionWords.Contains(word)) return Loc.F($"action on your computer ('{word}')");
        return null;
    }

    /// <summary>
    /// An explicit request to search the web or to read the news ("sur internet", "search the web",
    /// "actualités", "news", "météo", "wikipedia"...). It wins over words that only suggest code
    /// ("search the web for C# async tips" is a search), but not over <see cref="FindHardSignal"/>.
    /// Null when there is none; the text is the reason shown.
    /// </summary>
    internal static string? FindWebIntent(string text, RoutingRules? rules = null)
    {
        rules ??= RoutingRules.Current();
        var t = RoutingText.MessageTokens(text);

        foreach (var w in t)
        {
            if (rules.WebWords.Contains(w)) return Loc.F($"web search ('{w}')");
            foreach (var stem in rules.WebStems)
                if (w.StartsWith(stem, StringComparison.Ordinal)) return Loc.F($"web search ('{w}')");
        }

        foreach (var phrase in rules.WebPhrases)
            if (phrase.IsIn(t))
                return Loc.F($"web search ('{phrase}')");
        return null;
    }

    /// <summary>
    /// Words that suggest a task on code or on the workspace (git, tests, shell, mail, audio...).
    /// Null for a general message; the text is the reason shown.
    /// </summary>
    internal static string? FindSoftSignal(string text, RoutingRules? rules = null)
    {
        rules ??= RoutingRules.Current();
        text = RoutingText.WithoutLinks(text);
        if (text.Contains("c#", StringComparison.OrdinalIgnoreCase) || text.Contains(".net", StringComparison.OrdinalIgnoreCase))
            return Loc.T("mentions code");

        foreach (var word in RoutingText.Tokens(text))
        {
            if (rules.CodeWords.Contains(word)) return $"mentions '{word}'";
            foreach (var stem in rules.CodeStems)
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

    /// <summary>
    /// A short reply that carries on the work in progress: it starts with an acknowledgement
    /// ("oui", "ok", "vas-y", "d'accord", "yes"...) and has few words. "fetch latest scientific news"
    /// is short too, but it is a new request, so it is not a follow-up.
    /// </summary>
    internal static bool IsFollowUp(string text, RoutingRules? rules = null)
    {
        rules ??= RoutingRules.Current();
        var n = 0;
        string? first = null, second = null;
        foreach (var t in RoutingText.Tokens(text))
        {
            if (n == 0) first = t; else if (n == 1) second = t;
            n++;
            if (n > rules.FollowUpMaxWords) return false;
        }
        if (first is null) return false;
        if (rules.FollowUpStarters.Contains(first)) return true;
        var dash = first.IndexOf('-');
        if (dash > 0 && rules.FollowUpStarters.Contains(first[..dash])) return true; // "fais-le", "vas-y", "ok-go"
        return first == "d" && second == "accord"; // d'accord
    }
}
