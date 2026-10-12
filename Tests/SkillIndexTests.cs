using System.Text.Json.Nodes;
using CsAgent.Shared;

namespace CsAgent.Tests;

static partial class Tests
{
    // The /skills menu of the web UIs reads skills/INDEX.md through GET /api/skills.
    static async Task SkillIndexGroup()
    {
        Group("Skills index (/skills menu)");

        const string sample = """
            # Skills Index

            Some text | with a pipe that is not a table.

            | Skill | Version | Use when… | File |
            |---|---|---|---|
            | Failure Recovery | 1.0 | A tool call has failed twice, or the same error repeats. | `skills/failure-recovery.md` |
            | Excel Report Builder | 1.2 | Raw data to a report | with `styles` | `skills/export-report-builder.md` |

            ---
            """;

        await T("the table is read: header and separator skipped, four fields per row", async () =>
        {
            var l = SkillIndex.Parse(sample);
            await Task.CompletedTask;
            Assert(l.Count == 2, $"{l.Count} rows");
            Assert(l[0].Name == "Failure Recovery" && l[0].Version == "1.0", $"{l[0].Name}/{l[0].Version}");
            Assert(l[0].File == "skills/failure-recovery.md", l[0].File);
            Assert(l[0].Description.StartsWith("A tool call has failed twice"), l[0].Description);
            return "ok";
        });

        await T("a '|' inside a description stays in the description", async () =>
        {
            var l = SkillIndex.Parse(sample);
            await Task.CompletedTask;
            Assert(l[1].Description == "Raw data to a report | with `styles`" && l[1].File == "skills/export-report-builder.md", l[1].Description + " / " + l[1].File);
            return "ok";
        });

        await T("no table, or an empty text: no skills", async () =>
        {
            await Task.CompletedTask;
            Assert(SkillIndex.Parse("").Count == 0 && SkillIndex.Parse("# Title\nno table here").Count == 0, "expected none");
            return "ok";
        });

        await T("Load: reads <dir>/skills/INDEX.md; a missing folder or file gives an empty list", async () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "skillidx_" + Guid.NewGuid().ToString("N"));
            _tmpDirs.Add(dir);
            Directory.CreateDirectory(Path.Combine(dir, "skills"));
            await Task.CompletedTask;
            Assert(SkillIndex.Load(dir).Count == 0, "no INDEX.md yet");
            File.WriteAllText(Path.Combine(dir, "skills", "INDEX.md"), sample);
            Assert(SkillIndex.Load(dir).Count == 2, "INDEX.md present");
            Assert(SkillIndex.Load(Path.Combine(dir, "nope")).Count == 0, "missing folder");
            return "ok";
        });

        await T("ToJson: an array of {name, version, description, file}", async () =>
        {
            var json = JsonNode.Parse(SkillIndex.ToJson(SkillIndex.Parse(sample)))!.AsArray();
            await Task.CompletedTask;
            Assert(json.Count == 2, $"{json.Count}");
            Assert(json[0]!["name"]!.GetValue<string>() == "Failure Recovery" && json[0]!["file"]!.GetValue<string>() == "skills/failure-recovery.md", json.ToJsonString());
            return "ok";
        });
    }
}
