# CSAgent — Cross-Platform Autonomous Coding Agent.

**CSAgent** is a cross-platform autonomous coding agent that runs on Windows, Linux, and macOS. It uses an OpenAI-compatible API (e.g., [Albert API](https://albert.api.etalab.gouv.fr)) to understand natural-language instructions and autonomously perform coding tasks by reading, writing, and listing files, as well as executing shell commands.

It ships with three presentation modes — a terminal UI (TUI), a web UI, and a lean UI.

---

## Table of Contents.

- [Quick Start](#quick-start)
- [Modes of Operation](#modes-of-operation)
  - [CLI Mode (Default)](#cli-mode-default)
  - [Web UI Mode](#web-ui-mode)
  - [Lean UI Mode](#lean-ui-mode)
  - [Headless API Mode (for orchestrators)](#headless-api-mode-for-orchestrators)
- [LLM Models](#llm-models)
- [Future Features](#future-features)
- [Environment Variables](#environment-variables)
- [Command-Line Arguments](#command-line-arguments)
- [Safety Features](#safety-features)
- [Available Tools](#available-tools)
  - [MCP servers](#mcp-servers)
- [Memory & Conversation Persistence](#memory--conversation-persistence)
  - [Hybrid Memory (anti-amnesia)](#hybrid-memory-anti-amnesia)
  - [Session Distillation (across sessions)](#session-distillation-across-sessions)
  - [Memory files](#memory-files)
  - [Code layout](#code-layout)
- [Building from Source](#building-from-source)
- [Tests](#tests)
- [AOT Publishing](#aot-publishing)
- [Troubleshooting](#troubleshooting)

---

## Quick Start

### Prerequisites

- .NET 10.0 SDK or later (for building from source)
- An API key for an OpenAI-compatible endpoint (e.g., [Albert API](https://albert.api.etalab.gouv.fr))

### Run with the Web UI

```bash
# Set your API key
set ALBERT_API_KEY=your-api-key-here

# Run the web server
csagent --ui
```

Then open your browser to **http://localhost:5050** (or the port you chose with `--port`).

### Run in CLI Mode

```bash
set ALBERT_API_KEY=your-api-key-here
dotnet run
```

---

## Modes of Operation

### CLI Mode (Default)

In CLI mode, CSAgent presents a text-based interactive session. You type instructions, and the agent autonomously works through them step by step.

```
> User: Create a new C# console project that prints "Hello, World!"
```

The agent will:
1. Think about the task
2. Execute tools (write files, run shell commands)
3. Report results
4. Continue until the task is complete

Type `exit` to quit the session.

### Web UI Mode

In Web UI mode (`--ui` flag), CSAgent starts a local web server with a modern, dark-themed interface featuring:

- Real-time streaming of agent thoughts, tool calls, and results via Server-Sent Events (SSE)
- Syntax highlighting for code blocks (via Prism.js)
- Responsive design for desktop and mobile
- A clean, terminal-inspired aesthetic

The web UI is served at **http://localhost:5050** by default. Use `--port <n>` (or `-p <n>`) to change the port.

#### Voice recorder (Web UI and Lean UI)

The 🎙 button records from the browser. Long recordings are fine: audio is uploaded in 30-second chunks to `recordings/`. (In Lean UI the transcript and the two choices are printed in the log.) When you stop, the audio is transcribed through the LLM endpoint (Albert, Whisper; needs `ffmpeg` for recordings over a few minutes, see `transcribe_audio`). You then choose:

- **Use as instruction**: the text is put in the prompt box.
- **Keep as text**: the transcript is saved to `transcripts/<name>.txt` and attached to your next prompt as `[Attached text file: path]` (data for the agent, e.g. to email as an attachment).

Next to the button, **FR / EN / Auto** sets the language you speak (it helps Whisper, and is remembered by the browser). On the first visit it follows the browser's language when that is French or English; **Auto** lets Whisper detect it.

Old audio can be deleted automatically: set `CSAGENT_AUDIO_KEEP_DAYS=30` and, at each start of `--ui` / `--leanui`, the audio files of `recordings/` not modified for 30 days are removed. It is off by default, and transcripts are never deleted.

Endpoints (same-origin only): `POST /api/audio/start`, `POST /api/audio/{id}` (chunk), `POST /api/audio/{id}/transcribe` (SSE progress). Recordings and transcripts stay on disk; you may want `recordings/` and `transcripts/` in `.gitignore`.

### Lean UI Mode

Lean UI mode (`--leanui` flag) is a lightweight, terminal-style variant of the Web UI. It has its own embedded assets (`Presentation/LeanUI/assets`) and the same SSE-based chat endpoints, launched via the `--leanui` command-line argument. It is served at **http://localhost:5050** by default (use `--port <n>` to change it).

### Headless API Mode (for orchestrators)

`--api` starts the same SSE server as the Web UI **without any web interface**: no HTML/JS/CSS routes, no browser opened, no clipboard access. It is meant to be driven by another program, such as an agent orchestrator.

```bash
set ALBERT_API_KEY=your-llm-key

# Local, no confirmations
csagent --api --yes

# Reachable from other machines: an API key is mandatory
csagent --api --yes --host 0.0.0.0 --port 8080 --api-key s3cret
```

| Option | Effect |
|---|---|
| `--api` | Headless mode (takes precedence over `--ui` / `--leanui`). |
| `--yes`, `-y`, `--auto-approve` | Every tool call is approved automatically: the agent never waits for `POST /api/confirm`. Also works in CLI, `--ui` and `--leanui` modes. |
| `--host <addr>` | Address to listen on (default `localhost`). Only used with `--api`; the Web UI modes always listen on `localhost`. |
| `--api-key <key>` | Every request must present this key. Can also be set with the `CSAGENT_API_KEY` environment variable (preferred: a command-line value is visible in the process list). |

**`--yes` only removes the confirmation prompts.** The shell command filter (`sudo`, `chmod`, `shutdown`, `/etc/`, …) and the path restrictions stay active.

**Endpoints**

| Endpoint | Description |
|---|---|
| `GET /api/chat?prompt=…[&task=slug]` | Run a prompt, response is a Server-Sent Events stream |
| `POST /api/chat` | Same, as `multipart/form-data` (`prompt`, optional `task`, optional `image`) |
| `POST /api/confirm` | Body `true` or `false`: answers a pending `confirm` event (not needed with `--yes`) |

**Authentication.** When a key is set, send `Authorization: Bearer <key>` or `X-API-Key: <key>`; anything else gets `401`. The server speaks plain HTTP: when it is exposed beyond the local machine, put it behind a TLS reverse proxy. **Without a key, a non-local `--host` is refused at startup**, because the agent can run commands and write files.

**Events.** Each SSE message is `data: {"id": <n>, "type": "<type>", "data": …}`:

| `type` | `data` |
|---|---|
| `step` | `{"n": 1, "m": 30}`: step number / maximum steps |
| `thought` | The model's text |
| `call` | `{"n": "<tool>", "a": "<arguments JSON>"}` |
| `result` | `{"r": "<output>", "e": false}`: `e` is `true` when the tool failed |
| `confirm` | `{"tool": "<tool>"}`: waiting for `POST /api/confirm` (never sent with `--yes`) |
| `done` | The task is complete |
| `error` | A fatal error for this request |
| `warning`, `danger` | Notices (for example the session-summary line) |

`done` is not always the last message (a `warning` about the session summary can follow): read until the server closes the connection.

```bash
curl -N -H "Authorization: Bearer s3cret" \
     "http://localhost:8080/api/chat?prompt=create+hello.txt+containing+hi"
```

**One conversation per server.** All requests share the same memory folder (`--mem`) and conversation, so send one request at a time. To run several independent tasks in parallel, start one instance per task, each with its own `--mem` name and `--port`.

### Vision / Image Attachments

CSAgent supports **multimodal (vision) prompts** — you can attach an image to a prompt and the agent will analyze it. This works in both the Web UI and the Lean UI, and in the CLI/TUI.

- **Web UI / Lean UI:** click the **📎** (paperclip) button next to the input box to attach an image. A small preview appears; click **✕** to remove it before sending. Supported formats: **PNG, JPEG, GIF, WebP** (max **10 MB**).
- **CLI / TUI:** image attachment is handled through the conversation history — once a vision exchange has occurred, the agent automatically keeps using the vision-capable model for the rest of the session.

When an image is attached, CSAgent automatically routes the request to a **vision-capable model** (`gemma-4-31b-it` by default) instead of the standard text model. This routing is automatic and happens in two cases:

1. The current prompt includes an attached image.
2. The conversation history already contains an image from a previous exchange (text-only models reject requests whose history contains image content).

You can change the vision model with `--vision-model` (or `CSAGENT_VISION_MODEL`); `--model` forces one model for every message, images included. The default is defined by `LlmSettings.VisionModel`.

---

## LLM Models

CSAgent chooses the model for **each message**, according to what you ask. There are three profiles, the same in every mode (CLI, `--ui`, `--leanui`, `--api`):

| Profile | Default model | Used when |
|---|---|---|
| **Code** | `deepseek-v4-flash` | Code, files, shell, mail, audio, links... and anything that is not clearly a general question. This is the model that runs the tools |
| **Chat** | `openweight-large` (Albert alias of `gpt-oss-120b`) | A general conversation with nothing to do with code or files: an explanation, a question of culture, a text to write, advice |
| **Vision** | `gemma-4-31b-it` | An image is attached or already in the conversation; see [Vision / Image Attachments](#vision--image-attachments) |

How the choice is made (no extra LLM call, no delay):

1. `--model <name>` always wins, for every message.
2. An image in the conversation selects the vision model.
3. A message that mentions code, a file or a path (`Program.cs`, `src/Core`), a link, a command, mail, audio, git, tests... selects the code model, and so does a short follow-up (8 words or fewer) to a turn that used tools ("yes, go ahead").
4. Otherwise it is a general conversation and the chat model answers.

When in doubt the code model is kept, so only clearly general messages change model. The CLI prints the model before each answer, with the reason when it is not the default, for example `[model: openweight-large (chat: general question)]`; the web interfaces show the same line when the chat model answers.

Before using the chat model, CSAgent checks it against the endpoint's model list (the same list as the `list_models` tool, fetched once and kept for 10 minutes). If the model is unknown, reported unavailable, or not a text model, the code model answers instead and the reason is shown. If the list cannot be fetched, the chat model is used anyway.

The aliases `openweight-*` are the ones of the Albert API: they stay the same when a model is upgraded to a new version. On a custom `--endpoint` (Ollama...) there is no chat model unless you set one with `--chat-model`, so every message uses the code model, as before.

Settings:

| Setting | Effect |
|---|---|
| `--code-model <name>` / `CSAGENT_MODEL_CODE` | Model of the code profile |
| `--chat-model <name>` / `CSAGENT_MODEL_CHAT` | Model of the chat profile |
| `--vision-model <name>` / `CSAGENT_VISION_MODEL` | Model of the vision profile |
| `--no-route` / `CSAGENT_ROUTING=off` | Always use the code model (no automatic choice) |
| `--model <name>` | One model for everything (beats all of the above) |

The chat model must support **tool calling**, because the agent loop can use tools in any message. Check a model with `csagent --model <name> "list the files here and summarise the README"` before making it your chat model.

### Local models with Ollama

Any OpenAI-compatible server works. With [Ollama](https://ollama.com):

```bash
ollama pull qwen2.5-coder:14b
csagent --endpoint http://localhost:11434/v1 --model qwen2.5-coder:14b
```

- No API key is needed for a local endpoint (`localhost`, `127.x`, `::1`); a remote endpoint still requires `ALBERT_API_KEY`.
- Pick a model that supports **tool calling** (e.g. `qwen2.5-coder`, `qwen3`, `llama3.1`); the agent loop depends on it.
- Images need a vision model: `--vision-model llava` (otherwise the default `gemma-4-31b-it` is requested, which Ollama does not have).
- Set `CSAGENT_ENDPOINT` to avoid retyping the URL. Works in every mode (`--ui`, `--leanui`, `--api`).

### Examples

```bash
# CLI mode with a different model
csagent --model gpt-4o

# Web UI mode with a different model
csagent --ui --model deepseek-v4-flash
```

---

## Future Features

The following capabilities are planned for future releases:

- **Python scripting** — drive CSAgent from Python scripts: launch sessions, send prompts, and retrieve responses and agent events (steps, tool calls, results) programmatically, for example via a csagent module or a web-interface (SSE) client.

---

## Environment Variables

| Variable | Required | Description |
|---|---|---|
| `ALBERT_API_KEY` | Yes, except for a local endpoint | Your API key for the OpenAI-compatible endpoint. Not needed when `--endpoint` points to this machine (localhost, 127.x, ::1) |
| `CSAGENT_ENDPOINT` | No | Same as `--endpoint` (the argument wins) |
| `CSAGENT_VISION_MODEL` | No | Same as `--vision-model` |
| `CSAGENT_MODEL_CODE` | No | Same as `--code-model` (default `deepseek-v4-flash`) |
| `CSAGENT_MODEL_CHAT` | No | Same as `--chat-model` (default `openweight-large` on the Albert endpoint) |
| `CSAGENT_ROUTING` | No | `off` (or `0`, `false`, `no`) turns the automatic model choice off, like `--no-route` |
| `CSAGENT_TRANSCRIBE_MODEL` | No | Speech-to-text model used by `transcribe_audio` (default `openai/whisper-large-v3`) |
| `CSAGENT_FFMPEG` | No | Path of `ffmpeg` for `transcribe_audio` when it is not in the `PATH` |
| `CSAGENT_AUDIO_KEEP_DAYS` | No | Web recorder: delete the audio of `recordings/` older than this many days at startup (default: keep everything; transcripts are never deleted) |
| `CSAGENT_API_KEY` | No | Key that clients must present in `--api` mode (same as `--api-key`) |
| `CSAGENT_MCP_URL` | No | Same as `--mcp` (the argument wins) |

---

## Command-Line Arguments

| Argument | Description |
|---|---|
| `--ui` | Start in Web UI mode (starts a web server) |
| `--leanui` | Start in Lean UI mode (lightweight, terminal-style variant of the Web UI) |
| `--api` | Start the headless SSE server, without web UI (see [Headless API Mode](#headless-api-mode-for-orchestrators)) |
| `--quiet`, `-q` | CLI, `--ui` and `--leanui`: show just the assistant's messages (plus warnings, errors and the final line). Steps, tool calls and results are hidden; a failed tool call is reported on one line, and a destructive action shows its tool call right before asking for confirmation. `--api` ignores it (orchestrators need every event) |
| `--yes`, `-y` | Approve every tool call automatically (no confirmation prompts; the shell command filter stays active) |
| `--host <addr>` | With `--api`: address to listen on (default: `localhost`; a non-local address requires an API key) |
| `--api-key <key>` | With `--api`: key required on every request (or set `CSAGENT_API_KEY`) |
| `--mcp <url>`, `--mcp-url <url>` | Connect to an MCP server over Streamable HTTP (see [MCP servers](#mcp-servers)). Or set `CSAGENT_MCP_URL` |
| `--mem <name>` | Memory folder holding the conversation and the memory files (default: `agent_memory`, see [Memory files](#memory-files)) |
| `--model <model>` | Use this model for every message (turns the automatic model choice off, see [LLM Models](#llm-models)) |
| `--code-model <name>` | Model for code, files and tools (default: `deepseek-v4-flash`) |
| `--chat-model <name>` | Model for general conversation (default on Albert: `openweight-large`; none on a custom `--endpoint`) |
| `--no-route` | Always use the code model (no automatic model choice) |
| `--endpoint <url>` | OpenAI-compatible base URL (default: Albert API). For Ollama: `http://localhost:11434/v1` (see [Local models with Ollama](#local-models-with-ollama)) |
| `--vision-model <name>` | Model used when the conversation contains an image (default: `gemma-4-31b-it`) |
| `--port`, `-p <n>` | Web UI port number (default: `5050`) |
| `--dry-run` | Simulate tool execution without making changes |
| `--max-retries <n>` | Max attempts for HTTP 429 (rate limit) retries (default: `3`) |
| `--retry-delay <ms>` | Base backoff delay in ms before the first retry (default: `1000`) |
| `--no-distill` | Do not summarise the session at the end of a run (saves one LLM call; see [Session Distillation](#session-distillation-across-sessions)) |
| `--help`, `-h`, `/?` | Display help and exit |
| `--version` | Display the current version of CSAgent and exit |
| `--doc` | Display this documentation in a nicely formatted terminal view and exit |
| `<name>` | Positional argument: memory name without the `--mem` flag |

### Examples

```bash
# Web UI with a custom memory (folder my_project_memory/)
csagent --ui --mem my_project_memory

# Lean UI mode
csagent --leanui

# Web UI on a custom port
csagent --ui --port 8080

# CLI mode with a specific memory (folder my_memory/)
dotnet run my_memory

# Dry run mode
csagent --dry-run

# Display version
csagent --version

# Display documentation in terminal
csagent --doc

# Override the LLM model in CLI mode
csagent --model gpt-4o-mini

# Override the LLM model in Web UI mode
csagent --ui --model deepseek-v4-flash

# Tune rate-limit retry behavior
csagent --max-retries 5 --retry-delay 2000

# Do not summarise the session at the end of the run
csagent --no-distill

# Headless SSE server for an orchestrator, no confirmations
csagent --api --yes

# Same, reachable from the network (API key required)
csagent --api --yes --host 0.0.0.0 --port 8080 --api-key s3cret
```

---

## Safety Features

CSAgent includes multiple layers of safety to prevent accidental damage to your system:

### 1. Destructive Action Confirmation

The `write_file` tool is classified as **destructive** because it modifies files on disk. Before executing, the agent will prompt for confirmation:

```
[?] Allow destructive action 'write_file'? [Y/n]
```

With `--yes` (or `-y`) these prompts are skipped and every tool call is approved automatically, which is what an unattended orchestrator needs (see [Headless API Mode](#headless-api-mode-for-orchestrators)). The two filters below stay active.

Shell commands (`sh`) are **not** classified as destructive by default, but they are still filtered for dangerous operations (see below).

### 2. Path Restriction

File operations (`write_file`, `read_file`, `list_dir`) are **restricted to the current working directory** and its subdirectories. Attempts to access files outside this scope are blocked:

```
Error: write_file - Path 'C:\Windows\System32\config' is not allowed for writing.
```

### 3. Dangerous Command Filtering

Shell commands are scanned for potentially dangerous patterns before execution. The filter is **platform-aware**:

#### Windows (cmd.exe)
Blocked patterns include:
- `format` — Format drives
- `del /f` / `del /s` — Force/recursive deletion
- `rd /s` / `rmdir /s` — Recursive directory removal
- `reg delete` / `reg add` / `reg import` — Registry manipulation
- `net user` / `net localgroup` / `net share` — System administration
- `takeown` / `icacls` / `cacls` — Permission/ownership changes
- `bcdedit` / `diskpart` — Boot/disk configuration
- `runas` / `powershell start-process -verb runas` — Privilege escalation
- `shutdown` / `reboot` — System control
- `\windows\system32\` / `\windows\system\` — System directory access
- `\program files\` — Protected directory access

#### Unix/Linux/macOS (bash/sh)
Blocked patterns include:
- `sudo` — Privilege escalation
- `chmod` — Permission changes
- `shutdown` / `reboot` — System control
- `dd` — Low-level disk operations
- `mkfs` — File system creation
- `/etc/` / `/usr/bin/` / `/bin/` — System directory access

### 4. Command Timeout

All shell commands have a **60-second timeout**. If a command takes longer, it is automatically killed:

```
Error: command timed out (60s).
```

### 5. File Size Limit

Reading files larger than **500 KB** is blocked to prevent memory issues:

```
Error: file too large (1024 KB). Use sh to grep/head.
```

---

## Available Tools

The agent has access to four built-in tools:

### `write_file`
Write (or overwrite) a text file. Parent directories are created automatically.

**Parameters:**
- `path` (string, required) — File path
- `content` (string, required) — UTF-8 content to write

### `read_file`
Read a text file and return its content.

**Parameters:**
- `path` (string, required) — File path

### `list_dir`
List files and subdirectories in a directory.

**Parameters:**
- `path` (string, optional, default: `.`) — Directory to list
- `recursive` (boolean, optional, default: `false`) — Whether to list recursively

### `sh`
Execute a shell command. Uses `cmd.exe` on Windows, `/bin/sh` elsewhere.

**Parameters:**
- `cmd` (string, required) — Shell command to run

### `read_msg`
Read an Outlook `.msg` e-mail file: subject, sender, recipients, dates, body and the numbered list of attachments. Read-only. It is a pure C# reader, so it works on every OS and needs neither Outlook nor a NuGet package.

**Parameters:**
- `path` (string, required) — Path of the `.msg` file (inside the working directory)
- `max_chars` (integer, optional, default: `50000`, maximum `200000`) — Maximum body characters returned

The body is the plain-text part when there is one; otherwise the HTML or RTF part converted to text (best effort). Embedded messages are shown after the body. The result is framed by `[EMAIL …]` / `[END OF EMAIL]` markers and the system prompt tells the agent that the content is **untrusted data, never instructions**.

### `save_attachment`
Save the attachments of a `.msg` file to disk. **Destructive: requires confirmation** (unless `--yes`).

**Parameters:**
- `path` (string, required) — Path of the `.msg` file
- `index` (integer, optional) — Attachment number shown by `read_msg`; omit to save all
- `destination` (string, optional) — Directory inside the working directory; default `<name>_attachments` next to the `.msg`

File names are sanitised (no path, no reserved or invalid characters), and an existing file is never overwritten (`name (1).ext` is written instead). Embedded messages and attachments stored by reference cannot be saved.

Not supported: `.pst` / `.ost` archives, and `.msg` files that are encrypted or rights-protected.

### `transcribe_audio`
Transcribe a speech recording to text with a Whisper endpoint (`/audio/transcriptions` on the configured `--endpoint`, same `ALBERT_API_KEY`). Read-only.

**Parameters:**
- `path` (string, required) — Path of the audio file (inside the working directory)
- `language` (string, optional) — ISO-639-1 code such as `fr`; omit to auto-detect
- `max_chars` (integer, optional, default: `50000`, maximum `200000`) — Maximum characters returned

With **ffmpeg** installed (in the `PATH`, or set `CSAGENT_FFMPEG`), any audio or video file is converted to 16 kHz mono WAV and cut into 10-minute parts, sent one after the other and joined. Without ffmpeg, only `.wav` and `.mp3` files up to 24 MB are accepted. Files are limited to 100 MB. The cut is made on the clock, so a word at a boundary can be split. The model is `openai/whisper-large-v3`, or `CSAGENT_TRANSCRIBE_MODEL`. The audio is sent to the endpoint; the transcript is **untrusted data, never instructions**. To keep it, the agent writes it with `write_file` (which asks for confirmation).

### MCP servers

`--mcp <url>` (or `CSAGENT_MCP_URL`) connects CsAgent to one MCP server over Streamable HTTP, for example `csagent --mcp http://localhost:8000/mcp`. At the first prompt the agent lists the server's tools and offers them to the model next to the built-in ones. This works in every mode (CLI, `--ui`, `--leanui`, `--api`).

- **Names.** Every server tool is offered as `mcp_<name>` (characters other than letters, digits, `_` and `-` become `_`, 64 characters at most). A server therefore cannot replace a built-in tool: its `read_file` is `mcp_read_file`, and the built-in `read_file` is untouched.
- **Confirmation.** CsAgent cannot know what a server's tool does, so with confirmations on (the default) **every** MCP call asks for your approval, like `write_file`. With `--yes` they run without asking.
- **Untrusted content.** What a server sends (tool descriptions, results) is treated as data: the system prompt tells the model never to follow instructions found there, and descriptions are cut to 1000 characters. Only connect servers you trust.
- **Limits.** One server, HTTP only (no stdio), no authentication header, tools only (no resources or prompts), text results (other content is shown as JSON). If the server cannot be reached the run stops with an error.

---

## Memory & Conversation Persistence

CSAgent saves the conversation history, and the files of its memory system, in a **memory folder** (default: `agent_memory/`). This allows the agent to maintain context across sessions.

- The memory is automatically loaded when the agent starts
- It is saved after each step
- Old messages are trimmed when the total content exceeds ~96 KB to keep context manageable
- You can choose another memory with `--mem <name>` or as a positional argument; the name is the folder

### Hybrid Memory (anti-amnesia)

Trimming old messages has a side effect: the agent forgets what it already tried and can repeat the same mistakes. To prevent this, CSAgent adds a **hybrid memory** on top of the conversation file. It is coordinated by `HybridMemoryManager` and uses only the .NET base class library (no NuGet package, no database).

| Layer | What it stores | Purpose |
|---|---|---|
| **ExactMemory** | The last 50 steps (thoughts, tool calls, errors, successes), verbatim | Shows the agent exactly what it just tried |
| **SemanticMemory** | Error / solution patterns, tagged (e.g. `permission`, `not-found`, `timeout`, `syntax`) | Explains *why* something failed and what worked instead |

Example: writing `/opt/config.json` fails with *permission denied*, then `/home/config.json` succeeds. Both facts are recorded, so the next time a similar write is attempted the agent is reminded to use the location that worked.

From step 4 onward, relevant entries are injected as a single `[MEMORY]` message just before each LLM call (the previous one is replaced, never stacked), so they stay visible even after `TrimHistory()` has removed older messages.

**How lessons are found.** The query is built from the agent's latest text, the tool call it is about to make (name and arguments) and the last user message. It is reduced to keywords (English and French stop words removed, accents folded, plurals folded, paths split on `/`), and each stored lesson scores one point per keyword it contains. The three best lessons are injected, best score first, then most recent, then most frequent.

**How the store stays small and useful.**
- *De-duplication*: the same lesson (ignoring case, accents, extra spaces and numbers such as line numbers) is stored once; a counter and a last-seen date are updated, and the prompt shows `(seen 3x)`.
- *Cap*: at most 200 lessons are kept; the least seen and oldest are evicted first.
- *Solutions only after an error*: a success becomes a lesson only if the same tool failed within the previous 5 steps. Only the arguments are stored, never the result, so file contents do not end up in long-term memory.
- *Error messages* are clipped to 300 characters.

**Safe saving.** Memory files are written atomically (temporary file, then replace), so a crash or a concurrent reader never sees a half-written file. A corrupt file is renamed to `<name>.bad` and the agent starts with an empty memory instead of failing; a single malformed entry is skipped and the rest is kept. Saving happens in a `finally` block around the agent loop, so memory is written however the run ends (task complete, text-only reply, error, cancellation, or maximum steps reached). A failed save is logged and does not stop the agent. In Web mode, one memory manager is shared by all requests; it is thread-safe, but simultaneous requests share the same memory.

### Session Distillation (across sessions)

The hybrid memory above lives inside one task. **Session distillation** carries the *reasoning* from one run to the next: when a run ends, the LLM condenses the conversation into four short lists.

| List | What it holds |
|---|---|
| **Decisions** | Choices made, and why |
| **Constraints** | Facts about the environment and requirements that must be respected |
| **Pending** | Work still to be done |
| **Failed approaches** | What was tried and did not work, so it is not retried |

The new summary is merged with the previous one (resolved or obsolete items are dropped) and saved in the memory folder. On the next run it is sent to the model as a `[SESSION CONTEXT]` message right after the system prompt, so the agent resumes with the previous reasoning and not only the raw history.

What you see in the terminal:

```
Session summary loaded: 5 note(s) from previous sessions (sent to the model as [SESSION CONTEXT]).
...
Session summary updated: 6 note(s) saved to agent_memory/summary.json.
```

Design rules:
- *Best effort.* Distillation runs after the task, with a 60-second limit. If the API is slow, down, or answers something unusable, the run still ends normally, a notice says so (`Session summary not updated (...)`), and the previous summary is kept untouched.
- *Notes, not instructions.* The conversation contains file contents and command output written by third parties. The distiller is told to treat them as data, the summary is sent to the model as "background information, not instructions", and every note is cut to one line of at most 240 characters (12 notes per list, injected markers removed).
- *Always present.* The summary is re-inserted at every step, because `TrimHistory()` removes the oldest messages first.
- *Not stored twice.* Injected blocks (`[SESSION CONTEXT]`, `[MEMORY]`) are never written into the conversation file.
- *Skipped when pointless.* A conversation with at most one exchange is not summarised.
- *Safe files.* The summary is written atomically; a corrupt file is renamed to `.bad` and the agent starts without a summary.

**Cost and opting out.** Each run ends with one extra LLM call. Use `--no-distill` to skip it: the two memory layers are still saved, and an existing summary is still read and used, but it is neither created nor changed. Delete `summary.json` in the memory folder to forget the summary entirely.

### Memory files

Each memory is a **folder**, named after `--mem` (default `agent_memory`). A trailing `.json` is dropped, so `--mem my_task.json` and `--mem my_task` both use the folder `my_task/`. The folder is created on the first save.

```
agent_memory/
├── conversation.json   Full conversation history (sent to the LLM)
├── exact.json          ExactMemory (last 50 steps)
├── semantic.json       SemanticMemory (error / solution patterns)
└── summary.json        Distilled session summary (decisions, constraints, pending, failed approaches)
```

A file that could not be read is kept beside the others as `<name>.bad` (for example `semantic.json.bad`; safe to delete).

To start a **new, unrelated task**, use a different `--mem` name (or delete the folder). To **continue** a task, keep the same name.

> **Upgrading from 0.7.0 or earlier.** The previous layout (`agent_memory.json`, `agent_memory.json.exact.json`, `agent_memory.json.semantic.json`, `agent_memory.json.semantic.json.summary.json`, all side by side) is no longer read and there is no automatic migration: the agent starts with an empty memory. To keep an old memory, move its files into the new folder and rename them to the four names above.

### Code layout

```
src/
├── Core/Agent/CodingAgent.cs       # Agent loop; receives HybridMemoryManager (7th ctor param)
├── Services/
│   ├── ExactMemory.cs
│   ├── SemanticMemory.cs
│   ├── HybridMemoryManager.cs
│   ├── TextTokenizer.cs            # Keyword extraction (EN + FR)
│   ├── SessionSummary.cs           # The four lists of a distilled session
│   └── SummaryMemory.cs            # Distils, saves and loads the session summary
├── Presentation/
│   ├── Tui/TuiHost.cs              # Creates and passes the memory manager
│   └── Web/
│       ├── ApiEndpoints.cs         # Same, for the Web / Lean UI
│       └── ApiHost.cs              # Headless --api server (key check, no UI)
└── Tests/                          # Test project (see Tests)
```

---

## Building from Source

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later

### Build

```bash
dotnet build
```

### Run

```bash
# CLI mode
set ALBERT_API_KEY=your-key
csagent

# Web UI mode
set ALBERT_API_KEY=your-key
csagent --ui
```

---

## Tests

The `Tests/` folder holds a test project for the memory system (hybrid memory, session distillation, atomic saves, `--no-distill`) and the agent loop. It uses **MSTest** (test-only NuGet packages; the application itself has no dependency). A mock OpenAI-compatible server stands in for the LLM, so no API key and no network are needed.

In Visual Studio, open **Test Explorer** and choose **Run All Tests**: every test is listed on its own row. The same tests also run from the console:

```bash
dotnet run --project Tests -c Release
```

The output ends with a summary line (`TOTAL 71 | PASS 71 | FAIL 0 | FINDINGS 0`), the process exit code is `0` when everything passes and `1` otherwise, and a full report is written to `results.txt` next to the test binary.

The project compiles the app's `Core/`, `Services/` and `Shared/` sources directly (the app itself is a Web / NativeAOT project) with reflection-based JSON disabled, which reproduces the AOT constraint: a stray `JsonSerializer.Serialize<T>()` fails the tests, as it would fail the published binary.

| File | Covers |
|---|---|
| `MemoryLayerTests.cs` | ExactMemory, SemanticMemory and HybridMemoryManager basics |
| `SemanticTests.cs` | Keyword search (EN + FR), de-duplication, 200-entry cap, solutions only after an error |
| `PersistenceTests.cs` | Atomic saves, tolerant loading, `.bad` quarantine, conversation file |
| `MemoryFolderTests.cs` | Memory folder naming (`--mem`), creation on first save, the four files of a run |
| `ConcurrencyTests.cs` | One shared memory manager used by many parallel requests |
| `DistillationTests.cs` | Session summary: saving, failure modes, sanitising, injection at every step |
| `NoDistillTests.cs` | `--no-distill` parsing and behaviour |
| `QuietModeTests.cs` | `--quiet`: what the CLI prints (and hides), failed calls, confirmation prompts; the same filter for the web UIs (`QuietObserver`) |
| `LlmEndpointTests.cs` | `--endpoint`, `--vision-model`, local endpoints and the API key |
| `ModelRouterTests.cs` | Automatic model choice (code / chat / vision): general vs code messages in French and English, `--model` priority, follow-ups after tools, `--no-route`, custom endpoints, and the check against the endpoint's model list (unknown, unavailable, non-text and offline cases) |
| `MsgTests.cs` / `MsgTestFile.cs` | Outlook `.msg` reader (a test-only builder writes valid `.msg` files), HTML/RTF to text, `read_msg`, `save_attachment` and its file-name safety |
| `TranscribeTests.cs` | `transcribe_audio`: parts sent in order (mock Whisper, fake ffmpeg), errors, truncation, path and key checks; web recorder storage (`AudioRecordings`) |
| `McpTests.cs` | MCP: `mcp_` names (no shadowing of native tools, collisions, 64 characters), calls under the server's own name, confirmation before every MCP call, native tool still wins when a server has the same name (mock MCP server) |
| `ApiModeTests.cs` | `--api`, `--yes`, `--host`, `--api-key`: parsing, host/key policy, authentication, auto-approve in the agent loop |
| `TestExplorer.cs` | Lists every test in Visual Studio's Test Explorer |
| `AgentEndToEndTests.cs` | Whole `CodingAgent` runs against the mock LLM |
| `MockServices.cs`, `TestKit.cs`, `Report.cs` | Mock server, observer, tiny test runner, report |

---

## AOT Publishing

CSAgent supports **Ahead-of-Time (AOT) compilation** for fast startup and single-file deployment:

```bash
# Publish as a single-file AOT binary
dotnet publish -c Release -r win-x64   # Windows
dotnet publish -c Release -r linux-x64 # Linux
dotnet publish -c Release -r osx-x64   # macOS
```

The AOT build produces a self-contained executable with no runtime dependencies.

Because reflection-based JSON serialization is disabled under AOT, do **not** use `JsonSerializer.Serialize<T>()` / `Deserialize<T>()`. All JSON (including the hybrid memory files) is built and parsed with `JsonNode`, `JsonObject`, `JsonArray` and `JsonValue`.

---

## Troubleshooting

### "API Key not set"
Ensure the `ALBERT_API_KEY` environment variable is set before running.

### "API 401: ..."
Your API key is invalid or expired. Check your credentials.

### "API 429: ..."
You've hit the rate limit. CSAgent now retries automatically with exponential
backoff (honoring the server's `Retry-After` header when present). If the error
persists after all retries, the API is still rate-limiting you — wait a moment
and try again. You can tune the retry behavior with `--max-retries` and
`--retry-delay` (see [Command-Line Arguments](#command-line-arguments)).

### "command timed out (60s)"
The shell command took longer than 60 seconds. Try breaking the task into smaller steps.

### "file too large"
The file exceeds the 500 KB read limit. Use `sh` with tools like `grep`, `head`, or `find` to inspect specific parts.

### "Path is not allowed"
File operations are restricted to the current working directory. Change to the target directory before running the agent, or use shell commands to copy files into the workspace.

### Browser doesn't open automatically
Navigate manually to **http://localhost:5050** in your browser (or the port you chose with `--port`).

### "Unsupported image type" / image won't attach
Only **PNG, JPEG, GIF, and WebP** images are supported, and the file must be **10 MB or smaller**. If you're attaching a different format (e.g. BMP, TIFF, SVG), convert it to a supported format first.

### Hybrid memory files (`exact.json`, `semantic.json`) are not created
Check that a `HybridMemoryManager` is created in `TuiHost.cs` / `ApiEndpoints.cs` and passed to the `CodingAgent` constructor (otherwise memory is `null` and silently skipped). Saving is done in a `finally` block, so it happens on every exit path.

### "Session summary not updated" / "unchanged"
*not updated (timed out / API error)*: the model did not answer within 60 seconds or answered something that was not the expected JSON. Your task is not affected and the previous summary is kept; it will be tried again at the end of the next run. If it happens every time, check the model and the API, or use `--no-distill`.
*unchanged*: the model judged there was nothing worth keeping (typical for a trivial task). This is normal.

### No `summary.json` file appears
The conversation was too short (system prompt plus at most one exchange), `--no-distill` was used, or the model returned nothing to keep. Run a task that uses a few tools and look for the `Session summary updated` line at the end.

### A memory file was renamed to `.bad`
The file was not valid JSON (for example after a manual edit or a disk problem). The agent started with an empty memory and kept the damaged file as `<name>.bad` in the memory folder (for example `semantic.json.bad`). Fix or delete it; nothing else is needed.

### "Reflection-based serialization has been disabled"
A `JsonSerializer.Serialize<T>()` / `Deserialize<T>()` call slipped into an AOT build. Replace it with manual `JsonNode` / `JsonObject` / `JsonArray` construction.

### "Expected multipart/form-data"
This error appears when the `/api/chat` endpoint is called with a `POST` that isn't `multipart/form-data`. The Web UI and Lean UI send the correct content type automatically; this usually only happens with a hand-written client.

---

## License

This project is provided as-is. It is built entirely on the .NET base class library with zero NuGet dependencies.

---

*CSAgent — Maximum autonomy, minimal dependencies.*