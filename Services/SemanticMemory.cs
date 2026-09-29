using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsAgentUI.Services;

public class SemanticMemory
{
    public record SemanticEntry(
       
        string Id,             // Guid string
        
        string Type,           // "error_pattern" | "solution"
        
        string Summary,        // human-readable lesson
        
        string[] Tags,           // ["permission", "file-ops"]
        
        DateTime CreatedAt,      // when the lesson was learned
        
        Dictionary<string, string>? Context = null  // extra kv metadata
    );


    private List<SemanticEntry> _patterns = new();
    public int Count => _patterns.Count;


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
            Context: context
        ));
    }


    // Keyword search across summary text and tags.
    public List<SemanticEntry> Search(string keyword, int limit = 3) =>
        _patterns
            .Where(p =>
                p.Summary.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                p.Tags.Any(t => t.Contains(keyword, StringComparison.OrdinalIgnoreCase)))

            .OrderByDescending(p => p.CreatedAt)
                .Take(limit)
                .ToList();


    public async Task SaveAsync(string path)
    {
        var arr = JsonNode.Parse("[]")!.AsArray();

        foreach (var p in _patterns)
        {
            // Build tags array manually — no JsonSerializer.Serialize
            var tagsArr = new JsonArray();
            foreach (var tag in p.Tags)
                tagsArr.Add(JsonValue.Create(tag));

            // Build context object manually
            JsonObject? ctxObj = null;
            if (p.Context != null)
            {
                ctxObj = new JsonObject();
                foreach (var kv in p.Context)
                    ctxObj[kv.Key] = JsonValue.Create(kv.Value);
            }

            arr.Add(new JsonObject
            {
                ["id"] = p.Id,
                ["type"] = p.Type,
                ["summary"] = p.Summary,
                ["tags"] = tagsArr,
                ["createdAt"] = p.CreatedAt.ToString("O"),
                ["context"] = ctxObj
            });
        }

        await File.WriteAllTextAsync(path,
            arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }


    public async Task LoadAsync(string path)
    {
        if (!File.Exists(path)) return;
        var arr = JsonNode.Parse(await File.ReadAllTextAsync(path))?.AsArray();
        if (arr is null) return;
        _patterns.Clear();
        foreach (var item in arr)
        {
            var tags = item?["tags"]?.AsArray()
                .Select(t => t?.GetValue<string>() ?? "")
                .ToArray() ?? Array.Empty<string>();
            var ctx = item?["context"]?.AsObject()
                ?.ToDictionary(kv => kv.Key, kv => kv.Value?.GetValue<string>() ?? "");
            _patterns.Add(new SemanticEntry(
                Id: item?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString(),
                Type: item?["type"]?.GetValue<string>() ?? "",
                Summary: item?["summary"]?.GetValue<string>() ?? "",
                Tags: tags,
                CreatedAt: DateTime.Parse(item?["createdAt"]?.GetValue<string>()
                               ?? DateTime.UtcNow.ToString("O")),
                Context: ctx?.Count > 0 ? ctx : null
            ));
        }
    }

}