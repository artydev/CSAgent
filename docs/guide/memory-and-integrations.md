# Memory and integrations

CSAgent includes memory-related components and a range of integrations. The root README describes a hybrid memory system, session distillation, MCP servers, skills, transcription and other tools. The test directory includes dedicated suites for several of these areas.

The exact persistence formats and lifecycle details can evolve. This chapter links to relevant tests rather than claiming every implementation detail is a permanent public contract.

## Conversation memory

The README describes hybrid memory and session distillation as mechanisms intended to reduce loss of useful context across turns and sessions.

| Test file | Area indicated by the suite |
|---|---|
| MemoryLayerTests.cs | Memory layer behaviour |
| SemanticTests.cs | Semantic memory behaviour |
| PersistenceTests.cs | Persistence and recovery |
| MemoryFolderTests.cs | Memory folder handling |
| ConcurrencyTests.cs | Concurrent memory operations |
| DistillationTests.cs | Session distillation |
| NoDistillTests.cs | Behaviour when distillation is disabled |
| AgentEndToEndTests.cs | Agent-level memory interactions |

The end-to-end tests construct a real CodingAgent with mock LLM responses and test memory retrieval and memory-related message handling. Consult persistence and concurrency suites before changing storage or lifecycle semantics.

### Operational advice

- Treat memory files as user data, not disposable build artifacts.
- Back up memory directories before migrations or manual edits.
- Avoid sharing memory files that may contain project details, prompts or personal information.
- Use isolated temporary directories in persistence tests and clean up test state.
- Document schema changes and migration behaviour alongside the corresponding tests.

## Session distillation

The README describes distillation as a way to preserve useful information between sessions. Separate DistillationTests.cs and NoDistillTests.cs indicate that enabled and disabled paths are both considered.

The precise prompt format, summarisation policy and persistence timing should be verified against current implementation and tests before being treated as a stable API.

## MCP servers

The test inventory includes McpTests.cs and McpStdioTests.cs. Tool-selection tests also cover conditional inclusion of MCP tool definitions and the ability to expose an MCP group when the request or prior use indicates relevance.

When debugging MCP integrations, separate these questions:

1. Is the MCP server configured and reachable?
2. Does the server expose the expected tools?
3. Are the tool definitions selected for this request?
4. Is the selected tool dispatched successfully?
5. Are errors and results surfaced to the conversation?

The named tests are the starting point for protocol and stdio lifecycle details. This guide intentionally does not invent configuration keys or transport guarantees not established in reviewed source.

## Skills

The repository contains a skills/ directory, and the project file copies skill resources to the publish directory. The README describes skills as an extension mechanism.

Keep skill descriptions concise and explicit about when they should be activated. Avoid including secrets or untrusted executable payloads in skill resources. For exact discovery and invocation behaviour, consult the current skill implementation and associated tests.

## Audio and transcription

The README describes recording in Web UI and Lean UI, chunked uploads, transcription and two user choices: use the transcript as the next instruction or keep it as text for attachment to a later request.

The README names these API routes:

- POST /api/audio/start
- POST /api/audio/{id} for an audio chunk
- POST /api/audio/{id}/transcribe for transcription progress via SSE

It also describes optional cleanup of old audio in recordings/ using CSAGENT_AUDIO_KEEP_DAYS. This is disabled by default; the README states that transcripts are not deleted by this cleanup.

The test inventory includes TranscribeTests.cs. Confirm current server limits and supported codecs in implementation before relying on a specific deployment configuration.

## Other integrations

The test inventory names additional feature suites:

- CalendarTests.cs — calendar-related behaviour.
- MsgTests.cs and MsgTestFile.cs — message/mail-related behaviour.
- GroupMsgTests.cs — grouping messages.
- DownloadTests.cs — download behaviour.
- ScriptModeTests.cs — script mode.
- QuietModeTests.cs — quiet mode.
- Clipboard infrastructure — clipboard-related operations.

These test names are navigation hints, not exhaustive feature specifications. Read test assertions and production service before changing user-visible behaviour.
