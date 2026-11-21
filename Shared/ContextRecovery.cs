using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CsAgent.Shared;

/// <summary>
/// Recovery from the server's "maximum context length" 400. The character budget (<c>--context-chars</c>) only
/// counts message text, so images, the arguments of tool calls, tool schemas and memory blocks can still push a
/// request over the model window. When that happens the history is shrunk in place and the request is sent again.
/// </summary>
public static class ContextRecovery
{
    public const int MaxRecoveries = 2;
    private const int OldToolCallArgChars = 600;

    private static readonly Regex Limit = new(@"maximum context length is\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Actual = new(@"(?:at least|contains|requested|resulted in)\s+(\d+)\s+(?:input\s+)?tokens", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True for a 400 body that says the prompt is longer than the model's window (vLLM, OpenAI, Albert).</summary>
    public static bool IsContextOverflow(string body, out int limitTokens, out int actualTokens)
    {
        limitTokens = actualTokens = 0;
        if (string.IsNullOrEmpty(body)) return false;
        var b = body.ToLowerInvariant();
        bool hit = b.Contains("maximum context length") || b.Contains("context_length_exceeded")
                || b.Contains("context window") || b.Contains("exceeds the limit of")
                || (b.Contains("input_tokens") && b.Contains("exceeds"));
        if (!hit) return false;
        if (Limit.Match(body) is { Success: true } l) int.TryParse(l.Groups[1].Value, out limitTokens);
        if (Actual.Match(body) is { Success: true } a) int.TryParse(a.Groups[1].Value, out actualTokens);
        return true;
    }

    /// <summary>Size of everything that is sent for the messages: text, tool-call arguments and images.</summary>
    public static int FullLength(JsonArray msgs) => msgs.Sum(m => m?.ToJsonString().Length ?? 0);

    /// <summary>
    /// Shrinks <paramref name="msgs"/> in place. <paramref name="attempt"/> is 1 or 2 (2 is harsher).
    /// Steps: images except the newest become a text note; old tool results and old tool-call arguments are cut;
    /// then the oldest messages are dropped until the target size is reached. The system message, the newest
    /// messages and the first user request are kept. Returns the number of characters removed.
    /// </summary>
    public static int Shrink(JsonArray msgs, int limitTokens, int actualTokens, int attempt)
    {
        int before = FullLength(msgs);

        // Images: first pass keeps only the newest one, at low detail; second pass drops them all.
        StubOldImages(msgs, keepNewest: attempt == 1 ? 1 : 0);
        if (attempt == 1) LowerImageDetail(msgs);
        JsonHelpers.CompactToolResults(msgs, keepRecent: attempt == 1 ? 2 : 1, maxChars: attempt == 1 ? 1_500 : 400);
        CompactOldToolCallArguments(msgs, keepRecent: attempt == 1 ? 2 : 1);

        // Target: scale by how far over the window the request was; without numbers, 60% then 35%.
        double ratio = limitTokens > 0 && actualTokens > limitTokens
            ? limitTokens * (attempt == 1 ? 0.65 : 0.4) / actualTokens
            : (attempt == 1 ? 0.6 : 0.35);
        int target = Math.Max(8_000, (int)(before * Math.Min(ratio, 0.9)));

        int total = FullLength(msgs);
        int firstUser = IndexOfFirstUser(msgs);
        int i = 1;
        while (total > target && msgs.Count > 3 && i < msgs.Count - 2)
        {
            if (i == firstUser) { i++; continue; }          // the original request stays
            total -= msgs[i]?.ToJsonString().Length ?? 0;
            msgs.RemoveAt(i);
            if (firstUser > i) firstUser--;
        }
        JsonHelpers.RepairToolPairs(msgs);
        return before - FullLength(msgs);
    }

    private static int IndexOfFirstUser(JsonArray msgs)
    {
        for (int i = 1; i < msgs.Count; i++)
            if (msgs[i]?["role"]?.GetValue<string>() == "user") return i;
        return -1;
    }

    public const string ImageNote = "[image removed from context to fit the model window]";

    /// <summary>
    /// Every image except those of the newest <paramref name="keepNewest"/> multimodal messages becomes a short text
    /// note. A message left without any image gets a plain string <c>content</c> again, because a text-only model
    /// (the router may go back to the code model once the image is gone) can reject the array form.
    /// Returns the number of images replaced.
    /// </summary>
    public static int StubOldImages(JsonArray msgs, int keepNewest = 1)
    {
        var keep = new HashSet<int>();
        for (int i = msgs.Count - 1; i >= 1 && keep.Count < keepNewest; i--)
            if (HasImage(msgs[i])) keep.Add(i);

        int stubbed = 0;
        for (int i = 1; i < msgs.Count; i++)
        {
            if (keep.Contains(i) || msgs[i] is not JsonObject msg || msg["content"] is not JsonArray blocks) continue;
            bool any = false;
            for (int b = 0; b < blocks.Count; b++)
            {
                if (blocks[b]?["type"]?.GetValue<string>() != "image_url") continue;
                blocks[b] = new JsonObject { ["type"] = "text", ["text"] = ImageNote };
                stubbed++; any = true;
            }
            if (any && !HasImage(msg))
                msg["content"] = string.Join("\n", blocks
                    .Select(x => x?["text"] is JsonValue t && t.TryGetValue<string>(out var str) ? str : "")
                    .Where(x => x.Length > 0));
        }
        return stubbed;
    }

    /// <summary>Asks for the cheap rendering of the remaining images (<c>detail: low</c>, the OpenAI-compatible field).</summary>
    internal static int LowerImageDetail(JsonArray msgs)
    {
        int n = 0;
        foreach (var m in msgs)
            if (m?["content"] is JsonArray blocks)
                foreach (var b in blocks)
                    if (b?["type"]?.GetValue<string>() == "image_url" && b["image_url"] is JsonObject o && o["detail"] is null)
                    { o["detail"] = "low"; n++; }
        return n;
    }

    private static bool HasImage(JsonNode? m) =>
        m?["content"] is JsonArray blocks && blocks.Any(b => b?["type"]?.GetValue<string>() == "image_url");

    /// <summary>Long arguments (write_file content, ...) of older assistant tool calls are cut; the call ids and names stay.</summary>
    internal static int CompactOldToolCallArguments(JsonArray msgs, int keepRecent)
    {
        int seen = 0, changed = 0;
        for (int i = msgs.Count - 1; i >= 1; i--)
        {
            if (msgs[i]?["tool_calls"] is not JsonArray calls) continue;
            if (++seen <= keepRecent) continue;
            foreach (var call in calls)
            {
                var fn = call?["function"];
                if (fn?["arguments"] is not JsonValue v || !v.TryGetValue<string>(out var args) || args.Length <= OldToolCallArgChars) continue;
                // Arguments must stay valid JSON: keep the keys, shorten only long string values.
                fn["arguments"] = JsonValue.Create(ShortenArgs(args));
                changed++;
            }
        }
        return changed;
    }

    private static string ShortenArgs(string args)
    {
        try
        {
            if (JsonNode.Parse(args) is JsonObject o)
            {
                foreach (var key in o.Select(kv => kv.Key).ToList())
                    if (o[key] is JsonValue s && s.TryGetValue<string>(out var str) && str.Length > 200)
                        o[key] = $"[{str.Length:N0} characters left out of context]";
                return o.ToJsonString();
            }
        }
        catch (System.Text.Json.JsonException) { }
        return "{}";
    }
}
