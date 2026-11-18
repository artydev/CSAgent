# CSFlow Workflow Authoring

**Version:** 1.0

Write a new CSFlow workflow (a YAML file in `workflows/`) that follows the project's conventions. Use it only
when the user **explicitly asks** to create or write a workflow ("crée un workflow…", "écris-moi un workflow…").
To run an existing one, use the CSFlow Workflows skill instead.

## Procedure

1. **Read `CSFLOW_LLM_GUIDE.md`** (CSFlow folder) with `read_file` before writing anything. It is the reference:
   do not invent a key, a step type or a variable. Pick the step types with its table in section 5.0.
2. **Ask at most one short question** if something costly is missing (which files, which human decision).
   Otherwise state your assumption and go on.
3. **Write** `workflows/<name>.yaml` with `write_file` (it asks the user to confirm). Use a new file name:
   never overwrite or edit an existing workflow unless the user named it.
4. **Check** with `csflow_check`. If `ok` is false, fix the file with `edit_file` and check again. Do not
   report the workflow as ready while `ok` is false.
5. **Report**: the file name, what each phase does, which steps call CSAgent, and how to run it
   (`csagent --mcp-config mcp.json --workflow <name>`, or the CSFlow interface).

## Conventions (always)

- **`name:` is required in practice.** It prefixes the run folder: `output/<name>_<date>_<time>/`
  (CSFlow 0.2.2). Use the file name without `.yaml`: lowercase ASCII letters, digits and hyphens
  (`compte-rendu-copil`). Spaces, accents and `/` become `_`. No name gives the prefix `run`.
- **Every file the workflow produces goes under `output/{{run_id}}/`**, for example
  `output/{{run_id}}/rapport.md`. Never `tmp/` (the next run overwrites it), never the CSFlow root, never `workflows/`.
  Several existing workflows still use `tmp/`: do not copy that.
- In an `agent` step, name the exact file to write (`Écris output/{{run_id}}/rapport.md …`), then read it
  back with a `file_read` step to prove it exists. Set `auto_confirm: true` only when the agent writes
  nothing outside `output/{{run_id}}/`.
- Paths are **relative** and use `/` (the same on Windows and Linux). No secret in the file: `{{env.NAME}}` only.
- Each step has a unique `id` in `snake_case` ASCII and a readable `description` in the user's language.
- Exact figures (totals, dates) are computed by `python` or `transform` steps, never by an `agent` step.
- Anything with an outside effect (e-mail, deletion, upload) sits behind a `human_approval` step.
  A workflow containing `human_approval` or `user_input` cannot be run through MCP or `--workflow`:
  say so, and tell the user to open it in the CSFlow interface.
- Add `# yaml-language-server: $schema=../csflow.schema.json` as the first line.

## Rules

- Never write a workflow that sends data out, contains a secret, or runs a destructive command without a human step.
- A request the 11 step types cannot express (loop, parallel run, sub-workflow, input asked at run time):
  say so and propose a workaround (a `python` step that handles the list, several steps in sequence). No new keyword.
- `shell` and `python` steps bypass CSAgent's command filter: keep them minimal, standard library only, and show
  the user what they do before they run the workflow.
