using System.Text;
using System.Text.Json.Nodes;
using CsAgent.Core.Llm;
using CsAgent.Shared;

namespace CsAgent.Core.Memory;

/// <summary>
/// End-of-session proposition extractor.
///
/// Makes a single LLM call at the end of a session and returns a JsonArray
/// of atomic propositions extracted from the raw conversation history.
///
/// AOT-safe: uses LlmClient directly (no new HttpClient), builds the request
/// with JsonNode/JsonObject/JsonArray, parses the response with JsonNode.Parse.
/// </summary>
public static class PropMemIngester
{
    // Maximum characters of raw conversation to send to the extraction call.
    // Large sessions are truncated from the front to stay within token limits.
    private const int MaxInputChars = 24_000;

    /// <summary>
    /// Extracts atomic propositions from <paramref name="messages"/> using
    /// a dedicated LLM call on <paramref name="client"/>.
    ///
    /// Returns an empty array on any failure — ingestion errors must never
    /// interrupt the main agent flow.
    /// </summary>
    public static async Task<JsonArray> ExtractAsync(
        JsonArray messages,
        string sessionDate,
        LlmClient client,
        CancellationToken ct = default)
    {
        var conversation = BuildConversationText(messages);
        if (string.IsNullOrWhiteSpace(conversation))
            return [];

        // Truncate to avoid sending huge sessions
        if (conversation.Length > MaxInputChars)
            conversation = "...[truncated]...\n" + conversation[^MaxInputChars..];

        var prompt = BuildExtractionPrompt(conversation, sessionDate);

        var extractionMessages = new JsonArray
        {
            JsonHelpers.Message("user", prompt)
        };

        try
        {
            // No tools — pure text extraction
            var response = await client.CompleteChatAsync(extractionMessages, null, ct);
            var raw = response["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
            return ParsePropositions(raw, sessionDate);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PropMemIngester] Extraction error: {ex.Message}");
            return [];
        }
    }

    // ── Prompt ────────────────────────────────────────────────────────────────

    private static string BuildExtractionPrompt(string conversation, string sessionDate) => $"""
        Extract every atomic fact from the conversation below.
        Return ONLY a JSON array. No prose, no markdown fences, no explanation.

        Rules:
        - One object per fact.
        - Each object has exactly three string fields:
            "entity" : the single entity this fact is about.
                       Use a lowercase, underscore_separated identifier.
                       Examples: "api_key", "project_name", "user", "database", "csagent".
                       Use "general" when no specific entity applies.
            "text"   : one self-contained sentence stating the fact.
                       Never use pronouns — always repeat the entity name explicitly.
                       Example: "The API key is stored in the .env file at the project root."
            "date"   : the resolved date in YYYY-MM-DD format.
                       Convert relative dates ("last week", "yesterday", "two days ago")
                       to absolute dates using the session date provided below.
                       Use the session date when no date is mentioned.
        - Omit filler: greetings, tool call logs, acknowledgements, progress messages.
        - Omit transient information: file contents shown to the agent, command outputs.
        - Omit facts already implied by the code itself (e.g. "the function is named X").
        - One fact per entry — never combine two facts in one sentence.
        - Aim for 5–20 propositions per session. Be selective — quality over quantity.

        Session date: {sessionDate}

        Conversation:
        {conversation}
        """;

    // ── Conversation text builder ──────────────────────────────────────────────

    /// <summary>
    /// Flattens the message array into a plain text representation,
    /// skipping system messages, tool call results, and assistant tool_calls blocks.
    /// AOT-safe: explicit foreach, JsonNode traversal.
    /// </summary>
    private static string BuildConversationText(JsonArray messages)
    {
        var sb = new StringBuilder();

        foreach (var node in messages)
        {
            var role    = node?["role"]?.GetValue<string>() ?? "";
            var content = node?["content"];

            // Skip system messages and tool results — not informative for extraction
            if (role is "system" or "tool") continue;

            // Skip messages with no content (pure tool_calls messages)
            if (content is null) continue;

            // Plain string content
            if (content is JsonValue v)
            {
                var text = v.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(text))
                    sb.AppendLine($"{role}: {text}");
                continue;
            }

            // Multimodal content array — extract text parts only
            if (content is JsonArray blocks)
            {
                foreach (var block in blocks)
                {
                    if (block?["type"]?.GetValue<string>() == "text")
                    {
                        var text = block["text"]?.GetValue<string>() ?? "";
                        if (!string.IsNullOrWhiteSpace(text))
                            sb.AppendLine($"{role}: {text}");
                    }
                }
            }
        }

        return sb.ToString().Trim();
    }

    // ── Response parser ───────────────────────────────────────────────────────

    /// <summary>
    /// Parses the LLM response into a JsonArray of proposition objects.
    ///
    /// The LLM is instructed to return raw JSON, but may wrap it in markdown
    /// fences or add preamble text. We strip common wrappers defensively.
    /// AOT-safe: JsonNode.Parse only.
    /// </summary>
    private static JsonArray ParsePropositions(string raw, string sessionDate)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        // Strip markdown fences if present
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            var lastFence    = trimmed.LastIndexOf("```");
            if (firstNewline > 0 && lastFence > firstNewline)
                trimmed = trimmed[(firstNewline + 1)..lastFence].Trim();
        }

        // Find the JSON array boundaries defensively
        var start = trimmed.IndexOf('[');
        var end   = trimmed.LastIndexOf(']');
        if (start < 0 || end <= start) return [];

        trimmed = trimmed[start..(end + 1)];

        JsonNode? parsed;
        try { parsed = JsonNode.Parse(trimmed); }
        catch { return []; }

        if (parsed is not JsonArray arr) return [];

        // Validate and sanitise each entry
        var result = new JsonArray();
        foreach (var node in arr)
        {
            if (node is not JsonObject obj) continue;

            var entity = obj["entity"]?.GetValue<string>() ?? "general";
            var text   = obj["text"]?.GetValue<string>()   ?? "";
            var date   = obj["date"]?.GetValue<string>()   ?? sessionDate;

            if (string.IsNullOrWhiteSpace(text)) continue;

            // Sanitise entity: lowercase, replace spaces with underscores
            entity = entity.ToLowerInvariant().Replace(' ', '_').Trim('_');
            if (string.IsNullOrEmpty(entity)) entity = "general";

            // Validate date format — fall back to session date
            if (!IsValidDate(date)) date = sessionDate;

            var prop = new JsonObject();
            prop.Add("entity", JsonValue.Create(entity));
            prop.Add("text",   JsonValue.Create(text.Trim()));
            prop.Add("date",   JsonValue.Create(date));
            result.Add(prop);
        }

        return result;
    }

    private static bool IsValidDate(string date)
    {
        if (date.Length != 10) return false;
        if (date[4] != '-' || date[7] != '-') return false;
        return int.TryParse(date.AsSpan(0, 4), out _) &&
               int.TryParse(date.AsSpan(5, 2), out _) &&
               int.TryParse(date.AsSpan(8, 2), out _);
    }
}
