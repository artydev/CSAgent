using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsAgent.Core.Memory;

/// <summary>
/// Persistent store for atomic propositions extracted from conversations.
///
/// Storage format: a flat JSON array where each entry is a small object:
/// {
///   "entity":  "api_key",
///   "text":    "The API key is stored in the .env file at the project root.",
///   "date":    "2026-06-09",
///   "session": 3
/// }
///
/// AOT-safe: all reads and writes use JsonNode/JsonArray/JsonObject/JsonValue.
/// No JsonSerializer.Deserialize&lt;T&gt; — no reflection, no trimming warnings.
/// </summary>
public static class PropositionStore
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    // ── Load ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads all propositions from <paramref name="path"/>.
    /// Returns an empty array if the file does not exist or cannot be parsed.
    /// </summary>
    public static async Task<JsonArray> LoadAsync(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            var json = await File.ReadAllTextAsync(path, Encoding.UTF8);
            return JsonNode.Parse(json)?.AsArray() ?? [];
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PropositionStore] Load error: {ex.Message}");
            return [];
        }
    }

    // ── Save ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Persists <paramref name="propositions"/> to <paramref name="path"/>.
    /// AOT-safe: uses ToJsonString(Pretty).
    /// </summary>
    public static async Task SaveAsync(string path, JsonArray propositions)
    {
        try
        {
            var json = propositions.ToJsonString(Pretty);
            await File.WriteAllTextAsync(path, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PropositionStore] Save error: {ex.Message}");
        }
    }

    // ── Merge / Deduplicate ───────────────────────────────────────────────────

    /// <summary>
    /// Merges <paramref name="incoming"/> propositions into <paramref name="existing"/>,
    /// deduplicating by normalised text (lowercased, punctuation stripped).
    ///
    /// When a duplicate is found, the newer proposition replaces the older one.
    /// AOT-safe: explicit foreach loops, no LINQ lambdas, string operations only.
    /// </summary>
    public static JsonArray Merge(JsonArray existing, JsonArray incoming)
    {
        // Build index of existing propositions by normalised text
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new JsonArray();

        foreach (var node in existing)
        {
            var text = node?["text"]?.GetValue<string>() ?? "";
            var key  = Normalise(text);
            if (!string.IsNullOrEmpty(key) && !index.ContainsKey(key))
                index[key] = result.Count;
            result.Add(node?.DeepClone());
        }

        // Merge incoming: replace on duplicate, append otherwise
        foreach (var node in incoming)
        {
            var text = node?["text"]?.GetValue<string>() ?? "";
            var key  = Normalise(text);

            if (string.IsNullOrEmpty(key)) continue;

            if (index.TryGetValue(key, out var existingIdx))
            {
                // Replace with newer proposition
                result[existingIdx] = node?.DeepClone();
            }
            else
            {
                index[key] = result.Count;
                result.Add(node?.DeepClone());
            }
        }

        return result;
    }

    // ── Filter ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns propositions whose <c>entity</c> field matches
    /// <paramref name="entity"/> (case-insensitive).
    ///
    /// Falls back to returning all propositions when:
    /// - entity is "general" or empty
    /// - fewer than 5 propositions match
    ///
    /// AOT-safe: explicit foreach, string comparison only.
    /// </summary>
    public static JsonArray FilterByEntity(JsonArray propositions, string entity)
    {
        if (string.IsNullOrWhiteSpace(entity) ||
            entity.Equals("general", StringComparison.OrdinalIgnoreCase))
            return propositions;

        var filtered = new JsonArray();
        foreach (var node in propositions)
        {
            var e = node?["entity"]?.GetValue<string>() ?? "";
            if (e.Equals(entity, StringComparison.OrdinalIgnoreCase))
                filtered.Add(node?.DeepClone());
        }

        // Fewer than 5 matches — not specific enough, return everything
        return filtered.Count >= 5 ? filtered : propositions;
    }

    // ── Context injection ─────────────────────────────────────────────────────

    /// <summary>
    /// Serialises <paramref name="propositions"/> into a plain string suitable
    /// for injection as a system message before the user's prompt.
    ///
    /// Format:
    ///   Relevant facts from memory:
    ///   - [2026-06-03] (api_key) The API key is stored in the .env file.
    ///   - [2026-06-05] (user) The user prefers Python for scripting tasks.
    /// </summary>
    public static string ToContextBlock(JsonArray propositions)
    {
        if (propositions.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("Relevant facts from memory:");

        foreach (var node in propositions)
        {
            var date   = node?["date"]?.GetValue<string>()   ?? "";
            var entity = node?["entity"]?.GetValue<string>() ?? "";
            var text   = node?["text"]?.GetValue<string>()   ?? "";

            if (string.IsNullOrWhiteSpace(text)) continue;

            sb.Append("- ");
            if (!string.IsNullOrEmpty(date))   sb.Append($"[{date}] ");
            if (!string.IsNullOrEmpty(entity)) sb.Append($"({entity}) ");
            sb.AppendLine(text);
        }

        return sb.ToString().TrimEnd();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Normalises a proposition text for deduplication:
    /// lowercased, with all punctuation and whitespace collapsed to single spaces.
    /// AOT-safe: manual character loop, no Regex.
    /// </summary>
    private static string Normalise(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var sb = new StringBuilder(text.Length);
        var prevSpace = true;

        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                prevSpace = false;
            }
            else if (!prevSpace)
            {
                sb.Append(' ');
                prevSpace = true;
            }
        }

        return sb.ToString().TrimEnd();
    }
}
