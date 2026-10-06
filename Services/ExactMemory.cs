using CsAgent.Shared;
using System.Text.Json.Nodes;

namespace CsAgent.Services
{
    public class ExactMemory
    {
        // One entry per agent step. Immutable after creation.
        public record ExactEntry(

            int Step,           // loop counter: 1, 2, 3 …

            string Type,           // "thought" | "tool_call" | "error" | "success"

            string Content,        // message text (truncated to 500 chars)

            string? ToolName = null, // "write_file" | "sh" | null

            DateTime? Timestamp = null  // UTC time of event
        );


        private List<ExactEntry> _history = new();
        private const int MaxSize = 50;


        // Call this once per agent action.
        public void Add(int step, string type, string content, string? toolName = null)
        {
            // Truncate: large file reads shouldn't bloat memory
            var body = content.Length > 500 ? content[..500] + "…" : content;


            _history.Add(new ExactEntry(
                        Step: step,
                        Type: type,
                        Content: body,
                        ToolName: toolName,
                        Timestamp: DateTime.UtcNow
                    ));

            // Trim: keep only the most recent MaxSize entries
            if (_history.Count > MaxSize)
                _history = _history
                    .OrderByDescending(e => e.Step)  // newest first
                    .Take(MaxSize)                  // keep top 50
                    .OrderBy(e => e.Step)           // restore order
                    .ToList();

        }

        public int Count => _history.Count;


        // Last N steps, in chronological order.
        public List<ExactEntry> GetRecent(int count = 5) =>
            _history.OrderByDescending(e => e.Step)
                    .Take(count)
                    .OrderBy(e => e.Step)
                    .ToList();

        // Recent errors only — fed into the "⚠️ BE CAREFUL" prompt block.
        public List<ExactEntry> GetRecentErrors(int count = 3) =>
            _history.Where(e => e.Type == "error")
                    .OrderByDescending(e => e.Step)
                    .Take(count)
                    .OrderBy(e => e.Step)
                    .ToList();

        // Recent successes — for learning "what worked".
        public List<ExactEntry> GetRecentSuccesses(int count = 3) =>
            _history.Where(e => e.Type == "success")
                    .OrderByDescending(e => e.Step)
                    .Take(count)
                    .OrderBy(e => e.Step)
                    .ToList();


        public async Task SaveAsync(string path)
        {
            var arr = new JsonArray();
            foreach (var e in _history)
                arr.Add(new JsonObject
                {
                    ["step"] = e.Step,
                    ["type"] = e.Type,
                    ["content"] = e.Content,
                    ["toolName"] = e.ToolName,
                    ["ts"] = e.Timestamp?.ToString("O")
                });
            await AtomicFile.TryWriteAllTextAsync(path,
                arr.ToJsonString(new() { WriteIndented = true }));
        }

        // Tolerant: a corrupt file is quarantined (see AtomicFile) and memory starts empty;
        // a single malformed entry is skipped, the others are kept.
        public async Task LoadAsync(string path)
        {
            var arr = await AtomicFile.ReadJsonArrayAsync(path);
            if (arr is null) return;

            _history.Clear();
            foreach (var item in arr)
            {
                try
                {
                    _history.Add(new ExactEntry(
                        Step: item?["step"]?.GetValue<int>() ?? 0,
                        Type: item?["type"]?.GetValue<string>() ?? "",
                        Content: item?["content"]?.GetValue<string>() ?? "",
                        ToolName: item?["toolName"]?.GetValue<string>(),
                        Timestamp: DateTime.TryParse(item?["ts"]?.GetValue<string>(), null,
                                       System.Globalization.DateTimeStyles.RoundtripKind, out var ts) ? ts : null));
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException)
                {
                    // malformed entry: skip it
                }
            }
        }

    }

}