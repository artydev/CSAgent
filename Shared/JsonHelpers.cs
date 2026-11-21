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

    /// <summary>Text that stands in for an image once it is no longer sent to the model.</summary>
    public const string OldImagePlaceholder = "[image omitted: sent earlier in the conversation]";

    static bool HasImageBlock(JsonNode? msg)
    {
        if (msg?["content"] is not JsonArray blocks) return false;
        foreach (var block in blocks)
            if (block?["type"]?.GetValue<string>() == "image_url") return true;
        return false;
    }

    /// <summary>
    /// True when the most recent user message carries an image. This is what makes a turn a
    /// vision turn: an image sent earlier in the conversation no longer does.
    /// </summary>
    public static bool LastUserMessageHasImage(JsonArray msgs)
    {
        for (int i = msgs.Count - 1; i >= 0; i--)
        {
            var m = msgs[i];
            if (m?["role"]?.GetValue<string>() == "user") return HasImageBlock(m);
        }
        return false;
    }

    /// <summary>
    /// Replace every image in the history by <see cref="OldImagePlaceholder"/> (the message keeps
    /// its text and becomes a plain string), so a non-vision model never receives image blocks.
    /// Returns the number of messages changed. AOT-safe: JsonNode only.
    /// </summary>
    public static int DropImages(JsonArray msgs)
    {
        int changed = 0;
        for (int i = 0; i < msgs.Count; i++)
        {
            var m = msgs[i];
            if (!HasImageBlock(m)) continue;
            var parts = new List<string>();
            foreach (var block in (JsonArray)m!["content"]!)
            {
                var type = block?["type"]?.GetValue<string>();
                if (type == "text") parts.Add(block?["text"]?.GetValue<string>() ?? "");
                else if (type == "image_url") parts.Add(OldImagePlaceholder);
            }
            m["content"] = JsonValue.Create(string.Join("\n", parts));
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// Shortens old tool results: all but the last <paramref name="keepRecent"/> tool messages are cut to
    /// <paramref name="maxChars"/> characters, followed by a line saying how much was left out. Only the text of
    /// the result changes (never the message order or the tool call ids), so the conversation stays valid.
    /// Running it again changes nothing. Returns how many results were shortened.
    /// </summary>
    public static int CompactToolResults(JsonArray msgs, int keepRecent, int maxChars)
    {
        int seen = 0, changed = 0;
        for (int i = msgs.Count - 1; i >= 1; i--)
        {
            var m = msgs[i];
            if (m?["role"]?.GetValue<string>() != "tool") continue;
            if (++seen <= keepRecent) continue;
            if (m["content"] is not JsonValue v || !v.TryGetValue<string>(out var s)) continue;
            if (s.Length <= maxChars + 200) continue;   // small, or already shortened (the note is under 200 chars)

            int cut = maxChars;
            if (cut > 0 && char.IsHighSurrogate(s[cut - 1])) cut--;
            m["content"] = JsonValue.Create(
                s[..cut] + $"\n[… {s.Length - cut:N0} of {s.Length:N0} characters left out: older tool result, run the tool again if you need it]");
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// Trim conversation history to stay under the character threshold.
    /// Keeps the system message (index 0) and at least 3 messages.
    /// Multimodal messages (those containing image_url blocks) are never evicted here (older images are first replaced by a text note, see ContextRecovery.StubOldImages):
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

        // Removing messages one by one can cut an assistant(tool_calls) turn away from its tool results.
        // Strict servers (vLLM templates: mistral, gemma, ...) reject that with
        // "Unexpected role 'tool' after role 'user'", so the pairs are repaired.
        RepairToolPairs(msgs);
    }

    private static string RoleOf(JsonNode? m) =>
        m?["role"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static string? ToolCallIdOf(JsonNode? m) =>
        m?["tool_call_id"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static List<string> CallIdsOf(JsonNode? m)
    {
        var ids = new List<string>();
        if (m?["tool_calls"] is JsonArray calls)
            foreach (var c in calls)
                if (c?["id"] is JsonValue v && v.TryGetValue<string>(out var s)) ids.Add(s);
        return ids;
    }

    /// <summary>
    /// Makes the tool-call structure valid for every server: a <c>tool</c> message must directly follow the
    /// assistant message that made the call (or another <c>tool</c> message of the same call group), and an
    /// assistant message with <c>tool_calls</c> must be followed by a result for each call.
    /// Orphan tool results are removed; an assistant turn whose results are missing is removed with the
    /// results that remain. Returns how many messages were removed. A valid conversation is not changed.
    /// </summary>
    public static int RepairToolPairs(JsonArray msgs)
    {
        int removed = 0;

        // 1) tool results with no matching assistant tool call just before them
        for (int i = msgs.Count - 1; i >= 0; i--)
        {
            if (RoleOf(msgs[i]) != "tool") continue;
            int j = i - 1;
            while (j >= 0 && RoleOf(msgs[j]) == "tool") j--;
            var id = ToolCallIdOf(msgs[i]);
            bool valid = j >= 0 && RoleOf(msgs[j]) == "assistant" && id != null && CallIdsOf(msgs[j]).Contains(id);
            if (!valid) { msgs.RemoveAt(i); removed++; }
        }

        // 2) assistant tool calls whose results are missing (the turn is dropped as a whole)
        for (int i = msgs.Count - 1; i >= 0; i--)
        {
            if (RoleOf(msgs[i]) != "assistant") continue;
            var ids = CallIdsOf(msgs[i]);
            if (ids.Count == 0) continue;

            var answered = new HashSet<string>(StringComparer.Ordinal);
            int k = i + 1;
            while (k < msgs.Count && RoleOf(msgs[k]) == "tool")
            {
                if (ToolCallIdOf(msgs[k]) is { } id) answered.Add(id);
                k++;
            }
            if (ids.All(answered.Contains)) continue;

            for (int r = k - 1; r >= i; r--) { msgs.RemoveAt(r); removed++; }
        }
        return removed;
    }
}