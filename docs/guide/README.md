# CSAgent Documentation

A test-informed guide to CSAgent. It explains observable behaviour from the repository README, source layout and selected executable tests. It is not a substitute for current CLI help or source code.

## What is CSAgent?

CSAgent is a cross-platform autonomous coding agent for Windows, Linux and macOS. It uses an OpenAI-compatible language-model endpoint to interpret natural-language instructions and can inspect and modify files, run commands and use integrations. The repository describes CLI/terminal, Web UI and Lean UI modes, plus a headless API mode.

The project targets .NET 10 and is configured for trimmed, single-file Native AOT publishing. A Python client is provided for the HTTP/SSE API and uses only the Python standard library.

## Documentation map

- [Architecture](architecture.md) — repository map, agent loop, model routing and tool selection.
- [Modes and configuration](modes-and-configuration.md) — CLI, Web UI, Lean UI, API, arguments and environment variables.
- [Tools and security](tools-and-security.md) — tool groups, command filtering and confirmations.
- [Memory and integrations](memory-and-integrations.md) — memory concepts, MCP, skills, audio and related tests.
- [Testing and traceability](testing-and-traceability.md) — test strategy, commands and behavioural index.
- [Python client](python-client.md) — client API, SSE events, confirmations and examples.
- [HTML edition](index.html) — self-contained browser-readable guide.

## Quick start

The root README is the canonical source for installation and launch instructions. For a source checkout, the project file targets .NET 10:

    dotnet build CsAgent.csproj -c Release

Configure a key for an OpenAI-compatible model endpoint, then start the desired interface. See [modes and configuration](modes-and-configuration.md) and the [root README](../../README.md) for exact flags and endpoint details.

## Evidence policy

This guide distinguishes three kinds of statements:

- **Test-verified** — a test directly asserts the behaviour.
- **README/source-described** — documented in project materials or visible in production project configuration, but not necessarily verified by a test in this guide.
- **Operational guidance** — recommended practice, not a claim that a test enforces it.

A test proves the scenario it exercises, not every possible input or every behaviour of a subsystem. Where details are not established by reviewed tests, this guide avoids presenting assumptions as guarantees.

## Repository links

- [Project README](../../README.md)
- [Tests directory](../../Tests/)
- [Python client documentation](../../python/README.md)
- [Project file](../../CsAgent.csproj)
- [Source repository](https://github.com/artydev/CSAgent)

## Maintenance

When behaviour changes, update the relevant guide and its test links in the same change. See [testing and traceability](testing-and-traceability.md) for a review workflow.
