using System.Text.Json.Nodes;

namespace CsAgent.Core.Llm;

/// <summary>One model of the endpoint's /models list.</summary>
public sealed record CatalogEntry(string Id, string Type, string Status, IReadOnlyList<string> Aliases);

/// <summary>
/// The models offered by the endpoint (GET /models, the same call as the list_models tool).
/// Used by <see cref="ModelRouter"/> to check that the chat model exists and is not down before a
/// run is sent to it. Every failure means "no information": the router then trusts the configuration.
/// AOT-safe: JsonNode traversal only.
/// </summary>
public static class ModelCatalog
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private static readonly object Gate = new();
    private static (string Endpoint, DateTime At, IReadOnlyList<CatalogEntry> Models)? _cache;

    /// <summary>Parses an OpenAI/Albert /models body. Returns an empty list when it cannot be read.</summary>
    public static IReadOnlyList<CatalogEntry> Parse(string json)
    {
        var result = new List<CatalogEntry>();
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch { return result; }

        if (root?["data"] is not JsonArray data) return result;

        foreach (var m in data)
        {
            var id = Str(m?["id"]);
            if (id.Length == 0) continue;

            var aliases = new List<string>();
            if (m?["aliases"] is JsonArray arr)
                foreach (var a in arr)
                {
                    var alias = Str(a);
                    if (alias.Length > 0) aliases.Add(alias);
                }

            result.Add(new CatalogEntry(id, Str(m?["type"]), Str(m?["status"]), aliases));
        }
        return result;
    }

    /// <summary>The entry whose id or alias is <paramref name="model"/> (case-insensitive), or null.</summary>
    public static CatalogEntry? Find(IReadOnlyList<CatalogEntry> catalog, string model)
    {
        foreach (var e in catalog)
        {
            if (e.Id.Equals(model, StringComparison.OrdinalIgnoreCase)) return e;
            foreach (var a in e.Aliases)
                if (a.Equals(model, StringComparison.OrdinalIgnoreCase)) return e;
        }
        return null;
    }

    /// <summary>
    /// True when <paramref name="model"/> can answer a chat: known to the endpoint, a text or
    /// image-text model (not an embedding, reranker or speech model) and not reported "unavailable".
    /// A null catalog (no information) counts as usable.
    /// </summary>
    public static bool IsUsable(IReadOnlyList<CatalogEntry>? catalog, string model, out string why)
    {
        why = "";
        if (catalog is null || catalog.Count == 0) return true;

        var e = Find(catalog, model);
        if (e is null) { why = "not in the endpoint's model list"; return false; }
        if (e.Status.Equals("unavailable", StringComparison.OrdinalIgnoreCase)) { why = "reported unavailable"; return false; }
        if (e.Type.Length > 0 && e.Type is not ("text-generation" or "image-text-to-text"))
        { why = $"type '{e.Type}' cannot chat"; return false; }
        return true;
    }

    /// <summary>The catalog of <paramref name="endpoint"/>, cached for a few minutes. Null when it cannot be fetched.</summary>
    public static async Task<IReadOnlyList<CatalogEntry>?> GetCachedAsync(string endpoint, string apiKey)
    {
        lock (Gate)
        {
            if (_cache is { } c && c.Endpoint == endpoint && DateTime.UtcNow - c.At < CacheFor)
                return c.Models;
        }

        var models = await FetchAsync(endpoint, apiKey);
        if (models is { Count: > 0 })
            lock (Gate) _cache = (endpoint, DateTime.UtcNow, models);
        return models;
    }

    /// <summary>Forgets the cached catalog (tests).</summary>
    public static void ClearCache() { lock (Gate) _cache = null; }

    private static async Task<IReadOnlyList<CatalogEntry>?> FetchAsync(string endpoint, string apiKey)
    {
        try
        {
            using var client = new HttpClient { Timeout = FetchTimeout };
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.TrimEnd('/') + "/models");
            // AOT-safe: TryAddWithoutValidation avoids any header-type reflection
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            return Parse(await response.Content.ReadAsStringAsync());
        }
        catch
        {
            return null; // offline, timeout, bad TLS...: no information
        }
    }

    private static string Str(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}
