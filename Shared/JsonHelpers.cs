using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsAgent.Shared;

/// <summary>
/// Shared JSON helpers — AOT-safe, no trimming warnings.
/// </summary>
public static class JsonHelpers
{
    /// <summary>
    /// Create a chat message JSON object with role and content.
    /// AOT-safe: uses JsonValue.Create instead of implicit conversions.
    /// </summary>
    public static JsonObject Message(string role, string content)
    {
        var obj = new JsonObject();
        obj.Add("role", JsonValue.Create(role));
        obj.Add("content", JsonValue.Create(content));
        return obj;
    }

    /// <summary>
    /// Create a multimodal chat message that carries both a text prompt and an
    /// inline Base64-encoded image, using the OpenAI image_url content format.
    /// AOT-safe: built entirely with JsonNode / JsonObject / JsonArray — no
    /// reflection or JsonSerializer involved.
    /// </summary>
    /// <param name="role">Message role, typically "user".</param>
    /// <param name="text">The text part of the prompt.</param>
    /// <param name="base64Image">Raw Base64 string of the image bytes.</param>
    /// <param name="mimeType">MIME type, e.g. "image/png" or "image/jpeg".</param>
    public static JsonObject MultimodalMessage(string role, string text, string base64Image, string mimeType)
    {
        // Text part
        var textPart = new JsonObject();
        textPart.Add("type", JsonValue.Create("text"));
        textPart.Add("text", JsonValue.Create(text));

        // image_url part
        var imageUrl = new JsonObject();
        imageUrl.Add("url", JsonValue.Create($"data:{mimeType};base64,{base64Image}"));

        var imagePart = new JsonObject();
        imagePart.Add("type", JsonValue.Create("image_url"));
        imagePart.Add("image_url", imageUrl);

        // content array
        var content = new JsonArray();
        content.Add(textPart);
        content.Add(imagePart);

        var msg = new JsonObject();
        msg.Add("role", JsonValue.Create(role));
        msg.Add("content", content);
        return msg;
    }

    /// <summary>
    /// Create a tool result message JSON object.
    /// </summary>
    public static JsonObject ToolResult(string callId, string content)
    {
        var obj = new JsonObject();
        obj.Add("role", JsonValue.Create("tool"));
        obj.Add("tool_call_id", JsonValue.Create(callId));
        obj.Add("content", JsonValue.Create(content));
        return obj;
    }

    /// <summary>
    /// Pretty-print a JSON string (indented).
    /// </summary>
    public static string PrettyJson(string raw)
    {
        try { return JsonNode.Parse(raw)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? raw; }
        catch { return raw; }
    }

    /// <summary>
    /// Returns true if any message in the history has a content array that
    /// contains an image_url block — meaning the conversation is multimodal
    /// and must continue on a vision-capable model.
    /// AOT-safe: uses JsonNode traversal only, no reflection.
    /// </summary>
    public static bool HistoryContainsImage(JsonArray msgs)
    {
        foreach (var msg in msgs)
        {
            var content = msg?["content"];
            if (content is not JsonArray blocks) continue;
            foreach (var block in blocks)
            {
                if (block?["type"]?.GetValue<string>() == "image_url") return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Trim conversation history to stay under the character threshold.
    /// Keeps the system message (index 0) and at least 3 messages.
    /// Multimodal messages (those containing image_url blocks) are never evicted:
    /// removing them would corrupt the conversation context while the saved file
    /// still references them, causing the API to reject subsequent requests.
    /// </summary>
    public static void TrimHistory(JsonArray msgs, int thresholdChars = 96_000)
    {
        static int Len(JsonNode? m)
        {
            var c = m?["content"];
            return c is JsonValue v ? v.GetValue<string>().Length : (c?.ToJsonString().Length ?? 0);
        }

        static bool IsMultimodal(JsonNode? m)
        {
            var c = m?["content"];
            if (c is not JsonArray blocks) return false;
            foreach (var block in blocks)
                if (block?["type"]?.GetValue<string>() == "image_url") return true;
            return false;
        }

        int total = msgs.Sum(Len);
        int i = 1; // never touch index 0 (system message)
        while (total > thresholdChars && msgs.Count > 3)
        {
            // Advance past any multimodal messages — they must not be removed
            while (i < msgs.Count && IsMultimodal(msgs[i]))
                i++;

            if (i >= msgs.Count) break; // only multimodal messages remain; stop

            total -= Len(msgs[i]);
            msgs.RemoveAt(i);
            // Don't increment i — after removal the next candidate is at the same index
        }
    }
}