# Adding a New Tool to ToolDispatcher

This document describes the process for adding a new tool to the `ToolDispatcher` class in `CsAgent.Core.Agent`.

## Steps

1. **Add a case to the `DispatchAsync` switch** — map the tool name to its handler method, reading arguments from the parsed `JsonNode` (e.g. `args["path"]!.GetValue<string>()`).

2. **Add a JSON entry to `ToolDefinitions`** — a `{ "type": "function", "function": { name, description, parameters } }` object so the LLM knows the tool exists and how to call it.

3. **Implement the handler method** — a `private static` method (sync returning `string`, or `async Task<string>` if it does I/O) that wraps its logic in try/catch and returns either an `"OK: ..."` or `"Error: ..."` string.

4. **Add safety checks** — call `IsSafePath` for any file/directory operations, and `IsSafeCommand` if it runs shell commands.

5. **If destructive** — add the tool name to the `IsDestructive` list so it requires user confirmation.

6. **If it needs a callback** — add an optional delegate parameter to `DispatchAsync` (like `SwitchModelHandler`) and thread it through the call site.
