using CsAgentUI.Services;
using System.Text;

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
                summary: $"[{tool}] {error}",
                tags: new[] { "error", errType, domain },
                context: new() { { "tool", tool }, { "step", step.ToString() } });
        }

        // Called when a tool returns a success result.
        public void RecordSuccess(int step, string tool, string result)
        {
            _exact.Add(step, "success", result, tool);
            _semantic.AddPattern("solution",
                summary: $"[{tool}] succeeded: {result[..Math.Min(80, result.Length)]}",
                tags: new[] { "solution", Domain(tool) },
                context: new() { { "tool", tool }, { "step", step.ToString() } });
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
                    sb.AppendLine($"  [{p.Type}] {p.Summary}");
            }

            return sb.ToString();
        }

        // Persistence: delegates to both layers
        public async Task SaveAsync(string sem, string ex)
        { await Task.WhenAll(_semantic.SaveAsync(sem), _exact.SaveAsync(ex)); }

        public async Task LoadAsync(string sem, string ex)
        { await Task.WhenAll(_semantic.LoadAsync(sem), _exact.LoadAsync(ex)); }

        // Helpers
        private static string Classify(string e) => e.ToLower() switch
        {
            var s when s.Contains("permission") => "permission",
            var s when s.Contains("not found") => "not-found",
            var s when s.Contains("timeout") => "timeout",
            var s when s.Contains("syntax") => "syntax",
            _ => "other"
        };

        private static string Domain(string tool) => tool switch
        {
            "write_file" or "read_file" or "list_dir" => "file-ops",
            "sh" => "shell",
            _ => "general"
        };
    }
}
