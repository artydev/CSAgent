using System.Text.Json.Nodes;

namespace CsAgent.Shared;

/// <summary>One row of skills/INDEX.md.</summary>
public sealed record SkillEntry(string Name, string Version, string Description, string File);

/// <summary>
/// Reads the skill library index (skills/INDEX.md): the table whose columns are
/// Skill | Version | Use when… | File. The agent reads the same file; this is what lets
/// a UI list the skills. AOT-safe: plain string work and JsonNode, no reflection.
/// </summary>
public static class SkillIndex
{
    public const string RelativePath = "skills/INDEX.md";

    /// <summary>Parse the Markdown table; anything that is not a table row is ignored.</summary>
    public static List<SkillEntry> Parse(string markdown)
    {
        var list = new List<SkillEntry>();
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('|')) continue;

            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 4) continue;
            if (cells.All(c => c.Length == 0 || c.All(ch => ch is '-' or ':' or ' '))) continue;   // |---|---|
            if (cells[0].Equals("Skill", StringComparison.OrdinalIgnoreCase)) continue;             // header row

            var file = cells[^1].Trim('`', ' ');
            // a "|" inside the description splits it into several cells: put it back together
            var description = string.Join(" | ", cells.Skip(2).Take(cells.Length - 3));
            if (cells[0].Length == 0 || file.Length == 0) continue;

            list.Add(new SkillEntry(cells[0], cells[1], description, file));
        }
        return list;
    }

    /// <summary>The skills of the working directory's index; empty when there is none or it cannot be read.</summary>
    public static List<SkillEntry> Load(string? workingDirectory = null)
    {
        var path = Path.Combine(workingDirectory ?? Directory.GetCurrentDirectory(), "skills", "INDEX.md");
        try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : new List<SkillEntry>(); }
        catch { return new List<SkillEntry>(); }
    }

    public static string ToJson(IEnumerable<SkillEntry> skills)
    {
        var arr = new JsonArray();
        foreach (var s in skills)
        {
            var o = new JsonObject();
            o.Add("name", JsonValue.Create(s.Name));
            o.Add("version", JsonValue.Create(s.Version));
            o.Add("description", JsonValue.Create(s.Description));
            o.Add("file", JsonValue.Create(s.File));
            arr.Add(o);
        }
        return arr.ToJsonString();
    }
}
