using CsAgent.Core.Agent;
using System.Text;

namespace CsAgent.Core.Tasks;

/// <summary>
/// Host-side enforcement of the task-tracking convention described in the
/// system prompt (§3.5). The host — not the LLM — owns the task folder and its
/// lifecycle, so tracking is guaranteed rather than merely suggested.
///
/// A tracker is created lazily on the first tool dispatch (see
/// <see cref="CodingAgent"/>), so conversational replies and questions that
/// never call a tool do not produce a folder. Once created, the host writes
/// PLAN.md / SUBTASKS.md / PROGRESS.md / task.json programmatically and records
/// the final status when the run ends.
///
/// Folder layout (relative to the working directory):
///   .csagent/tasks/&lt;slug&gt;-&lt;YYYY-MM-DD&gt;/
///     task.json     ← real task ID (GUID) + metadata, enables --resume
///     PLAN.md       ← scaffold written by the host; the LLM fills in the plan
///     SUBTASKS.md   ← checklist scaffold, updated as steps complete
///     PROGRESS.md   ← append-only log, one entry per meaningful step
/// </summary>
public sealed class TaskTracker
{
    /// <summary>Root directory under which all task folders live.</summary>
    public const string RootDir = ".csagent/tasks";

    private readonly string _folder;
    private readonly string _id;
    private readonly string _slug;
    private readonly string _date;
    private readonly string _goal;
    private int _stepCount;

    private TaskTracker(string folder, string id, string slug, string date, string goal)
    {
        _folder = folder;
        _id = id;
        _slug = slug;
        _date = date;
        _goal = goal;
    }

    /// <summary>The absolute path of the task folder.</summary>
    public string Folder => _folder;

    /// <summary>The real, unique task identifier (GUID).</summary>
    public string Id => _id;

    /// <summary>Human-readable kebab-case slug used in the folder name.</summary>
    public string Slug => _slug;

    /// <summary>True once the tracker has been finalized (complete or incomplete).</summary>
    public bool IsFinalized { get; private set; }

    /// <summary>
    /// Creates a new task folder and writes the initial scaffold files.
    /// </summary>
    /// <param name="slug">Short kebab-case summary of the task, e.g. "add-health-endpoint".</param>
    /// <param name="goal">One-line description of the task (from the user's first message).</param>
    public static TaskTracker Create(string slug, string goal)
    {
        var id = Guid.NewGuid().ToString("N");
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var folder = Path.Combine(RootDir, $"{SanitizeSlug(slug)}-{date}");
        var tracker = new TaskTracker(folder, id, SanitizeSlug(slug), date, goal);

        Directory.CreateDirectory(folder);
        tracker.WriteTaskJson();
        tracker.WritePlan();
        tracker.WriteSubtasks();
        tracker.WriteProgress($"Task started (id {id}).");

        return tracker;
    }

    /// <summary>
    /// Loads an existing task folder by its ID (GUID). Returns null when no
    /// folder with that ID exists.
    /// </summary>
    public static TaskTracker? Load(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !Directory.Exists(RootDir))
            return null;

        foreach (var dir in Directory.EnumerateDirectories(RootDir))
        {
            var meta = Path.Combine(dir, "task.json");
            if (!File.Exists(meta)) continue;

            try
            {
                var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(meta));
                if (json?["id"]?.GetValue<string>() == id)
                {
                    var tracker = new TaskTracker(
                        dir,
                        id,
                        json["slug"]?.GetValue<string>() ?? Path.GetFileName(dir),
                        json["date"]?.GetValue<string>() ?? "",
                        json["goal"]?.GetValue<string>() ?? "");
                    tracker.IsFinalized = json["status"]?.GetValue<string>() is "complete" or "incomplete";
                    return tracker;
                }
            }
            catch
            {
                // Corrupt metadata — skip this folder and keep looking.
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the most recently modified task folder whose PROGRESS.md does not
    /// end with a final status. Used to offer resumption at session start.
    /// </summary>
    public static TaskTracker? FindResumable()
    {
        if (!Directory.Exists(RootDir)) return null;

        TaskTracker? best = null;
        DateTime bestTime = DateTime.MinValue;

        foreach (var dir in Directory.EnumerateDirectories(RootDir))
        {
            var meta = Path.Combine(dir, "task.json");
            if (!File.Exists(meta)) continue;

            try
            {
                var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(meta));
                var status = json?["status"]?.GetValue<string>();
                if (status is "complete" or "incomplete") continue; // already finalized

                var tracker = new TaskTracker(
                    dir,
                    json?["id"]?.GetValue<string>() ?? "",
                    json?["slug"]?.GetValue<string>() ?? Path.GetFileName(dir),
                    json?["date"]?.GetValue<string>() ?? "",
                    json?["goal"]?.GetValue<string>() ?? "");

                var time = Directory.GetLastWriteTimeUtc(dir);
                if (time > bestTime)
                {
                    bestTime = time;
                    best = tracker;
                }
            }
            catch
            {
                // Corrupt metadata — skip.
            }
        }

        return best;
    }

    /// <summary>
    /// Appends a meaningful-step entry to PROGRESS.md. The host calls this after
    /// each completed step so the log is enforced, not left to the LLM.
    /// </summary>
    public void LogStep(string status, string note)
    {
        _stepCount++;
        var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm");
        var line = $"## {stamp} — step {_stepCount}\nStatus: {status}\n{note}\n";
        File.AppendAllText(Path.Combine(_folder, "PROGRESS.md"), line, Encoding.UTF8);
    }

    /// <summary>
    /// Marks a SUBTASKS.md checklist item as done. Items are matched by
    /// 1-based index (the order they appear in the file).
    /// </summary>
    public void MarkSubtaskDone(int index)
    {
        var path = Path.Combine(_folder, "SUBTASKS.md");
        if (!File.Exists(path)) return;

        var lines = File.ReadAllLines(path);
        int seen = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("- [ ]", StringComparison.Ordinal))
            {
                seen++;
                if (seen == index)
                {
                    lines[i] = lines[i].Replace("- [ ]", "- [x]", StringComparison.Ordinal);
                    File.WriteAllLines(path, lines, Encoding.UTF8);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Finalizes the task with a terminal status. Writes the status to
    /// task.json and appends the final entry to PROGRESS.md. Idempotent.
    /// </summary>
    public void Finalize(string status, string summary)
    {
        if (IsFinalized) return;
        IsFinalized = true;

        var meta = Path.Combine(_folder, "task.json");
        try
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(meta))?.AsObject();
            if (json is not null)
            {
                json["status"] = status;
                json["finished_at"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm");
                File.WriteAllText(meta, json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            }
        }
        catch
        {
            // Best-effort metadata update; PROGRESS.md is the source of truth.
        }

        var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm");
        File.AppendAllText(Path.Combine(_folder, "PROGRESS.md"),
            $"## {stamp} — final\nStatus: {status} — {summary}\n", Encoding.UTF8);
    }

    // ── Scaffold writers ─────────────────────────────────────────────────────

    private void WriteTaskJson()
    {
        var json = new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = _id,
            ["slug"] = _slug,
            ["date"] = _date,
            ["goal"] = _goal,
            ["status"] = "in_progress",
            ["created_at"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")
        };
        File.WriteAllText(Path.Combine(_folder, "task.json"),
            json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);
    }

    private void WritePlan()
    {
        var content = $"# Task: {_goal}\nDate: {_date}\n\n## Goal\n{_goal}\n\n" +
                      "## Plan\n<!-- The agent fills in the numbered steps here as it works. -->\n\n" +
                      "## Risks\n<!-- The agent records risks and how to detect them. -->\n";
        File.WriteAllText(Path.Combine(_folder, "PLAN.md"), content, Encoding.UTF8);
    }

    private void WriteSubtasks()
    {
        var content = "# Subtasks\n\n- [ ] Plan the approach\n- [ ] Execute\n- [ ] Verify and test\n- [ ] Update PROGRESS.md with outcome\n";
        File.WriteAllText(Path.Combine(_folder, "SUBTASKS.md"), content, Encoding.UTF8);
    }

    private void WriteProgress(string firstEntry)
    {
        var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm");
        File.WriteAllText(Path.Combine(_folder, "PROGRESS.md"),
            $"## {stamp} — start\nStatus: started\n{firstEntry}\n", Encoding.UTF8);
    }

    private static string SanitizeSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return "task";
        var sb = new StringBuilder();
        foreach (var ch in slug.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var result = sb.ToString().Trim('-');
        return result.Length == 0 ? "task" : result;
    }
}
