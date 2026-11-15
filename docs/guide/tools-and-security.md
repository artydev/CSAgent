# Tools and security

CSAgent is an agent that can act on a user's machine. Tool selection, dispatch validation, command filtering and user confirmation are important parts of its safety boundary.

This chapter focuses on behaviour represented in reviewed tests and the README. It is not a formal security audit.

## Tool selection

The tool selector builds the tool definitions sent to the model. [ToolSelectorTests.cs](../../Tests/ToolSelectorTests.cs) specifies the following behaviours:

- Native tools are organised into a core set and named feature groups.
- Each native tool belongs to at most one declared group in the tested catalogue.
- An ordinary coding request receives core tools such as file reading/editing, shell execution and file search.
- Request wording can activate groups. Tested examples include Git, web search, mail, audio transcription, Office/Excel, clipboard, archives, terminal sessions and model listing.
- English and French requests are both represented.
- Tools already used in a conversation can remain available for a follow-up.
- The enable_tools meta-tool can activate a group and reports an error for an unknown group.
- --all-tools requests all native tool definitions.
- MCP tools are included based on availability, size and request relevance in tested scenarios.

The tests also verify that unrelated requests do not activate some optional groups, reducing irrelevant tool descriptions sent to the model.

## Command filtering

[CommandFilterTests.cs](../../Tests/CommandFilterTests.cs) exercises dangerous-command detection for Windows and Unix-like command strings.

| Platform | Examples expected to be blocked |
|---|---|
| Windows | format, registry deletion, adding a user, shutdown, forced deletion, access to protected Windows system paths |
| Unix-like | dd, sudo, chmod 777, access to /etc/, filesystem formatting, reboot |

The tests also include safe lookalikes and context cases. For example, Git commands containing “add” must not be confused with the Unix dd command. Tests check command separators and embedded command fragments, not just a single bare command.

### Refusal details

The test suite verifies that when a dangerous pattern is found, the refusal:

- Names the blocked pattern.
- Tells the caller the command is not allowed.
- Suggests rewriting the command where appropriate.
- Is surfaced through multiple tool entry points, including sh, run_terminal and excel_command.

This matters because the model receives a concrete reason rather than an opaque failure, allowing it to propose a safer alternative.

**Important limitation:** a command filter is not a complete operating-system sandbox. It cannot replace least-privilege execution, filesystem permissions, isolation, backups or human review. Do not treat passing tests as proof that arbitrary shell commands are safe.

## Confirmations and automatic approval

The README and API-mode tests distinguish confirmation policy from filtering:

- By default, API mode does not automatically approve tool calls.
- --yes, -y and --auto-approve enable automatic approval.
- Automatic approval removes the interactive confirmation step; it does not disable the dangerous-command filter or path restrictions.

Use automatic approval only in a controlled environment with an appropriate user account, working directory, backups and network policy.

## Safe operating practices

1. Run the agent with the minimum operating-system permissions needed.
2. Use a dedicated working directory for tasks that may edit or create files.
3. Review changes before committing, publishing or deploying them.
4. Avoid running with administrator/root privileges.
5. Keep API mode on localhost unless remote orchestration is required.
6. Protect remotely reachable API mode with a key and network restrictions.
7. Keep backups of important files and review destructive operations.
8. Do not place credentials in prompts, logs, test fixtures or committed configuration.

## Security-related tests

| Test file | What it helps specify |
|---|---|
| [CommandFilterTests.cs](../../Tests/CommandFilterTests.cs) | Platform-specific dangerous command patterns and refusal messages |
| [ApiModeTests.cs](../../Tests/ApiModeTests.cs) | API host, API key and approval option parsing |
| [ToolSelectorTests.cs](../../Tests/ToolSelectorTests.cs) | Which tools are exposed for a request and how groups are enabled |

A security review should additionally inspect production implementation, permissions model, path validation, HTTP authentication, logging and all tool dispatch paths.
