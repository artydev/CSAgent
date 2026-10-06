using CsAgent.Shared;
using System.Text.Json.Nodes;

namespace CsAgent.Services
{
    public class ExactMemory
    {
        // One entry per agent step. Immutable after creation.
        public record ExactEntry(
            int Step,                   // loop counter: 1, 2, 3 …
            string Type,                // "thought" | "tool_call" | "error" | "success"
            string Content,             // message text (truncated to 500 chars)
            string? ToolName = null,    // "write_file" | "sh" | null
            DateTime? Timestamp = null  // UTC time of event
        );

        private List<ExactEntry> _history = new();
        private readonly object _gate = new();   // web mode shares one instance across requests
        private const int MaxSize = 50;

        // Call this once per agent action.
        public void Add(int step, string type, string content, string? toolName = null)
        {
            // Truncate: large file reads shouldn't bloat memory
            var body = content.Length > 500 ? content[..500] + "…" : content;

            lock (_gate)
            {
                _history.Add(new ExactEntry(
                    Step: step, Type: type, Content: body,
                    ToolName: toolName, Timestamp: DateTime.UtcNow));

                // Trim: keep only the most recent MaxSize entries
                if (_history.Count > MaxSize)
                    _history = _history
                        .OrderByDescending(e => e.Step)
                        .Take(MaxSize)
                        .OrderBy(e => e.Step)
                        .ToList();
            }
        }

        public int Count { get { lock (_gate) return _history.Count; } }

        private ExactEntry[] Snapshot() { lock (_gate) return _history.ToArray(); }

        public List<ExactEntry> GetRecent(int count = 5) =>
            Snapshot().OrderByDescending(e => e.Step).Take(count)
                      .OrderBy(e => e.Step).ToList();

        public List<ExactEntry> GetRecentErrors(int count = 3) =>
            Snapshot().Where(e => e.Type == "error")
                      .OrderByDescending(e => e.Step).Take(count)
                      .OrderBy(e => e.Step).ToList();

        public List<ExactEntry> GetRecentSuccesses(int count = 3) =>
            Snapshot().Where(e => e.Type == "success")
                      .OrderByDescending(e => e.Step).Take(count)
                      .OrderBy(e => e.Step).ToList();

        public async Task SaveAsync(string path)
        {
            var arr = new JsonArray();
            foreach (var e in Snapshot())
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

        public async Task LoadAsync(string path)
        {
            var arr = await AtomicFile.ReadJsonArrayAsync(path);
            if (arr is null) return;

            var loaded = new List<ExactEntry>();
            foreach (var item in arr)
            {
                try
                {
                    loaded.Add(new ExactEntry(
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

            lock (_gate) _history = loaded;
        }
    }
}