using CsAgent.Shared;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsAgent.Core.Memory;

public static class MemoryStore
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static async Task<JsonArray> LoadAsync(string path)
    {
        // corrupt file -> quarantined as "<path>.bad" (never silently overwritten)
        return await AtomicFile.ReadJsonArrayAsync(path) ?? [];
    }

    public static async Task SaveAsync(string path, JsonArray messages)
    {
        // Before persisting, strip image_url blocks from every multimodal message.
        // Images are sent to the API once (in-memory) but must never be saved to
        // disk — they would be re-sent on every subsequent prompt, repeating the
        // image description indefinitely and bloating the memory file.
        //
        // For each message whose content is a JsonArray, we rebuild the content
        // as a plain string joining all text blocks, then replace the array with
        // that string. This preserves the conversation context (the text prompt)
        // while dropping the base64 image data.
        //
        // AOT-safe: pure JsonNode traversal, no reflection.
        var stripped = StripImages(messages);

        // Injected context ([MEMORY], [SESSION CONTEXT]) is rebuilt at every step:
        // it must not be saved into the conversation file.
        var persisted = new JsonArray();
        foreach (var m in stripped)
        {
            var text = m?["content"] is JsonValue v && v.TryGetValue<string>(out var t) ? t : "";
            if (text.StartsWith("[MEMORY]", StringComparison.Ordinal)
                || text.StartsWith("[SESSION CONTEXT]", StringComparison.Ordinal)) continue;
            persisted.Add(m?.DeepClone());
        }

        var json = persisted.ToJsonString(Pretty);
        await AtomicFile.WriteAllTextAsync(path, json, Encoding.UTF8);
    }

    /// <summary>
    /// Returns a copy of <paramref name="messages"/> where every multimodal
    /// message (content = JsonArray with image_url blocks) is reduced to a
    /// plain-string message containing only the concatenated text parts.
    /// </summary>
    private static JsonArray StripImages(JsonArray messages)
    {
        var result = new JsonArray();

        foreach (var node in messages)
        {
            // Not a message object — copy as-is
            if (node is not JsonObject msg)
            {
                result.Add(node?.DeepClone());
                continue;
            }

            var content = msg["content"];

            // Plain string content — no image, copy as-is
            if (content is not JsonArray blocks)
            {
                result.Add(msg.DeepClone());
                continue;
            }

            // Multimodal content: collect text parts, discard image_url blocks
            var sb = new System.Text.StringBuilder();
            foreach (var block in blocks)
            {
                if (block?["type"]?.GetValue<string>() == "text")
                {
                    var text = block["text"]?.GetValue<string>() ?? "";
                    if (sb.Length > 0 && text.Length > 0) sb.Append(' ');
                    sb.Append(text);
                }
                // image_url blocks are silently dropped
            }

            // Rebuild as a plain message with just the text
            var role = msg["role"]?.GetValue<string>() ?? "user";
            var stripped = new JsonObject();
            stripped.Add("role", JsonValue.Create(role));
            stripped.Add("content", JsonValue.Create(sb.ToString()));
            result.Add(stripped);
        }

        return result;
    }
}