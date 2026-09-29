using System.Text.Json.Nodes;
using CsAgent.Core.Llm;
using CsAgent.Shared;

namespace CsAgent.Core.Memory;

/// <summary>
/// Query-time proposition retriever.
///
/// Makes a single lightweight LLM call to identify the entity a user prompt
/// is about, then filters the proposition store to only the relevant facts.
/// The filtered facts are injected as a system message immediately before
/// the user's turn in the messages array.
///
/// AOT-safe: JsonNode/JsonObject/JsonValue only, no reflection.
/// </summary>
public static class PropMemRetriever
{
    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Classifies the <paramref name="userPrompt"/> to identify its primary entity,
    /// loads and filters propositions, and injects a system context block into
    /// <paramref name="messages"/> immediately before the last message (the user turn).
    ///
    /// Does nothing if the proposition store is empty or classification fails.
    /// Errors are swallowed — retrieval must never interrupt the main agent flow.
    /// </summary>
    public static async Task InjectAsync(
        JsonArray messages,
        string userPrompt,
        string propositionFile,
        LlmClient client,
        CancellationToken ct = default)
    {
        try
        {
            // 1. Skip retrieval for conversational inputs — statements, preferences,
            //    and simple questions don't benefit from proposition injection and
            //    the entity classification LLM call would add unnecessary latency.
            if (IsConversational(userPrompt)) return;

            // 2. Load the proposition store
            var propositions = await PropositionStore.LoadAsync(propositionFile);
            if (propositions.Count == 0) return;

            // 3. Classify the query entity
            var entity = await ClassifyEntityAsync(userPrompt, client, ct);

            // 4. Filter to relevant propositions
            var relevant = PropositionStore.FilterByEntity(propositions, entity);
            if (relevant.Count == 0) return;

            // 5. Build the context block
            var contextBlock = PropositionStore.ToContextBlock(relevant);
            if (string.IsNullOrWhiteSpace(contextBlock)) return;

            // 6. Inject as a system message just before the last message (user turn)
            //    Insert at Count-1 so it sits directly before the current user prompt.
            var insertAt = Math.Max(1, messages.Count - 1);
            messages.Insert(insertAt, JsonHelpers.Message("system", contextBlock));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PropMemRetriever] Inject error: {ex.Message}");
        }
    }

    // ── Entity classification ─────────────────────────────────────────────────

    /// <summary>
    /// Makes a lightweight LLM call to identify the primary entity the
    /// <paramref name="userPrompt"/> is about.
    ///
    /// Returns a lowercase identifier string (e.g. "api_key", "csagent", "user")
    /// or "general" as a fallback.
    ///
    /// AOT-safe: JsonNode traversal only.
    /// </summary>
    public static async Task<string> ClassifyEntityAsync(
        string userPrompt,
        LlmClient client,
        CancellationToken ct = default)
    {
        var prompt = BuildClassificationPrompt(userPrompt);
        var classificationMessages = new JsonArray
        {
            JsonHelpers.Message("user", prompt)
        };

        try
        {
            var response = await client.CompleteChatAsync(classificationMessages, null, ct);
            var raw = response["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
            return SanitiseEntity(raw);
        }
        catch
        {
            // Classification failure is not fatal — fall back to "general"
            return "general";
        }
    }

    // ── Prompts ───────────────────────────────────────────────────────────────

    private static string BuildClassificationPrompt(string userPrompt) => $"""
        Identify the primary entity this question or instruction is about.
        Reply with a SINGLE lowercase word or short snake_case identifier only.
        No punctuation, no explanation, no surrounding text — just the identifier.

        Rules:
        - Person or user → their name or "user"
        - File or path   → the filename without extension (e.g. "config", "main")
        - Project        → the project name in snake_case (e.g. "csagent", "my_app")
        - Tool or system → the tool name (e.g. "api_key", "database", "docker")
        - General / unclear → general

        Question or instruction:
        {userPrompt}
        """;

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the prompt is conversational — a statement, preference,
    /// acknowledgement, or simple factual question — and does NOT benefit from
    /// proposition injection. Mirrors the §0 classification in the system prompt.
    ///
    /// Uses keyword heuristics only: no LLM call, no allocation beyond the
    /// initial ToLowerInvariant. AOT-safe.
    /// </summary>
    private static bool IsConversational(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return true;

        // Short one-liners that are clearly statements or greetings
        var trimmed = prompt.Trim();
        if (trimmed.Length < 12) return true;

        var lower = trimmed.ToLowerInvariant();

        // Explicit preference / statement indicators
        if (lower.StartsWith("i prefer ")   ||
            lower.StartsWith("i like ")     ||
            lower.StartsWith("i use ")      ||
            lower.StartsWith("i always ")   ||
            lower.StartsWith("we use ")     ||
            lower.StartsWith("we prefer ")  ||
            lower.StartsWith("from now on") ||
            lower.StartsWith("note that ")  ||
            lower.StartsWith("just so you know") ||
            lower.StartsWith("remember that"))
            return true;

        // Simple factual questions — answered from context, no fact lookup needed
        if ((lower.StartsWith("what ") ||
             lower.StartsWith("which ") ||
             lower.StartsWith("who ") ||
             lower.StartsWith("when ") ||
             lower.StartsWith("where ") ||
             lower.StartsWith("how ") ||
             lower.StartsWith("why ") ||
             lower.StartsWith("is ") ||
             lower.StartsWith("are ") ||
             lower.StartsWith("can ") ||
             lower.StartsWith("do ") ||
             lower.StartsWith("does "))
            && !lower.Contains("fix") && !lower.Contains("create")
            && !lower.Contains("write") && !lower.Contains("implement")
            && !lower.Contains("add") && !lower.Contains("refactor")
            && !lower.Contains("update") && !lower.Contains("change")
            && !lower.Contains("delete") && !lower.Contains("remove"))
            return true;

        // Greetings and acknowledgements
        if (lower is "ok" or "okay" or "thanks" or "thank you" or "got it"
                  or "sure" or "yes" or "no" or "hello" or "hi" or "bye"
                  or "exit" or "quit")
            return true;

        return false;
    }

    /// <summary>
    /// Cleans up the raw entity string returned by the LLM:
    /// lowercase, underscores for spaces, trim punctuation.
    /// Falls back to "general" if the result is empty or too long.
    /// AOT-safe: no Regex, manual character operations only.
    /// </summary>
    private static string SanitiseEntity(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "general";

        // Take only the first line (model may add explanation despite instructions)
        var firstLine = raw.Trim();
        var newline = firstLine.IndexOf('\n');
        if (newline > 0) firstLine = firstLine[..newline].Trim();

        // Convert to lowercase snake_case, strip non-alphanumeric except underscores
        var result = firstLine
            .ToLowerInvariant()
            .Replace(' ', '_')
            .Replace('-', '_');

        var clean = new System.Text.StringBuilder(result.Length);
        foreach (var ch in result)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
                clean.Append(ch);
        }

        var finalEntity = clean.ToString().Trim('_');

        // Sanity checks: empty or suspiciously long → fallback
        if (string.IsNullOrEmpty(finalEntity) || finalEntity.Length > 40)
            return "general";

        return finalEntity;
    }
}
