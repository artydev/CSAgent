# CSFlow Workflows 

**Version:** 1.0

Run the user's CSFlow workflows (YAML files in `workflows/`) through the `csflow_*` MCP tools
(`mcp_csflow_list`, `mcp_csflow_check`, `mcp_csflow_run`, `mcp_csflow_status`; the prefix may differ).

## Procedure

1. **List** with `csflow_list` when the user does not give an exact file name. Each entry says
   `needs_human` (contains a human approval step) and `uses_agent` (has steps that call CSAgent).
2. **Check** with `csflow_check`. Stop and report if `ok` is false: show the `issues`, do not run.
3. **Run** with `csflow_run`. It waits about 50 s. If `status` is `running`, call `csflow_status`
   with the same `run_id`, again and again, until `status` is `done` or `error`. Each call waits
   about 50 s, so do not call it faster; stop after about 20 calls and tell the user it is still running.
4. **Report** the real outcome: status, steps done, the `result` and, on error, the `failed_step`
   (its `output` and `returncode`). Say which files the workflow wrote (read them with `read_file`
   if the user asks for their content). Never say a workflow succeeded unless `status` is `done`.

## Rules

- A workflow with `needs_human: true` is **never** run from here: `csflow_run` refuses it.
  Tell the user to open it in the CSFlow interface and approve it there.
- Run **one workflow at a time**. If `csflow_run` answers `started: false`, wait for the running one.
- Do **not** create, edit or delete files in `workflows/` unless the user explicitly asks: a workflow
  can run shell commands and Python scripts that bypass this agent's own command filter.
  When they do ask to create one, follow the CSFlow Workflow Authoring skill.
- If `uses_agent` is true, a second CSAgent in API mode (`csagent --api`, port 8080) must be running
  for the workflow's agent steps. If a step fails with "Connection refused", tell the user to start it.
- Paths inside workflows are relative to the CSFlow folder, which must be the folder CSAgent runs in
  (`csflow_list` returns it as `cwd`). If a file written by a workflow is not found, compare `cwd`
  with your own working directory.
- If a run ends in `error`, apply the Failure Recovery skill: read the failed step, do not simply rerun.
