using CsAgent.Core.Llm;
using CsAgent.Core.Memory;
using CsAgent.Services;
using System.Text;
using System.Text.Json.Nodes;

namespace CsAgent.Services
{
    public class HybridMemoryManager
    {
        private readonly SemanticMemory _semantic;
        private readonly ExactMemory _exact;

        public HybridMemoryManager(SemanticMemory semantic, ExactMemory exact)
            => (_semantic, _exact) = (semantic, exact);

        public void RecordError(int step, string tool, string error)
        {
            var errType = Classify(error);
            var domain = Domain(tool);
            _exact.Add(step, "error", error, tool);
            _semantic.AddPattern("error_pattern",
                summary: $"[{tool}] {Compact(error, 300)}",
                tags: new[] { "error", errType, domain },
                context: new() { { "tool", tool }, { "step", step.ToString() } });
        }

        // A success is always kept in ExactMemory (bounded ring buffer).
        // It becomes a semantic "solution" only when the same tool failed shortly before:
        // that is the lesson ("/opt failed, /home worked"). Only the arguments are stored,
        // never the result, so file contents do not leak into the long-term store.
        private const int SolutionWindow = 5;

        public void RecordSuccess(int step, string tool, string result, string? args = null)
        {
            _exact.Add(step, "success", result, tool);

            var failure = _exact.GetRecentErrors(10)
                .LastOrDefault(e => e.ToolName == tool && step - e.Step is >= 0 and <= SolutionWindow);
            if (failure is null) return;

            var errType = Classify(failure.Content);
            _semantic.AddPattern("solution",
                summary: $"[{tool}] succeeded after {errType} error with: {Compact(args ?? "(no arguments)", 160)}",
                tags: new[] { "solution", errType, Domain(tool) },
                context: new() { { "tool", tool }, { "step", step.ToString() } });
        }

        private static string Compact(string text, int max)
        {
            var oneLine = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
        }

        public record MemoryInsight(
            List<SemanticMemory.SemanticEntry> Patterns,
            List<ExactMemory.ExactEntry> RecentSteps,
            List<ExactMemory.ExactEntry> RecentErrors,
            List<ExactMemory.ExactEntry> RecentSuccesses
        );

        public MemoryInsight GetContext(string currentChallenge) => new(
            Patterns: _semantic.Search(currentChallenge, limit: 3),   // learned
            RecentSteps: _exact.GetRecent(5),                           // context
            RecentErrors: _exact.GetRecentErrors(2),                    // warnings
            RecentSuccesses: _exact.GetRecentSuccesses(2)                  // what worked
        );

        public string ToPromptText(MemoryInsight i)
        {
            var sb = new StringBuilder();

            if (i.RecentSuccesses.Count > 0)
            {
                sb.AppendLine("✅ WHAT WORKED RECENTLY:");  // step-2 lesson

                foreach (var s in i.RecentSuccesses)
                    sb.AppendLine($"  step {s.Step} [{s.ToolName}] → {s.Content[..Math.Min(80, s.Content.Length)]}");
            }

            if (i.RecentErrors.Count > 0)
            {
                sb.AppendLine("\n⚠️ RECENT ERRORS — DO NOT REPEAT:");
                foreach (var e in i.RecentErrors)
                    sb.AppendLine($"  step {e.Step} [{e.ToolName}] → {e.Content}");
            }

            if (i.Patterns.Count > 0)
            {
                sb.AppendLine("\n📚 LEARNED PATTERNS:");
                foreach (var p in i.Patterns)
                    sb.AppendLine($"  [{p.Type}] {p.Summary}{(p.Count > 1 ? $" (seen {p.Count}x)" : "")}");
            }

            return sb.ToString();
        }

        // Persistence: delegates to both layers
        public async Task SaveAsync(MemoryPaths paths)
        { await Task.WhenAll(_semantic.SaveAsync(paths.Semantic), _exact.SaveAsync(paths.Exact)); }

        public async Task LoadAsync(MemoryPaths paths)
        {
            await Task.WhenAll(_semantic.LoadAsync(paths.Semantic), _exact.LoadAsync(paths.Exact));
            var summary = await _summary.LoadAsync(paths.Summary);
            lock (_summaryGate) _previousSummary = summary;   // null when none: no stale summary from another file
        }

        // ── Session distillation ─────────────────────────────────────────────
        private readonly SummaryMemory _summary = new();
        private readonly object _summaryGate = new();
        private SessionSummary? _previousSummary;

        /// <summary>Number of notes of the loaded summary (0 when there is none).</summary>
        public int SessionNoteCount
        {
            get
            {
                lock (_summaryGate)
                    return _previousSummary is { } s
                        ? s.Decisions.Length + s.Constraints.Length + s.Pending.Length + s.FailedApproaches.Length
                        : 0;
            }
        }

        /// <summary>Context block for the next LLM call; empty when no summary exists.</summary>
        public string GetSessionContextBlock()
        {
            SessionSummary? s;
            lock (_summaryGate) s = _previousSummary;
            return s is { IsEmpty: false } ? _summary.ToContextBlock(s) : "";
        }

        /// <summary>
        /// Saves both memory layers, then asks the LLM to distill the conversation.
        /// Best effort: a failure (API down, timeout, unparsable answer) never throws and
        /// never overwrites the previous summary.
        /// Returns a one-line status for the user, or null when there was nothing to do.
        /// </summary>
        public async Task<string?> DistillAndSaveAsync(
            JsonArray messages, LlmClient client, MemoryPaths paths, CancellationToken ct)
        {
            await SaveAsync(paths);

            // Nothing worth summarising (system prompt + at most one exchange).
            if (messages.Count < 4) return null;

            try
            {
                SessionSummary? previous;
                lock (_summaryGate) previous = _previousSummary;

                var summary = await _summary.DistillAsync(messages, client, previous, ct);
                if (summary.IsEmpty) return "Session summary unchanged (the model returned nothing to keep).";

                if (!await _summary.SaveAsync(summary, paths.Summary))
                    return "Session summary could not be saved (see message above).";

                lock (_summaryGate) _previousSummary = summary;
                return $"Session summary updated: {SessionNoteCount} note(s) saved to {paths.Summary}.";
            }
            catch (Exception e) when (e is OperationCanceledException or HttpRequestException
                                         or FormatException or System.Text.Json.JsonException
                                         or InvalidDataException or InvalidOperationException)
            {
                var why = e is OperationCanceledException ? "timed out" : e.Message;
                Console.Error.WriteLine($"[Memory] session distillation skipped: {why}");
                return $"Session summary not updated ({why}); the previous one is kept.";
            }
        }

        // Helpers

        // English + French messages (cmd.exe / PowerShell on a French Windows answer in French).
        private static string Classify(string e)
        {
            var s = TextTokenizer.Fold(e);
            if (s.Contains("permission") || s.Contains("access is denied") || s.Contains("acces refuse")
                || s.Contains("acces interdit") || s.Contains("non autorise")) return "permission";
            if (s.Contains("not found") || s.Contains("no such file") || s.Contains("cannot find")
                || s.Contains("introuvable") || s.Contains("n'existe pas") || s.Contains("inexistant")) return "not-found";
            if (s.Contains("timeout") || s.Contains("timed out") || s.Contains("delai")
                || s.Contains("expire")) return "timeout";
            if (s.Contains("syntax") || s.Contains("syntaxe")) return "syntax";
            return "other";
        }

        private static string Domain(string tool) => tool switch
        {
            "write_file" or "read_file" or "list_dir" => "file-ops",
            "sh" => "shell",
            _ => "general"
        };
    }
}