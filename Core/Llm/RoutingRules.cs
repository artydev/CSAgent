using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsAgent.Core.Llm;

/// <summary>One rule of rules.json: "if the message looks like this, use that model".</summary>
internal sealed record UserRule(
    string Name, string Use,
    IReadOnlyList<Phrase> ContainsAny, IReadOnlyList<Phrase> ContainsAll,
    IReadOnlyList<Phrase> NotContains, IReadOnlyList<Phrase> StartsWith,
    int? MinWords, int? MaxWords)
{
    /// <summary>Every condition that is present must hold.</summary>
    public bool Matches(List<string> tokens)
    {
        if (MinWords is { } min && tokens.Count < min) return false;
        if (MaxWords is { } max && tokens.Count > max) return false;

        if (ContainsAny.Count > 0)
        {
            var any = false;
            foreach (var p in ContainsAny) if (p.IsIn(tokens)) { any = true; break; }
            if (!any) return false;
        }
        foreach (var p in ContainsAll) if (!p.IsIn(tokens)) return false;
        foreach (var p in NotContains) if (p.IsIn(tokens)) return false;

        if (StartsWith.Count > 0)
        {
            var any = false;
            foreach (var p in StartsWith) if (p.MatchesAt(tokens, 0)) { any = true; break; }
            if (!any) return false;
        }
        return true;
    }
}

/// <summary>
/// The routing rules: which model plays each profile, the word lists the router looks for, and the user's
/// own rules. The built-in values live in <see cref="RoutingDefaults"/>; a folder named
/// <c>LLMRoutingRules</c> (models.json, keywords.json, rules.json) changes them without touching the code.
/// The folder is searched in the working directory, then next to the executable, and is read again
/// whenever a file changes. A mistake in a file is reported as a warning and the built-in value is kept:
/// a typo can never stop CsAgent from running.
/// AOT-safe: JsonNode traversal only.
/// </summary>
public sealed class RoutingRules
{
    public const string FolderName = "LLMRoutingRules";
    public const string ModelsFile = "models.json";
    public const string KeywordsFile = "keywords.json";
    public const string RulesFile = "rules.json";
    public const string DefaultsFile = "keywords.defaults.json";

    private const int MaxFileBytes = 256 * 1024;
    private const int MaxRules = 200;

    // ───────────────────────────── the values ─────────────────────────────

    /// <summary>Model of the code profile from models.json (null: not set there).</summary>
    public string? CodeModel { get; private set; }
    /// <summary>Model of the chat profile from models.json (null: not set there).</summary>
    public string? ChatModel { get; private set; }
    /// <summary>Model of the vision profile from models.json (null: not set there).</summary>
    public string? VisionModel { get; private set; }

    internal HashSet<string> ActionWords { get; private set; } = new(StringComparer.Ordinal);
    internal HashSet<string> WebWords { get; private set; } = new(StringComparer.Ordinal);
    internal List<string> WebStems { get; private set; } = new();
    internal List<Phrase> WebPhrases { get; private set; } = new();
    internal HashSet<string> CodeWords { get; private set; } = new(StringComparer.Ordinal);
    internal List<string> CodeStems { get; private set; } = new();
    internal HashSet<string> FileExtensions { get; private set; } = new(StringComparer.Ordinal);
    internal HashSet<string> FollowUpStarters { get; private set; } = new(StringComparer.Ordinal);
    internal int FollowUpMaxWords { get; private set; } = RoutingDefaults.FollowUpMaxWords;
    internal List<UserRule> Rules { get; private set; } = new();

    /// <summary>What is wrong in the files (empty when all is well). The built-in value is used for each of them.</summary>
    public IReadOnlyList<string> Warnings { get; private set; } = Array.Empty<string>();

    /// <summary>The folder these rules were read from, or "built-in".</summary>
    public string Source { get; private set; } = "built-in";

    private bool _reported;

    /// <summary>The warnings, once: the first call returns them, the next ones return nothing (until a file changes).</summary>
    public IReadOnlyList<string> TakeNewWarnings()
    {
        lock (this)
        {
            if (_reported || Warnings.Count == 0) return Array.Empty<string>();
            _reported = true;
            return Warnings;
        }
    }

    /// <summary>The built-in rules.</summary>
    public static RoutingRules Default { get; } = Parse(null, null, null);

    // ───────────────────────────── parsing ─────────────────────────────

    /// <summary>
    /// Builds the rules from the content of the three files (null = file absent). Never throws.
    /// </summary>
    public static RoutingRules Parse(string? modelsJson, string? keywordsJson, string? rulesJson, string source = "built-in")
    {
        var w = new List<string>();
        var r = new RoutingRules { Source = source };

        // Start from the built-in lists.
        r.ActionWords = new(RoutingDefaults.ActionWords, StringComparer.Ordinal);
        r.WebWords = new(RoutingDefaults.WebWords, StringComparer.Ordinal);
        r.WebStems = new(RoutingDefaults.WebStems);
        foreach (var p in RoutingDefaults.WebPhrases) if (Phrase.Parse(p) is { } ph) r.WebPhrases.Add(ph);
        r.CodeWords = new(RoutingDefaults.CodeWords, StringComparer.Ordinal);
        r.CodeStems = new(RoutingDefaults.CodeStems);
        r.FileExtensions = new(RoutingDefaults.FileExtensions, StringComparer.Ordinal);
        r.FollowUpStarters = new(RoutingDefaults.FollowUpStarters, StringComparer.Ordinal);

        if (ParseObject(modelsJson, ModelsFile, w) is { } models) r.ApplyModels(models, w);
        if (ParseObject(keywordsJson, KeywordsFile, w) is { } keywords) r.ApplyKeywords(keywords, w);
        if (ParseObject(rulesJson, RulesFile, w) is { } rules) r.ApplyRules(rules, w);

        r.Warnings = w;
        return r;
    }

    private static JsonObject? ParseObject(string? json, string file, List<string> w)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (node is JsonObject o) return o;
            w.Add(Loc.F($"{file}: the file must contain a JSON object ({{ ... }}); the built-in values are used."));
        }
        catch (JsonException ex)
        {
            w.Add(Loc.F($"{file}: invalid JSON ({OneLine(ex.Message)}); the built-in values are used."));
        }
        return null;
    }

    private void ApplyModels(JsonObject o, List<string> w)
    {
        foreach (var (key, value) in o)
        {
            if (key.StartsWith('_')) continue;
            if (key is not ("code" or "chat" or "vision"))
            {
                w.Add(Loc.F($"{ModelsFile}: unknown key '{key}' (expected code, chat, vision)."));
                continue;
            }
            if (value is null) continue; // null = not set
            if (value is not JsonValue v || !v.TryGetValue<string>(out var name))
            {
                w.Add(Loc.F($"{ModelsFile}: '{key}' must be a model name (text)."));
                continue;
            }
            name = name.Trim();
            if (name.Length == 0) continue;
            if (name.Any(char.IsWhiteSpace))
            {
                w.Add(Loc.F($"{ModelsFile}: '{key}' is not a valid model name: '{name}'."));
                continue;
            }
            switch (key)
            {
                case "code": CodeModel = name; break;
                case "chat": ChatModel = name; break;
                default: VisionModel = name; break;
            }
        }
    }

    private void ApplyKeywords(JsonObject o, List<string> w)
    {
        foreach (var (key, value) in o)
        {
            if (key.StartsWith('_')) continue;
            switch (key)
            {
                case "action_words": ApplyWordList(value, key, ActionWords, w); break;
                case "web_words": ApplyWordList(value, key, WebWords, w); break;
                case "code_words": ApplyWordList(value, key, CodeWords, w); break;
                case "file_extensions": ApplyWordList(value, key, FileExtensions, w, stripDot: true); break;
                case "follow_up_starters": ApplyWordList(value, key, FollowUpStarters, w); break;
                case "web_stems": ApplyStemList(value, key, WebStems, w); break;
                case "code_stems": ApplyStemList(value, key, CodeStems, w); break;
                case "web_phrases": ApplyPhraseList(value, key, WebPhrases, w); break;
                case "follow_up_max_words":
                    if (value is JsonValue v && v.TryGetValue<int>(out var n) && n is >= 1 and <= 50) FollowUpMaxWords = n;
                    else w.Add(Loc.F($"{KeywordsFile}: 'follow_up_max_words' must be a number between 1 and 50."));
                    break;
                default:
                    w.Add(Loc.F($"{KeywordsFile}: unknown key '{key}'."));
                    break;
            }
        }
    }

    /// <summary>
    /// A list is either an array (the words to add) or { "add": [...], "remove": [...] }.
    /// Words are compared without case or accents; each entry is a single word.
    /// </summary>
    private static void ApplyEntries(JsonNode? node, string name, Action<string> add, Action<string> remove, List<string> w)
    {
        if (node is JsonArray arr) { Each(arr, name, "add", add, w); return; }
        if (node is JsonObject obj)
        {
            foreach (var (k, v) in obj)
            {
                if (k.StartsWith('_')) continue;
                if (k == "add" && v is JsonArray a) Each(a, name, k, add, w);
                else if (k == "remove" && v is JsonArray rm) Each(rm, name, k, remove, w);
                else w.Add(Loc.F($"{KeywordsFile}: '{name}.{k}' is not understood (expected \"add\" and \"remove\" lists)."));
            }
            return;
        }
        w.Add(Loc.F($"{KeywordsFile}: '{name}' must be a list, or {{ \"add\": [...], \"remove\": [...] }}."));
    }

    private static void Each(JsonArray arr, string name, string part, Action<string> act, List<string> w)
    {
        foreach (var item in arr)
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var s)) act(s);
            else w.Add(Loc.F($"{KeywordsFile}: '{name}.{part}' must contain text only."));
        }
    }

    private static void ApplyWordList(JsonNode? node, string name, HashSet<string> set, List<string> w, bool stripDot = false)
    {
        string? Norm(string s)
        {
            var t = RoutingText.Fold(s.Trim());
            if (stripDot) t = t.TrimStart('.');
            if (t.Length == 0) return null;
            if (t.Any(char.IsWhiteSpace))
            {
                if (name == "web_phrases") w.Add(Loc.F($"{KeywordsFile}: '{name}' takes single words; '{s.Trim()}' has a space."));
                else w.Add(Loc.F($"{KeywordsFile}: '{name}' takes single words; '{s.Trim()}' has a space (phrases go in web_phrases or in a rule)."));
                return null;
            }
            return t;
        }
        ApplyEntries(node, name,
            s => { if (Norm(s) is { } t) set.Add(t); },
            s => { if (Norm(s) is { } t) set.Remove(t); }, w);
    }

    private static void ApplyStemList(JsonNode? node, string name, List<string> list, List<string> w)
    {
        string? Norm(string s)
        {
            var t = RoutingText.Fold(s.Trim()).TrimEnd('*');
            if (t.Length == 0) return null;
            if (t.Any(char.IsWhiteSpace)) { w.Add(Loc.F($"{KeywordsFile}: '{name}' takes single words; '{s.Trim()}' has a space.")); return null; }
            return t;
        }
        ApplyEntries(node, name,
            s => { if (Norm(s) is { } t && !list.Contains(t)) list.Add(t); },
            s => { if (Norm(s) is { } t) list.Remove(t); }, w);
    }

    private static void ApplyPhraseList(JsonNode? node, string name, List<Phrase> list, List<string> w)
    {
        ApplyEntries(node, name,
            s =>
            {
                if (Phrase.Parse(s) is not { } p) return;
                if (!list.Any(x => x.ToString() == p.ToString())) list.Add(p);
            },
            s =>
            {
                if (Phrase.Parse(s) is not { } p) return;
                list.RemoveAll(x => x.ToString() == p.ToString());
            }, w);
    }

    private void ApplyRules(JsonObject o, List<string> w)
    {
        JsonArray? arr = null;
        foreach (var (key, value) in o)
        {
            if (key.StartsWith('_')) continue;
            if (key == "rules")
            {
                if (value is JsonArray a) arr = a;
                else w.Add(Loc.F($"{RulesFile}: 'rules' must be a list."));
            }
            else w.Add(Loc.F($"{RulesFile}: unknown key '{key}' (expected \"rules\")."));
        }
        if (arr is null) return;

        var index = 0;
        foreach (var item in arr)
        {
            index++;
            if (Rules.Count >= MaxRules)
            {
                w.Add(Loc.F($"{RulesFile}: only the first {MaxRules} rules are used."));
                break;
            }
            if (item is not JsonObject ro)
            {
                w.Add(Loc.F($"{RulesFile}: rule {index} must be an object ({{ ... }}); skipped."));
                continue;
            }
            if (ParseRule(ro, index, w) is { } rule) Rules.Add(rule);
        }
    }

    private static UserRule? ParseRule(JsonObject o, int index, List<string> w)
    {
        var name = $"rule {index}";
        if (o["name"] is JsonValue nv && nv.TryGetValue<string>(out var nm) && !string.IsNullOrWhiteSpace(nm)) name = nm.Trim();

        var label = $"{RulesFile}: rule '{name}'";
        string? use = null;
        var any = new List<Phrase>(); var all = new List<Phrase>(); var not = new List<Phrase>(); var starts = new List<Phrase>();
        int? min = null, max = null;
        var enabled = true;
        var conditions = 0;
        var ok = true;

        foreach (var (key, value) in o)
        {
            if (key.StartsWith('_') || key == "name") continue;
            switch (key)
            {
                case "enabled":
                    if (value is JsonValue ev && ev.TryGetValue<bool>(out var b)) enabled = b;
                    else { w.Add(Loc.F($"{label}: 'enabled' must be true or false.")); ok = false; }
                    break;
                case "use":
                    if (value is JsonValue uv && uv.TryGetValue<string>(out var u) && !string.IsNullOrWhiteSpace(u) && !u.Trim().Any(char.IsWhiteSpace))
                        use = u.Trim();
                    else { w.Add(Loc.F($"{label}: 'use' must be \"code\", \"chat\", \"vision\" or a model name.")); ok = false; }
                    break;
                case "contains": ok &= Phrases(value, key, any, label, w); conditions++; break;
                case "contains_all": ok &= Phrases(value, key, all, label, w); conditions++; break;
                case "not_contains": ok &= Phrases(value, key, not, label, w); conditions++; break;
                case "starts_with": ok &= Phrases(value, key, starts, label, w); conditions++; break;
                case "min_words":
                    if (value is JsonValue mv && mv.TryGetValue<int>(out var mi) && mi >= 0) min = mi;
                    else { w.Add(Loc.F($"{label}: 'min_words' must be a number.")); ok = false; }
                    conditions++;
                    break;
                case "max_words":
                    if (value is JsonValue xv && xv.TryGetValue<int>(out var xi) && xi >= 0) max = xi;
                    else { w.Add(Loc.F($"{label}: 'max_words' must be a number.")); ok = false; }
                    conditions++;
                    break;
                default:
                    w.Add(Loc.F($"{label}: unknown key '{key}' (expected name, use, contains, contains_all, not_contains, starts_with, min_words, max_words, enabled)."));
                    ok = false;
                    break;
            }
        }

        if (!enabled) return null;
        if (use is null && ok) { w.Add(Loc.F($"{label}: 'use' is missing; rule skipped.")); ok = false; }
        if (conditions == 0 && ok) { w.Add(Loc.F($"{label}: no condition (contains, starts_with...); rule skipped.")); ok = false; }
        if (ok && conditions > 0 && any.Count + all.Count + not.Count + starts.Count == 0 && min is null && max is null)
        { w.Add(Loc.F($"{label}: its conditions are empty; rule skipped.")); ok = false; }
        return ok ? new UserRule(name, use!, any, all, not, starts, min, max) : null;
    }

    /// <summary>A condition is one text or a list of texts (words, phrases, "stem*").</summary>
    private static bool Phrases(JsonNode? node, string key, List<Phrase> into, string label, List<string> w)
    {
        IEnumerable<JsonNode?> items = node switch
        {
            JsonArray a => a,
            JsonValue => new[] { node },
            _ => Array.Empty<JsonNode?>(),
        };
        var count = 0;
        foreach (var item in items)
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var s))
            {
                if (Phrase.Parse(s) is { } p) { into.Add(p); count++; }
            }
            else { w.Add(Loc.F($"{label}: '{key}' must contain text only.")); return false; }
        }
        if (count == 0 && node is not JsonArray) { w.Add(Loc.F($"{label}: '{key}' must be a text or a list of texts.")); return false; }
        return true;
    }

    private static string OneLine(string s)
    {
        var line = s.Replace('\r', ' ').Replace('\n', ' ');
        return line.Length > 160 ? line[..160] + "..." : line;
    }

    // ───────────────────────────── loading from disk ─────────────────────────────

    /// <summary>Set from --rules / CSAGENT_ROUTING_RULES: a folder to use instead of the default places.</summary>
    public static string? FolderOverride { get; set; }

    /// <summary>The folder of the executable (replaced in tests).</summary>
    internal static string ExeFolder { get; set; } = AppContext.BaseDirectory;

    /// <summary>Where the rules come from. Replaced in tests to keep them independent of the disk.</summary>
    internal static Func<RoutingRules> Provider { get; set; } = LoadFromDisk;

    private static readonly object CacheGate = new();
    private static (string Key, RoutingRules Rules)? _cache;

    /// <summary>The rules to use now: read from the folder when there is one, built-in otherwise.</summary>
    public static RoutingRules Current() => Provider();

    /// <summary>Restores the disk loader (tests).</summary>
    internal static void ResetProvider()
    {
        Provider = LoadFromDisk;
        lock (CacheGate) _cache = null;
    }

    /// <summary>The folder in use: --rules, else ./LLMRoutingRules, else the one next to the executable. Null when none exists.</summary>
    public static string? FindFolder()
    {
        if (!string.IsNullOrWhiteSpace(FolderOverride))
            return Directory.Exists(FolderOverride) ? Path.GetFullPath(FolderOverride) : null; // explicit choice: no silent fallback
        try
        {
            var here = Path.Combine(Directory.GetCurrentDirectory(), FolderName);
            if (Directory.Exists(here)) return here;
        }
        catch { /* the working directory can disappear */ }
        var exe = Path.Combine(ExeFolder, FolderName);
        return Directory.Exists(exe) ? exe : null;
    }

    private static RoutingRules LoadFromDisk()
    {
        try
        {
            var folder = FindFolder();
            if (folder is null)
            {
                if (string.IsNullOrWhiteSpace(FolderOverride)) return Default;
                return Cached("missing:" + FolderOverride, () =>
                {
                    var r = Parse(null, null, null);
                    r.Warnings = new[] { Loc.F($"--rules: the folder '{FolderOverride}' does not exist; the built-in rules are used.") };
                    return r;
                });
            }

            var key = folder + "|" + Stamp(folder, ModelsFile) + Stamp(folder, KeywordsFile) + Stamp(folder, RulesFile);
            return Cached(key, () =>
            {
                var w = new List<string>();
                var rules = Parse(Read(folder, ModelsFile, w), Read(folder, KeywordsFile, w), Read(folder, RulesFile, w), folder);
                if (w.Count > 0) rules.Warnings = w.Concat(rules.Warnings).ToList();
                return rules;
            });
        }
        catch (Exception ex)
        {
            // Whatever happens, routing must not stop the agent.
            var r = Parse(null, null, null);
            r.Warnings = new[] { Loc.F($"{FolderName}: cannot be read ({OneLine(ex.Message)}); the built-in rules are used.") };
            return r;
        }
    }

    private static RoutingRules Cached(string key, Func<RoutingRules> build)
    {
        lock (CacheGate)
        {
            if (_cache is { } c && c.Key == key) return c.Rules;
            var rules = build();
            _cache = (key, rules);
            return rules;
        }
    }

    private static string Stamp(string folder, string file)
    {
        var p = Path.Combine(folder, file);
        if (!File.Exists(p)) return "-;";
        var fi = new FileInfo(p);
        return $"{fi.LastWriteTimeUtc.Ticks}:{fi.Length};";
    }

    private static string? Read(string folder, string file, List<string> w)
    {
        var p = Path.Combine(folder, file);
        if (!File.Exists(p)) return null;
        try
        {
            if (new FileInfo(p).Length > MaxFileBytes)
            {
                w.Add(Loc.F($"{file}: the file is larger than {MaxFileBytes / 1024} KB; the built-in values are used."));
                return null;
            }
            return File.ReadAllText(p, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file locked by an editor or an antivirus: not fatal.
            w.Add(Loc.F($"{file}: cannot be read now ({OneLine(ex.Message)}); the built-in values are used."));
            return null;
        }
    }

    // ───────────────────────────── files written by --init-routing ─────────────────────────────

    private static readonly JsonWriterOptions Pretty = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep accents readable
    };

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var wr = new Utf8JsonWriter(ms, Pretty))
        {
            wr.WriteStartObject();
            body(wr);
            wr.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    /// <summary>models.json with the built-in model names.</summary>
    public static string ModelsTemplate() => Write(wr =>
    {
        wr.WriteString("_comment", Loc.F($"Which model plays each profile. null = the built-in value ({LlmSettings.Model} / {LlmSettings.ChatModel} / {LlmSettings.VisionModel}); the chat model is only used on the default endpoint. --code-model / --chat-model / --vision-model (and CSAGENT_MODEL_*) win over this file."));
        // null = not set: the built-in choice applies (the chat model, for instance, only exists on the default endpoint).
        wr.WriteNull("code");
        wr.WriteNull("chat");
        wr.WriteNull("vision");
    });

    /// <summary>keywords.json: nothing added, nothing removed (the built-in lists apply).</summary>
    public static string KeywordsTemplate() => Write(wr =>
    {
        wr.WriteString("_comment", Loc.T("Add words to the built-in lists, or remove some. Words are compared without case or accents. " +
                                   "See keywords.defaults.json for the built-in lists. A list can also be a plain array (words to add)."));
        foreach (var name in RoutingDefaults.ListNames)
        {
            wr.WriteStartObject(name);
            wr.WriteStartArray("add"); wr.WriteEndArray();
            wr.WriteStartArray("remove"); wr.WriteEndArray();
            wr.WriteEndObject();
        }
        wr.WriteNumber("follow_up_max_words", RoutingDefaults.FollowUpMaxWords);
    });

    /// <summary>keywords.defaults.json: the built-in lists in full (reference only, never read).</summary>
    public static string DefaultsReference() => Write(wr =>
    {
        wr.WriteString("_comment", Loc.T("The built-in lists, for reference. This file is not read: change keywords.json instead " +
                                   "(\"add\" words here that are missing, \"remove\" the ones you do not want). Regenerated by --init-routing."));
        foreach (var name in RoutingDefaults.ListNames)
        {
            wr.WriteStartArray(name);
            foreach (var item in RoutingDefaults.List(name)) wr.WriteStringValue(item);
            wr.WriteEndArray();
        }
        wr.WriteNumber("follow_up_max_words", RoutingDefaults.FollowUpMaxWords);
    });

    /// <summary>rules.json: no rule yet, with examples to copy (keys starting with "_" are ignored).</summary>
    public static string RulesTemplate() => Write(wr =>
    {
        wr.WriteString("_comment", Loc.T("Your own rules, checked in order before the built-in logic: the first rule that matches decides. " +
                                   "Conditions (all that are present must hold): contains (any of), contains_all, not_contains, starts_with, min_words, max_words. " +
                                   "Words are compared without case or accents; \"recherch*\" matches any word that starts with it; a phrase is several words (\"sur le web\"). " +
                                   "use: \"code\", \"chat\", \"vision\", or a model name. \"enabled\": false switches a rule off. " +
                                   "--model and an image in the conversation win over these rules; --no-route turns them off."));
        wr.WriteStartArray("rules"); wr.WriteEndArray();
        wr.WriteStartArray("_examples");
        Example(wr, Loc.T("tickets go to the code model"), w => { w.WriteStartArray("contains"); w.WriteStringValue("jira"); w.WriteStringValue("ticket"); w.WriteEndArray(); }, "code");
        Example(wr, Loc.T("translations to the chat model"), w => { w.WriteStartArray("starts_with"); w.WriteStringValue("traduis"); w.WriteStringValue("translate"); w.WriteEndArray(); }, "chat");
        Example(wr, Loc.T("a specific model for long documents"), w => { w.WriteStartArray("contains"); w.WriteStringValue("contrat*"); w.WriteStringValue("rapport"); w.WriteEndArray(); w.WriteNumber("min_words", 30); }, "openweight-large");
        wr.WriteEndArray();
    });

    private static void Example(Utf8JsonWriter wr, string name, Action<Utf8JsonWriter> conditions, string use)
    {
        wr.WriteStartObject();
        wr.WriteString("name", name);
        conditions(wr);
        wr.WriteString("use", use);
        wr.WriteEndObject();
    }

    /// <summary>A short guide, written next to the files.</summary>
    public static string ReadmeTemplate() => Loc.IsFrench ? ReadmeFrench() : ReadmeEnglish();

    private static string ReadmeEnglish() => """
        # LLMRoutingRules

        These files tell CsAgent which model answers each message. Edit them with any text editor:
        they are read again whenever a file changes, no restart needed. A mistake is reported as a
        warning and the built-in value is used instead.

        | File | What it does |
        |---|---|
        | `models.json` | The model of each profile: `code` (files, shell, git, mail...), `chat` (general questions, searches), `vision` (images). |
        | `keywords.json` | Add or remove words in the lists the router looks for (actions on your computer, web searches, code words, file extensions...). |
        | `rules.json` | Your own rules: "if the message contains X, use model Y". Checked first; the first match wins. |
        | `keywords.defaults.json` | The built-in lists, for reference only (never read). Regenerated by `csagent --init-routing`. |

        Where CsAgent looks: `--rules <folder>` (or `CSAGENT_ROUTING_RULES`), then `./LLMRoutingRules` in the
        working directory, then the `LLMRoutingRules` folder next to `CsAgent.exe`.

        Test a rule without calling any model:

            csagent --explain-routing "open in Edge browser"

        Order of decisions: `--model` > an image in the conversation > `rules.json` > built-in logic.
        `--no-route` turns the automatic choice off, `rules.json` included.
        """.Replace("\r\n", "\n") + "\n";

    private static string ReadmeFrench() => """
        # LLMRoutingRules

        Ces fichiers indiquent à CsAgent quel modèle répond à chaque message. Modifiez-les avec un éditeur de texte :
        ils sont relus dès qu'un fichier change, sans redémarrage. Une erreur est signalée par un
        avertissement et la valeur intégrée est utilisée à la place.

        | Fichier | Rôle |
        |---|---|
        | `models.json` | Le modèle de chaque profil : `code` (fichiers, shell, git, mail...), `chat` (questions générales, recherches), `vision` (images). |
        | `keywords.json` | Ajoute ou retire des mots dans les listes que le routeur utilise (actions sur votre ordinateur, recherches web, mots de code, extensions de fichier...). |
        | `rules.json` | Vos propres règles : « si le message contient X, utiliser le modèle Y ». Testées en premier ; la première qui correspond l'emporte. |
        | `keywords.defaults.json` | Les listes intégrées, pour information seulement (jamais lu). Régénéré par `csagent --init-routing`. |

        Où CsAgent cherche : `--rules <dossier>` (ou `CSAGENT_ROUTING_RULES`), puis `./LLMRoutingRules` dans le
        répertoire courant, puis le dossier `LLMRoutingRules` à côté de `CsAgent.exe`.

        Tester une règle sans appeler de modèle :

            csagent --explain-routing "open in Edge browser"

        Ordre des décisions : `--model` > une image dans la conversation > `rules.json` > logique intégrée.
        `--no-route` désactive le choix automatique, `rules.json` compris.

        Langue des messages : `--lang fr|en` ou `CSAGENT_LANG` (sinon la langue du système).
        """.Replace("\r\n", "\n") + "\n";
}
