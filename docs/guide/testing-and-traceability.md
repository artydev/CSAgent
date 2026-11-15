# Testing and documentation traceability

The test suite is the primary behavioural index for this documentation. Tests are especially useful when they assert externally observable results, priorities, failure handling and edge cases.

## Running tests

The root README documents the C# test suite and its invocation. Use the command currently documented by the repository README, for example:

    dotnet run --project Tests -c Release

The Python client has a separate standard-library test suite:

    cd python
    python3 test_csagent_client.py

Run both suites after changing shared API behaviour or the Python client. The exact C# test project file and runner are determined by current repository contents; do not assume a conventional xUnit/NUnit project based only on test method naming.

The README reports a passing test snapshot, but that is a historical repository statement, not a live test run performed while writing this guide. Always run tests against the commit being reviewed.

## Behavioural test index

| Test file | Documentation area | Notes |
|---|---|---|
| [ModelRouterTests.cs](../../Tests/ModelRouterTests.cs) | Model routing | Default profiles, overrides, request classification |
| RoutingRulesTests.cs | Model routing | Custom routing rules and reload scenarios |
| [ApiModeTests.cs](../../Tests/ApiModeTests.cs) | API configuration | API flags, defaults, host/key parsing, approval aliases |
| [AgentEndToEndTests.cs](../../Tests/AgentEndToEndTests.cs) | Agent and memory | Mock-backed execution and memory interactions |
| [CommandFilterTests.cs](../../Tests/CommandFilterTests.cs) | Security | Dangerous command patterns and refusal behaviour |
| [ToolSelectorTests.cs](../../Tests/ToolSelectorTests.cs) | Tool exposure | Core tools, groups, language cues, history and MCP selection |
| McpTests.cs | MCP | MCP integration scenarios |
| McpStdioTests.cs | MCP | stdio transport/lifecycle scenarios |
| MemoryLayerTests.cs | Memory | Memory layer |
| SemanticTests.cs | Memory | Semantic retrieval |
| PersistenceTests.cs | Memory | Persistence and recovery |
| MemoryFolderTests.cs | Memory | Memory directory handling |
| ConcurrencyTests.cs | Memory | Concurrent access |
| DistillationTests.cs | Memory | Distillation enabled |
| NoDistillTests.cs | Memory | Distillation disabled |
| TranscribeTests.cs | Audio | Transcription |
| CalendarTests.cs | Integrations | Calendar behaviour |
| MsgTests.cs, GroupMsgTests.cs | Integrations | Message/mail behaviour |
| DownloadTests.cs | Tools | Downloads |
| ScriptModeTests.cs, QuietModeTests.cs | Runtime modes | Script and quiet behaviour |

Some files are listed in the root README test inventory; links are omitted where the exact file was not inspected in this documentation pass.

## What a good behavioural test documents

A test is most useful as a specification when it makes the following explicit:

- **Given** — relevant configuration, environment, platform and initial state.
- **When** — the public operation or user action.
- **Then** — observable output, state change, tool call, error or event.
- **Boundary** — expected behaviour for invalid, missing, duplicate or dangerous input.
- **Isolation** — how environment variables, current directory, caches and temporary files are restored.

For example, the reviewed model-routing tests save and restore environment variables, reset LLM configuration and clear a model catalogue cache. Tool-selection tests assert both which tools are present and which should remain absent. Negative assertions document exclusions, not just positive capability.

## Evidence labels

- **Verified by test** — an assertion directly covers the statement.
- **Described by README/source** — documented in project materials or evident in production configuration, but not independently verified by a test here.
- **Guidance** — recommended operational practice, not a claim enforced by the suite.

Avoid words such as “always”, “never” or “guaranteed” unless the relevant contract is explicit and appropriately tested.

## Suggested traceability workflow

1. Identify the user-visible capability and entry point.
2. Locate its test group and read the full arrange/act/assert flow.
3. Read production implementation to understand defaults and failure paths.
4. Record positive and negative cases.
5. Write the user-facing explanation and link to tests.
6. Mark important behaviour that has no test.
7. When code or tests change, review linked documentation.

A lightweight feature matrix can live beside the tests:

| Feature | Test file(s) | User guide | Gap to review |
|---|---|---|---|
| Model routing | ModelRouter, RoutingRules | architecture | Are fallback/error cases covered? |
| API mode | ApiMode | modes and configuration | Are authentication and remote-host cases covered? |
| Command safety | CommandFilter | tools and security | Are bypasses and platform variants covered? |
| Tool selection | ToolSelector | tools and security | Are new tools assigned to a group? |
| Memory | MemoryLayer, Persistence, Distillation | memory and integrations | Are migration/corruption cases covered? |
| Python client | Python unittest file | python client | Are all SSE events covered? |

## CI recommendation

A future CI workflow should run the C# suite and Python client tests on supported operating systems. For changes to user-visible behaviour, consider a documentation review checklist or link/heading check. Documentation checks should not replace tests, and tests should not replace user-facing explanations.
