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
        Dictionary<string, string>? Context = null,  // extra key/value metadata
        int Count = 1,                               // how many times this lesson was seen
        DateTime? LastSeen = null                    // last time it was seen (null = CreatedAt)
    )
    {
        public DateTime Seen => LastSeen ?? CreatedAt;
    }

    /// <summary>Upper bound of stored patterns; least useful ones are evicted first.</summary>
    public const int MaxPatterns = 200;

    private readonly List<SemanticEntry> _patterns = new();

    public int Count => _patterns.Count;

    // ── Write ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a lesson. A lesson already known (same type and normalised summary) is not
    /// duplicated: its counter and last-seen date are updated instead.
    /// When the store exceeds <see cref="MaxPatterns"/>, the least seen / oldest are evicted.
    /// </summary>
    public void AddPattern(
        string type,
        string summary,
        string[] tags,
        Dictionary<string, string>? context = null)
    {
        var now = DateTime.UtcNow;
        var key = KeyOf(type, summary);

        var i = _patterns.FindIndex(p => KeyOf(p.Type, p.Summary) == key);
        if (i >= 0)
        {
            var known = _patterns[i];
            _patterns[i] = known with
            {
                Count = known.Count + 1,
                LastSeen = now,
                Context = context ?? known.Context
            };
            return;
        }

        _patterns.Add(new SemanticEntry(
            Id: Guid.NewGuid().ToString(),
            Type: type,
            Summary: summary,
            Tags: tags,
            CreatedAt: now,
            Context: context,
            Count: 1,
            LastSeen: now));

        EvictOverflow(protectedIndex: _patterns.Count - 1);
    }

    // Identity of a lesson: type + summary, lower-cased, accent-folded, digit runs
    // collapsed ("line 12" == "line 40"), whitespace collapsed, clipped.
    private static string KeyOf(string type, string summary)
    {
        var folded = TextTokenizer.Fold(summary);
        var sb = new System.Text.StringBuilder(folded.Length + type.Length + 1);
        sb.Append(type).Append('|');

        bool lastWasSpace = false, lastWasDigit = false;
        foreach (var c in folded)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true; lastWasDigit = false;
            }
            else if (char.IsDigit(c))
            {
                if (!lastWasDigit) sb.Append('#');
                lastWasDigit = true; lastWasSpace = false;
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false; lastWasDigit = false;
            }
            if (sb.Length >= 220) break;
        }
        return sb.ToString();
    }

    // Removes the least useful entries (lowest Count, then oldest Seen) until the cap holds.
    private void EvictOverflow(int protectedIndex = -1)
    {
        while (_patterns.Count > MaxPatterns)
        {
            int victim = -1;
            for (int i = 0; i < _patterns.Count; i++)
            {
                if (i == protectedIndex) continue;
                if (victim < 0
                    || _patterns[i].Count < _patterns[victim].Count
                    || (_patterns[i].Count == _patterns[victim].Count && _patterns[i].Seen < _patterns[victim].Seen))
                    victim = i;
            }
            if (victim < 0) return;
            _patterns.RemoveAt(victim);
            if (victim < protectedIndex) protectedIndex--;
        }
    }

    // Merges duplicates (files written before de-duplication existed) and enforces the cap.
    private void Compact()
    {
        var merged = new List<SemanticEntry>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var p in _patterns)
        {
            var key = KeyOf(p.Type, p.Summary);
            if (index.TryGetValue(key, out var at))
            {
                var k = merged[at];
                merged[at] = k with
                {
                    Count = k.Count + p.Count,
                    LastSeen = k.Seen > p.Seen ? k.Seen : p.Seen,
                    CreatedAt = k.CreatedAt < p.CreatedAt ? k.CreatedAt : p.CreatedAt
                };
            }
            else
            {
                index[key] = merged.Count;
                merged.Add(p);
            }
        }

        _patterns.Clear();
        _patterns.AddRange(merged);
        EvictOverflow();
    }

    // ── Read ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Keyword search over summaries and tags (English and French).
    ///   1. The query is reduced to keywords (see <see cref="TextTokenizer"/>).
    ///   2. A pattern scores one point per distinct query keyword it contains
    ///      (whole-keyword match, not substring).
    ///   3. Patterns with score ≥ 1 are returned: best score first, then most recently
    ///      seen, then most frequent.
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
            .ThenByDescending(x => x.Entry.Seen)
            .ThenByDescending(x => x.Entry.Count)
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

        Compact();   // old files: merge duplicates, enforce the cap
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
            ["context"] = context,
            ["count"] = p.Count,
            ["lastSeen"] = p.Seen.ToString("O")
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
            Context: context?.Count > 0 ? context : null,
            Count: item?["count"] is JsonValue c && c.TryGetValue<int>(out var n) && n > 0 ? n : 1,
            LastSeen: DateTime.TryParse(item?["lastSeen"] is JsonValue ls && ls.TryGetValue<string>(out var lsText) ? lsText : null,
                          null, System.Globalization.DateTimeStyles.RoundtripKind, out var seen) ? seen : null);
    }
}