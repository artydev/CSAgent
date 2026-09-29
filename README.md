# CSAgent — Cross-Platform Autonomous Coding Agent

**CSAgent** is a cross-platform autonomous coding agent that runs on Windows, Linux, and macOS. It uses an OpenAI-compatible API (e.g., [Albert API](https://albert.api.etalab.gouv.fr)) to understand natural-language instructions and autonomously perform coding tasks by reading, writing, and listing files, as well as executing shell commands.

It ships with three presentation modes — a terminal UI (TUI), a web UI, and a lean UI.

---

## Table of Contents.

- [Quick Start](#quick-start)
- [Modes of Operation](#modes-of-operation)
  - [CLI Mode (Default)](#cli-mode-default)
  - [Web UI Mode](#web-ui-mode)
  - [Lean UI Mode](#lean-ui-mode)
- [LLM Models](#llm-models)
- [Future Features](#future-features)
- [Environment Variables](#environment-variables)
- [Command-Line Arguments](#command-line-arguments)
- [Safety Features](#safety-features)
- [Available Tools](#available-tools)
- [Memory & Conversation Persistence](#memory--conversation-persistence)
  - [Hybrid Memory (anti-amnesia)](#hybrid-memory-anti-amnesia)
  - [Session Distillation (across sessions)](#session-distillation-across-sessions)
  - [Memory files](#memory-files)
  - [Code layout](#code-layout)
- [Building from Source](#building-from-source)
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

### Lean UI Mode

Lean UI mode (`--leanui` flag) is a lightweight duplicate of the Web UI. It serves the same embedded assets and SSE-based chat endpoints, launched via the `--leanui` command-line argument. It is served at **http://localhost:5050** by default (use `--port <n>` to change it).

### Vision / Image Attachments

CSAgent supports **multimodal (vision) prompts** — you can attach an image to a prompt and the agent will analyze it. This works in both the Web UI and the Lean UI, and in the CLI/TUI.

- **Web UI / Lean UI:** click the **📎** (paperclip) button next to the input box to attach an image. A small preview appears; click **✕** to remove it before sending. Supported formats: **PNG, JPEG, GIF, WebP** (max **10 MB**).
- **CLI / TUI:** image attachment is handled through the conversation history — once a vision exchange has occurred, the agent automatically keeps using the vision-capable model for the rest of the session.

When an image is attached, CSAgent automatically routes the request to a **vision-capable model** (`gemma-4-31b-it` by default) instead of the standard text model. This routing is automatic and happens in two cases:

1. The current prompt includes an attached image.
2. The conversation history already contains an image from a previous exchange (text-only models reject requests whose history contains image content).

You can override the vision model with the `--model` argument, and the default vision model is defined by `LlmSettings.VisionModel`.

---

## LLM Models

CSAgent uses different LLM models depending on the mode of operation. This is intentional — each model is chosen for its strengths in the specific context.

| Mode | Default Model | Rationale |
|---|---|---|
| **CLI** | `deepseek-v4-flash` | Fast, lightweight, ideal for interactive terminal sessions where quick turnarounds matter |
| **Web UI** | `Qwen/Qwen3-Coder-30B-A3B-Instruct` | More capable for complex multi-step coding tasks; the Web UI is designed for longer, more involved sessions |
| **Vision (any mode)** | `gemma-4-31b-it` | Used automatically when an image is attached to a prompt (or present in the conversation history); see [Vision / Image Attachments](#vision--image-attachments) |

You can override the default model in any mode using the `--model` argument (see [Command-Line Arguments](#command-line-arguments)).

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

- **MCP (Model Context Protocol) integration** — connect to external MCP servers over Streamable HTTP to expose additional tools to the agent.
- **Python scripting** — drive CSAgent from Python scripts: launch sessions, send prompts, and retrieve responses and agent events (steps, tool calls, results) programmatically, for example via a csagent module or a web-interface (SSE) client.

---

## Environment Variables

| Variable | Required | Description |
|---|---|---|
| `ALBERT_API_KEY` | Yes | Your API key for the OpenAI-compatible endpoint |

---

## Command-Line Arguments

| Argument | Description |
|---|---|
| `--ui` | Start in Web UI mode (starts a web server) |
| `--leanui` | Start in Lean UI mode (lightweight duplicate of the Web UI) |
| `--mem <file>` | Specify a custom memory/conversation file (default: `agent_memory.json`) |
| `--model <model>` | Override the default LLM model for the current mode |
| `--port`, `-p <n>` | Web UI port number (default: `5050`) |
| `--dry-run` | Simulate tool execution without making changes |
| `--max-retries <n>` | Max attempts for HTTP 429 (rate limit) retries (default: `3`) |
| `--retry-delay <ms>` | Base backoff delay in ms before the first retry (default: `1000`) |
| `--help`, `-h`, `/?` | Display help and exit |
| `--version` | Display the current version of CSAgent and exit |
| `--doc` | Display this documentation in a nicely formatted terminal view and exit |
| `<file>` | Positional argument: specify a memory file without `--mem` flag |

### Examples

```bash
# Web UI with custom memory file
csagent --ui --mem my_project_memory.json

# Lean UI mode
csagent --leanui

# Web UI on a custom port
csagent --ui --port 8080

# CLI mode with a specific memory file
dotnet run my_memory.json

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
```

---

## Safety Features

CSAgent includes multiple layers of safety to prevent accidental damage to your system:

### 1. Destructive Action Confirmation

The `write_file` tool is classified as **destructive** because it modifies files on disk. Before executing, the agent will prompt for confirmation:

```
[?] Allow destructive action 'write_file'? [Y/n]
```

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

---

## Memory & Conversation Persistence

CSAgent saves the conversation history to a JSON file (default: `agent_memory.json`). This allows the agent to maintain context across sessions.

- The memory file is automatically loaded when the agent starts
- It is saved after each step
- Old messages are trimmed when the total content exceeds ~96 KB to keep context manageable
- You can specify a custom memory file with `--mem <file>` or as a positional argument

### Hybrid Memory (anti-amnesia)

Trimming old messages has a side effect: the agent forgets what it already tried and can repeat the same mistakes. To prevent this, CSAgent adds a **hybrid memory** on top of the conversation file. It is coordinated by `HybridMemoryManager` and uses only the .NET base class library (no NuGet package, no database).

| Layer | What it stores | Purpose |
|---|---|---|
| **ExactMemory** | The last 50 steps (thoughts, tool calls, errors, successes), verbatim | Shows the agent exactly what it just tried |
| **SemanticMemory** | Error / solution patterns, tagged (e.g. `permission`, `not-found`, `timeout`, `syntax`) | Explains *why* something failed and what worked instead |

Example: writing `/opt/config.json` fails with *permission denied*, then `/home/config.json` succeeds. Both facts are recorded, so the next time a similar write is attempted the agent is reminded to use the location that worked.

From step 4 onward, relevant entries are injected as a `[MEMORY]` message just before each LLM call, so they stay visible even after `TrimHistory()` has removed older messages.

### Session Distillation (across sessions)

When a run ends, the LLM condenses the conversation into a compact summary: **decisions**, **constraints**, **pending work** and **failed approaches**. On the next run this summary is injected at the start of the conversation, so the agent resumes with the reasoning of the previous session and not just the raw history.

Saving happens in a `finally` block around the agent loop, so memory is written however the run ends (task complete, text-only reply, error, cancellation, or maximum steps reached).

### Memory files

All files are derived from the memory file name (`--mem`, default `agent_memory.json`):

| File | Content |
|---|---|
| `agent_memory.json` | Full conversation history (sent to the LLM, source for distillation) |
| `agent_memory.json.exact.json` | ExactMemory (last 50 steps) |
| `agent_memory.json.semantic.json` | SemanticMemory (error / solution patterns) |
| `agent_memory.json.semantic.json.summary.json` | Distilled session summary |

To start a **new, unrelated task**, use a different `--mem` file (or delete these files). To **continue** a task, keep the same memory file.

### Code layout

```
src/
├── Core/Agent/CodingAgent.cs       # Agent loop; receives HybridMemoryManager (7th ctor param)
├── Services/
│   ├── ExactMemory.cs
│   ├── SemanticMemory.cs
│   ├── HybridMemoryManager.cs
│   ├── SessionSummary.cs
│   └── SummaryMemory.cs
└── Presentation/
    ├── Tui/TuiHost.cs              # Creates and passes the memory manager
    └── Web/ApiEndpoints.cs         # Same, for the Web / Lean UI
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

### Hybrid memory files (`.exact.json`, `.semantic.json`) are not created
Check that a `HybridMemoryManager` is created in `TuiHost.cs` / `ApiEndpoints.cs` and passed to the `CodingAgent` constructor (otherwise memory is `null` and silently skipped). Saving is done in a `finally` block, so it happens on every exit path.

### "Reflection-based serialization has been disabled"
A `JsonSerializer.Serialize<T>()` / `Deserialize<T>()` call slipped into an AOT build. Replace it with manual `JsonNode` / `JsonObject` / `JsonArray` construction.

### "Expected multipart/form-data"
This error appears when the `/api/chat` endpoint is called with a `POST` that isn't `multipart/form-data`. The Web UI and Lean UI send the correct content type automatically; this usually only happens with a hand-written client.

---

## License

This project is provided as-is. It is built entirely on the .NET base class library with zero NuGet dependencies.

---

*CSAgent — Maximum autonomy, minimal dependencies.*