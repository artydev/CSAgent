using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsAgent.Services;

/// <summary>
/// Long-term lessons learned by the agent (error patterns, solutions).
/// Searched by keyword score; persisted as JSON with JsonNode only (AOT-safe, no reflection).
/// </summary>
public class SemanticMemory
{
    public record SemanticEntry(
        string Id,                                   // Guid string
        string Type,                                 // "error_pattern" | "solution"
        string Summary,                              // human-readable lesson
        string[] Tags,                               // ["permission", "file-ops"]
        DateTime CreatedAt,                          // when the lesson was learned
        Dictionary<string, string>? Context = null   // extra key/value metadata
    );

    private readonly List<SemanticEntry> _patterns = new();

    public int Count => _patterns.Count;

    // ── Write ────────────────────────────────────────────────────────────────────

    public void AddPattern(
        string type,
        string summary,
        string[] tags,
        Dictionary<string, string>? context = null)
    {
        _patterns.Add(new SemanticEntry(
            Id: Guid.NewGuid().ToString(),
            Type: type,
            Summary: summary,
            Tags: tags,
            CreatedAt: DateTime.UtcNow,
            Context: context));
    }

    // ── Read ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Keyword search over summaries and tags (English and French).
    ///   1. The query is reduced to keywords (see <see cref="TextTokenizer"/>).
    ///   2. A pattern scores one point per distinct query keyword it contains
    ///      (whole-keyword match, not substring).
    ///   3. Patterns with score ≥ 1 are returned: best score first, then newest.
    ///   4. An empty or all-noise query returns an empty list.
    /// </summary>
    public List<SemanticEntry> Search(string query, int limit = 3)
    {
        var wanted = TextTokenizer.Tokenize(query);
        if (wanted.Count == 0 || limit <= 0)
            return new List<SemanticEntry>();

        return _patterns
            .Select(p => (Entry: p, Score: Score(p, wanted)))
            .Where(x => x.Score >= 1)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Entry.CreatedAt)
            .Take(limit)
            .Select(x => x.Entry)
            .ToList();
    }

    private static int Score(SemanticEntry pattern, HashSet<string> wanted)
    {
        var have = TextTokenizer.Tokenize(pattern.Summary);
        foreach (var tag in pattern.Tags)
            have.UnionWith(TextTokenizer.Tokenize(tag));

        return wanted.Count(have.Contains);
    }

    // ── Persistence ──────────────────────────────────────────────────────────────

    public async Task SaveAsync(string path)
    {
        var array = new JsonArray();
        foreach (var p in _patterns)
            array.Add(ToJson(p));

        await File.WriteAllTextAsync(path,
            array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public async Task LoadAsync(string path)
    {
        if (!File.Exists(path)) return;

        var array = JsonNode.Parse(await File.ReadAllTextAsync(path))?.AsArray();
        if (array is null) return;

        _patterns.Clear();
        foreach (var item in array)
            _patterns.Add(FromJson(item));
    }

    private static JsonObject ToJson(SemanticEntry p)
    {
        // Built by hand: JsonSerializer.Serialize<T>() is disabled under AOT.
        var tags = new JsonArray();
        foreach (var tag in p.Tags)
            tags.Add(JsonValue.Create(tag));

        JsonObject? context = null;
        if (p.Context is not null)
        {
            context = new JsonObject();
            foreach (var kv in p.Context)
                context[kv.Key] = JsonValue.Create(kv.Value);
        }

        return new JsonObject
        {
            ["id"] = p.Id,
            ["type"] = p.Type,
            ["summary"] = p.Summary,
            ["tags"] = tags,
            ["createdAt"] = p.CreatedAt.ToString("O"),
            ["context"] = context
        };
    }

    private static SemanticEntry FromJson(JsonNode? item)
    {
        var tags = item?["tags"]?.AsArray()
                       .Select(t => t?.GetValue<string>() ?? "")
                       .ToArray()
                   ?? Array.Empty<string>();

        var context = item?["context"]?.AsObject()
                          ?.ToDictionary(kv => kv.Key, kv => kv.Value?.GetValue<string>() ?? "");

        return new SemanticEntry(
            Id: item?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString(),
            Type: item?["type"]?.GetValue<string>() ?? "",
            Summary: item?["summary"]?.GetValue<string>() ?? "",
            Tags: tags,
            CreatedAt: DateTime.Parse(item?["createdAt"]?.GetValue<string>()
                                      ?? DateTime.UtcNow.ToString("O")),
            Context: context?.Count > 0 ? context : null);
    }
}