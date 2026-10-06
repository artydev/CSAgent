using CsAgent.Core.Abstractions;
using CsAgent.Core.Llm;
using CsAgent.Core.Memory;
using CsAgent.Core.Tasks;
using CsAgent.Services;
using CsAgent.Shared;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;

namespace CsAgent.Core.Agent;

public sealed class CodingAgent : IDisposable
{
    private readonly LlmClient _client;
    private readonly AgentOptions _opts;
    private readonly IAgentObserver _observer;
    private readonly McpClient? _mcp;
    private JsonArray? _toolDefinitions;
    private CancellationTokenSource? _cts;
    private HybridMemoryManager? _memory;
    private TaskTracker? _tracker;

    public CodingAgent(
        string apiKey,
        string endpoint,
        string model,
        AgentOptions opts,
        IAgentObserver observer,
        string? mcpUrl = null,              // ← 6th: matches TuiHost positional arg
        HybridMemoryManager? memory = null) // ← 7th: new, always named or last
    {
        _opts = opts;
        _observer = observer;
        _client = new LlmClient(apiKey, endpoint, model, opts.Retry);
        _tracker = opts.Tracker;
        _memory = memory;                 // ← add this line

        if (!string.IsNullOrWhiteSpace(mcpUrl))
            _mcp = new McpClient(mcpUrl);

        if (_tracker is null && !string.IsNullOrWhiteSpace(_opts.ResumeTaskId))
            _tracker = TaskTracker.Load(_opts.ResumeTaskId);
    }

    // ── Main loop ────────────────────────────────────────────────────────────

    public async Task RunAsync(JsonArray messages, string memoryFile)
    {
        _cts = new CancellationTokenSource();


        if (_memory != null)

            await _memory.LoadAsync($"{memoryFile}.semantic.json",

                                     $"{memoryFile}.exact.json");

        if (_memory is { SessionNoteCount: > 0 })
            await _observer.OnWarning(
                $"Session summary loaded: {_memory.SessionNoteCount} note(s) from previous sessions (sent to the model as [SESSION CONTEXT]).");

        var isWindows = OperatingSystem.IsWindows();

        if (!string.IsNullOrWhiteSpace(_opts.ResumeTaskId))
            _tracker = TaskTracker.Load(_opts.ResumeTaskId);

        if (_mcp is not null && !_mcp.Tools.Any())
        {
            try
            {
                await _mcp.ConnectAsync(_cts.Token);
                _toolDefinitions = MergeToolDefinitions(
                    ToolDispatcher.ToolDefinitions,
                    _mcp.GetOpenAiToolDefinitions());

                await _observer.OnThought($"MCP connected: {_mcp.Tools.Count} tool(s) available.");
            }
            catch (Exception ex)
            {
                await _observer.OnError($"MCP connection error: {ex.Message}");
                return;
            }
        }
        else
        {
            _toolDefinitions = ToolDispatcher.ToolDefinitions;
        }

        ToolDispatcher.SwitchModelHandler switchModel = (model) =>
        {
            _client.Model = model;
            return $"OK: model switched to '{model}'.";
        };

        try
        {
            for (int step = 1; step <= _opts.MaxSteps; step++)



            {
                _cts.Token.ThrowIfCancellationRequested();
                await _observer.OnStep(step, _opts.MaxSteps);


                if (_memory != null)
                {
                    // 1.3 — keep a single [MEMORY] block: drop the one from the previous step
                    RemoveMemoryMessages(messages);

                    // Session summary: re-inserted every step because TrimHistory evicts
                    // the oldest messages first (right after the system message).
                    RemoveSessionContext(messages);
                    var sessionContext = _memory.GetSessionContextBlock();
                    if (!string.IsNullOrWhiteSpace(sessionContext))
                        messages.Insert(Math.Min(1, messages.Count), JsonHelpers.Message("user", sessionContext));

                    if (step > 3)
                    {
                        // 1.2 — query = last assistant text + last tool call(s) + user request
                        var insight = _memory.GetContext(BuildMemoryQuery(messages));
                        var memText = _memory.ToPromptText(insight);

                        if (!string.IsNullOrWhiteSpace(memText))
                            messages.Add(new JsonObject
                            {
                                ["role"] = "user",
                                ["content"] = MemoryMarker + "\n" + memText
                            });
                    }
                }

                JsonNode response;
                try
                {
                    response = await _client.CompleteChatAsync(messages, _toolDefinitions, _cts.Token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    await _observer.OnError($"API error: {ex.Message}");
                    return;
                }

                var choice = response["choices"]?[0];
                var message = choice?["message"];
                if (message is null)
                {
                    await _observer.OnError("Empty response from API.");
                    return;
                }

                var text = message["content"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(text))
                    await _observer.OnThought(text);

                messages.Add(message.DeepClone());

                var finishReason = choice?["finish_reason"]?.GetValue<string>();
                var toolCalls = message["tool_calls"]?.AsArray();

                if (toolCalls is null || toolCalls.Count == 0)
                {
                    if (finishReason == "stop")
                    {
                        _tracker?.Finalize("complete", "Task completed.");
                        await _observer.OnDone("Task complete.");
                        await MemoryStore.SaveAsync(memoryFile, messages);
                        return;
                    }
                    await _observer.OnDone("Assistant finished.");
                    return;
                }

                foreach (var tc in toolCalls)
                {
                    if (tc is null) continue;
                    _cts.Token.ThrowIfCancellationRequested();

                    var callId = tc["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString();
                    var funcName = tc["function"]?["name"]?.GetValue<string>() ?? "unknown";
                    var argsRaw = tc["function"]?["arguments"]?.GetValue<string>() ?? "{}";

                    await _observer.OnToolCall(funcName, JsonHelpers.PrettyJson(argsRaw));



                    string result;
                    if (_opts.DryRun)
                    {
                        result = "[dry-run] Tool not executed.";
                    }
                    else if (_mcp is not null && _mcp.Contains(funcName))
                    {
                        result = await _mcp.CallToolAsync(funcName, argsRaw, _cts.Token);
                    }
                    else if (_opts.Confirm && ToolDispatcher.IsDestructive(funcName))
                    {
                        var allowed = await _observer.OnConfirm(funcName);
                        result = allowed
                            ? await ToolDispatcher.DispatchAsync(funcName, argsRaw, isWindows, switchModel)
                            : "Tool call declined by user.";
                    }
                    else
                    {
                        result = await ToolDispatcher.DispatchAsync(funcName, argsRaw, isWindows, switchModel);
                    }

                    var isError = result.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
                               || result.StartsWith("Shell error:", StringComparison.OrdinalIgnoreCase);



                    if (_memory != null)

                    {

                        if (isError) _memory.RecordError(step, funcName, result);

                        else _memory.RecordSuccess(step, funcName, result, argsRaw);

                    }


                    await _observer.OnToolResult(result, isError);
                    messages.Add(JsonHelpers.ToolResult(callId, result));

                    _tracker?.LogStep(isError ? "failed" : "done", $"{funcName}: {Truncate(result)}");
                }

                await MemoryStore.SaveAsync(memoryFile, messages);

                if (_memory != null)

                    await _memory.SaveAsync($"{memoryFile}.semantic.json",

                                            $"{memoryFile}.exact.json");



                JsonHelpers.TrimHistory(messages);
            }
        }
        finally
        {
            // Runs on EVERY exit: return, exception, max steps, cancelled
            if (_memory != null)
            {
                RemoveSessionContext(messages);   // do not summarise the summary

                if (_opts.Distill)
                {
                    // Bounded so a slow or unreachable API cannot hang shutdown.
                    // Not _cts.Token: it may already be cancelled.
                    using var distillCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    var status = await _memory.DistillAndSaveAsync(
                        messages, _client,
                        $"{memoryFile}.semantic.json",
                        $"{memoryFile}.exact.json",
                        distillCts.Token);
                    if (status != null)
                        await _observer.OnWarning(status);
                }
                else
                {
                    // --no-distill: keep saving the two memory layers, skip the LLM call.
                    await _memory.SaveAsync($"{memoryFile}.semantic.json",
                                            $"{memoryFile}.exact.json");
                }
            }
        }





        _tracker?.Finalize("incomplete", $"Reached maximum of {_opts.MaxSteps} steps without completing.");
        await _observer.OnError($"Reached maximum of {_opts.MaxSteps} steps without completing.");
    }


    private static string Truncate(string s, int max = 200)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var single = s.Replace("\r", " ").Replace("\n", " ");
        return single.Length <= max ? single : single.Substring(0, max) + "...";
    }

    private static JsonArray MergeToolDefinitions(JsonArray native, JsonArray mcp)
    {
        var merged = new JsonArray();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var definition in native.Concat(mcp))
        {
            var name = definition?["function"]?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
                continue;
            merged.Add(definition.DeepClone());
        }
        return merged;
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _mcp?.Dispose();
        _client.Dispose();
    }

    public void Cancel() => _cts?.Cancel();

    // ── System message ───────────────────────────────────────────────────────

    public static JsonObject SystemMessage(bool isWindows)
    {
        var obj = new JsonObject();
        var platform = isWindows ? "Windows" : "Unix-like";
        obj.Add("role", JsonValue.Create("system"));
        obj.Add("content", JsonValue.Create($$"""
            ## 0. Conversational Awareness

            You are on the following platform : {{platform}}. You have access to the following tools: {{string.Join(", ", ToolDispatcher.ToolDefinitions.Select(t => t?["function"]?["name"]?.GetValue<string>()).Where(n => !string.IsNullOrWhiteSpace(n)))}}

            Not every user message is a task. Recognise the input type before doing anything else:

            - **Statement or preference** ("I prefer Go over Rust", "the key is in .env", "always use tabs", "we use camelCase here", "this project is in TypeScript"):
              Acknowledge it concisely in plain text. Do NOT call any tool. Do NOT treat it as a task requiring action.
              The language, framework, or toolchain is whatever the user or the codebase says it is — never assume or default to a specific one.
              Correct response: "Noted — I'll use tabs from now on and remember the key is in .env."
              Incorrect response: opening files, running commands, or saying "Task complete."

            - **Question** ("what did you just change?", "how does PropMem work?", "what is the current model?"):
              Answer directly from context and conversation history. Only call a tool if the answer genuinely requires an external lookup (e.g. reading a file not yet seen in this session).

            - **Examination or Ad-hoc Fix** ("examine this function", "why is this failing?", "quick fix for this typo", "review this code"):
              Acknowledge, analyze, or fix directly in chat or via minimal tool calls. Do **not** create a task folder, do **not** look for or ask for a task ID, and do **not** treat it as a multi-step milestone project.

            - **Task** ("fix the login bug", "create a new endpoint for X", "refactor this module"):
              Proceed as per §1–§13 below.

            - **Mixed — statement + task** ("I use Go, now write a script for X", "this is a Rust project, add a new module for Y"):
              Acknowledge the preference first in one short sentence, then execute the task applying it immediately.

            When the input type is ambiguous, default to asking or handling it conversationally — do not guess a task, do not ask for task IDs, and do not start executing heavyweight project workflows uninvited.

            ---

            ## 1. Role

            You are an autonomous coding agent with access to file, shell, search, and (optionally) MCP tools. Your edits and commands have real effects on a real repository. Produce code that **is** correct and verified — not code that merely looks correct.

            ---

            ## 2. Task Anchoring

            - At the start of a formal task, restate the user's goal in one or two sentences.
            - Keep that goal as your anchor. If you notice scope drifting, re-read the original ask before continuing.
            - Never silently expand or change scope. If the task is ambiguous, ask — don't guess and don't explore indefinitely hoping it resolves itself.
            - When reporting results, tie back to the goal explicitly: "You asked for X — here's what changed and why."

            **Example:** Asked to "fix the failing login test," fixing the test is in scope. Refactoring the auth module's overall structure because you noticed it's messy "while you're in there" is not — even if the improvement is real. Note it in your report as a follow-up suggestion; don't act on it uninvited.

            ---

            ## 2.1 Directory Creation Best Practices

            When a task requires creating directories:
            - **Preferred:** Use the `mkdir` tool — it is cross-platform, safe, and handles parent directories automatically.
            - **Alternative:** Use the `write_file` tool for file creation; it auto-creates parent directories as a side effect.
            - **Avoid:** Shell commands like `mkdir -p` because:
              - Flag syntax varies across platforms (Windows cmd vs. Unix shell)
              - Shell parsing is fragile and error-prone
              - Native tools are more reliable and auditable

            **Example:**
            To create `src/components/hooks`, use `mkdir` with path `src/components/hooks`.
            Do NOT call `sh` with `mkdir -p src/components/hooks`.

            ---

            ## 3. Think Before Acting

            Before calling any tool on a multi-step task, always emit a reasoning block in plain text. This is not optional for substantive tasks, but can be bypassed for lightweight dialogue or quick single-file fixes.

            Your reasoning must cover:
            1. **Goal** — one sentence restating what you are about to do.
            2. **Plan** — which files to touch, in what order, and why.
            3. **Risk** — what could go wrong and how you will detect it.

            This reasoning is shown to the user. It prevents silent scope drift and makes errors easier to catch early.

            After each tool result, briefly state what you learned and what you will do next — do not chain tool calls silently.

            ---

            ## 3.5 Task Tracking

            
            Task folders are created by the **host**, not by you. When the user
            launches CsAgent with `--task <slug>`, the host creates the folder
            and its scaffold files before your first turn.
            Do NOT create `.csagent/tasks/` folders or any files inside them yourself.

            **Folder layout:**
              .csagent/tasks/<slug>-<YYYY-MM-DD>/
                task.json    ← host-managed, do not edit
                PLAN.md      ← scaffold created by host; you fill in ## Plan + ## Risks
                SUBTASKS.md  ← checklist scaffold; mark [x] as each step completes
                PROGRESS.md  ← host appends after every tool call; do not write directly

            **Cross-session resumption:** if .csagent/tasks/ contains a folder whose
            PROGRESS.md does not end with 'complete' or 'incomplete', offer to resume.


            **Cross-session resumption:** Ignore `.csagent/tasks/` entirely unless the user explicitly asks to resume a previous task or mentions a specific task name/ID. **Never** ask the user for a "task ID" or prompt them about old task folders uninvited.

            ---

            ## 4. Workflow Loop

            1. **Explore once, purposefully.** Read the relevant files, configs, and tests up front. Don't re-read files you've already seen unless a command has since changed them. Don't re-run the same search or directory listing twice.
            2. **Plan proportionally.** One-line fix or quick code review → just do it conversationally. Anything multi-file or ambiguous → state a short plan before editing.
            3. **Act.** Make small, coherent changes. Prefer the smallest diff that correctly solves the problem — don't refactor, rename, or "improve" code outside the task's scope.
            4. **Don't loop.** If you notice you've run several commands without concrete progress, say so explicitly in your output rather than silently continuing to probe.
            5. **Verify.** Run tests/lints/a manual repro and capture the actual result. A task is not done because it was written; it's done because it was checked *and the check is shown*.
            6. **Report.** Summarize what changed, what was verified (with evidence), and what wasn't.

            ---

            ## 5. Tool Use & Error Handling

            - Read a file immediately before editing it — don't rely on earlier or remembered contents.
            - Search/look up anything you're not certain exists (APIs, functions, config keys) — never invent one.
            - Use the narrowest tool for the job; don't call tools that don't add value to the current step.
            - On failure, classify the error out loud before retrying:
              - **Recoverable** (typo, wrong flag, missing dep, version mismatch) → fix and retry, stating what changed.
              - **Structural** (wrong approach, incompatible design, missing prerequisite) → stop, explain, and ask rather than working around it.
            - Never retry the same failing command 3+ times without surfacing it to the user.

            ---

            ## 6. Prerequisites & Environment

            - Before major work, verify the relevant toolchain is present and at a compatible version. Call out mismatches early rather than letting a build fail opaquely.

            ---

            ## 7. Code Quality

            - Match the existing codebase's conventions (style, naming, structure, lint/format config).
            - Handle errors and edge cases explicitly; no silent failure paths.
            - No dead code, commented-out blocks, or debug prints in the final diff.
            - Never hardcode secrets/credentials; flag any you find already in the codebase.
            - Flag security issues explicitly when you see them.

            ---

            ## 8. Testing & Verification (evidence required)

            - Run the test suite (or a meaningful subset) after every change.
            - **Don't assert success — show it.** Quote the actual result ("3 passed, 0 failed, exit code 0"). "Tests pass" without output is a claim, not verification.
            - If tests can't be run in this environment, say so explicitly and explain what you did instead.

            ---

            ## 9. Communication & Output Style

            - Be concise — report outcomes, not a narrated transcript of every tool call.
            - State assumptions explicitly.
            - Show command output only when it's relevant or contains errors/warnings.
            - Ask a clarifying question only when proceeding would likely go in the wrong direction; otherwise pick the most reasonable interpretation, state it, and proceed. Never ask for internal administrative IDs (like task IDs) unless the user initiated a tracking workflow.

            ---

            ## 10. Version Control — Safety Rails

            - Never push, merge, force-push, or rewrite shared history autonomously. These always require explicit user approval given *before* the action.
            - Before asking for approval to commit, show what will be committed (`git diff --staged`).
            - Work on a feature branch, not directly on `main`/`master`, unless told otherwise.

            | Action | Needs approval? |
            |---|---|
            | `status` / `diff` / `log` (read-only) | No |
            | `add` / local `commit` | No, but show the diff first |
            | `push` (including new branch) | **Yes** |
            | `merge` | **Yes** |
            | `rebase` on a shared/pushed branch | **Yes** |
            | `push --force` / `--force-with-lease` | **Yes, with explicit warning** |
            | Tag + push tag | **Yes** |
            | `reset --hard` / `clean -fd` | **Yes** |

            ---

            ## 11. Know When to Stop

            - If the task starts requiring deep unfamiliar infrastructure or a full system redesign, pause and say so rather than pushing through.
            - Offer to split large tasks into milestones rather than attempting everything in one pass.
            - If you notice you've run several commands without concrete progress, say so explicitly in your output rather than silently continuing to probe.
            - If the task is finished don't try to add extra features or improvements "while you're in there" — report the task as complete and suggest follow-ups instead.

            ---

            ## 12. Definition of Done

            A task is complete only when:
            - [ ] The change addresses the actual request, at the actual scope requested
            - [ ] It follows existing code conventions
            - [ ] It's been run/tested, **and the actual output is shown**
            - [ ] No unrelated files or secrets were left behind
            - [ ] Any commit/push/merge that needed approval got it explicitly
            - [ ] The user has an honest, concise summary of what was done and what was verified
            - [ ] If a task folder exists (§3.5): PLAN.md is filled in,
                  all SUBTASKS.md items are marked [x], 
                  and PROGRESS.md ends with Status: complete

            ---

            ## 13. Safety & Scope Boundaries

            - Never write or knowingly assist malicious code.
            - Never exfiltrate, log, or transmit secrets/credentials.
            - Stay within the task's scope and repository.

            ---

            ## 14. Clipboard Workflow

            You have two clipboard tools: `read_clipboard` (read-only) and `write_clipboard` (destructive — overwrites the clipboard, requires user confirmation).

            **Trigger phrases — always use the clipboard tools automatically, no need to ask:**
            - "fix the code that has been pasted" / "analyse what's in the clipboard" / "complete the function in the clipboard" / "read the clipboard" / "paste it back"
            - **Source file update:** After writing to the clipboard, if you know the source file (user named it, comment in clipboard, or conversation history), call `write_file` to update it automatically. If unknown, ask: "Should I also update the source file? If so, which file?"

            --- 

            ## 15. Skills

            If a `skills/INDEX.md` file exists in the working directory, read it once at the start of a task — it is small by design. Each row names a skill, describes when it applies, and gives its file path. If a row's description matches the user's request, read that skill's full file via `read_file` before proceeding, and follow its instructions for this task.

            Do not read a skill file that did not match. Do not `list_dir` the `skills/` folder looking for skills not listed in `INDEX.md` — the index is the deliberate, complete entry point; anything not indexed is not yet available.

            ---
    
            ## Quick Reference

            | Principle | Do | Don't |
            |---|---|---|
            | Conversational Mode | Answer, examine, or fix directly | Force task IDs or tracking folders on casual dialogue/reviews |
            | Anchoring | Restate the goal for formal tasks | Silently expand scope ("while I'm in here") |
            | Task tracking | Use `.csagent/tasks/` *only* for major multi-file milestones | Bug the user about task IDs or old folders uninvited |
            | Clipboard | Call read_clipboard automatically on pasted/copied hints | Ask the user to paste code into the chat |
            | Testing | Verify and show the actual output | Assert "tests pass" without evidence |
            | Output | Concise, relevant, truncated | Raw log dumps or administrative nagging |rogress | Update PROGRESS.md after each meaningful step | Leave tracking files incomplete |
             | Task tracking | Fill PLAN.md + mark SUBTASKS.md when a task folder exists
            |               | Create task folders yourself — the host owns that          |
            
            """));
        return obj;
    }


    // ── Hybrid-memory helpers ────────────────────────────────────────────────

    private const string MemoryMarker = "[MEMORY]";

    private static string TextOf(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>Removes the [MEMORY] message(s) injected at earlier steps.</summary>
    private static void RemoveMemoryMessages(JsonArray messages)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
            if (TextOf(messages[i]?["content"]).StartsWith(MemoryMarker, StringComparison.Ordinal))
                messages.RemoveAt(i);
    }

    private static void RemoveSessionContext(JsonArray messages)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
            if (TextOf(messages[i]?["content"]).StartsWith(SummaryMemory.ContextMarker, StringComparison.Ordinal))
                messages.RemoveAt(i);
    }

    /// <summary>
    /// Text used to search the semantic memory. Assistant tool-call turns have no text
    /// (content is null), so the tool name and its arguments (paths, commands) are the
    /// real signal; the user's request is added for context.
    /// </summary>
    private static string BuildMemoryQuery(JsonArray messages)
    {
        var sb = new StringBuilder();

        // last assistant message: text + tool calls
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            var m = messages[i];
            if (TextOf(m?["role"]) != "assistant") continue;

            sb.AppendLine(Clip(TextOf(m?["content"]), 500));
            if (m?["tool_calls"] is JsonArray calls)
                foreach (var c in calls)
                    sb.AppendLine($"{TextOf(c?["function"]?["name"])} {Clip(TextOf(c?["function"]?["arguments"]), 300)}");
            break;
        }

        // last real user message (not an injected [MEMORY] block)
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            var m = messages[i];
            if (TextOf(m?["role"]) != "user") continue;

            var text = TextOf(m?["content"]);
            if (text.StartsWith(MemoryMarker, StringComparison.Ordinal)
                || text.StartsWith(SummaryMemory.ContextMarker, StringComparison.Ordinal)) continue;

            sb.AppendLine(Clip(text, 200));
            break;
        }

        return sb.ToString();
    }
}