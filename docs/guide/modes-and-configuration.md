# Operating modes and configuration

This chapter summarises modes and settings described by the project README and options directly exercised by API and model-routing tests. Check the root README for the latest command syntax before deployment.

## Operating modes

| Mode | Start option / invocation | Intended use |
|---|---|---|
| CLI / terminal | Default launch | Interactive text-based agent session |
| Web UI | --ui | Browser interface with streamed progress |
| Lean UI | --leanui | Lightweight terminal-inspired browser interface |
| Headless API | --api | External orchestration over HTTP and SSE |
| One-shot prompt | --prompt "…" | Run one request and exit |

The README describes Web UI and Lean UI as exposing conversation progress through Server-Sent Events (SSE). API mode uses the same conversation streaming API without serving the HTML/JavaScript/CSS interface.

## CLI and one-shot use

The default mode opens an interactive text session. The README documents exit to leave the session.

For a single prompt, use --prompt. The README states that the request is executed and the process exits, with the answer written to standard output so it can be redirected or piped.

    csagent --prompt "Summarise the current project"

## Web UI and Lean UI

The README documents both browser modes on localhost, using port 5050 by default and --port / -p to select another port. The Web UI provides streamed steps, tool calls and results, plus syntax-highlighted code. Lean UI is a smaller terminal-styled alternative with its own embedded assets.

The README also describes browser audio recording in both modes. Audio is uploaded in 30-second chunks, then transcribed through the configured LLM/audio service. Users can choose to use the transcription as an instruction or retain it as a text attachment. The README names recordings/ and transcripts/ as local data directories.

## Headless API mode

Start the API with:

    csagent --api

API mode is intended for another program or orchestrator. It does not serve the browser interface and does not open a browser.

### API-mode defaults verified by tests

[ApiModeTests.cs](../../Tests/ApiModeTests.cs) explicitly checks parser defaults:

| Setting | Tested default |
|---|---|
| API mode | Off |
| Automatic approval | Off |
| Host | localhost |
| API key | Not set |

The same test group checks parsing of --api, --yes, --host and --api-key in different orders, and aliases for automatic approval. It also covers API-key environment configuration and argument/environment precedence.

### API flags

| Flag | Meaning |
|---|---|
| --api | Run without a user interface |
| --yes, -y, --auto-approve | Automatically approve tool calls that would otherwise require confirmation |
| --host <address> | Select the API listening address; README says default is localhost |
| --port <number> / -p | Select the listening port |
| --api-key <key> | Require a bearer key for API requests |

The README recommends using CSAGENT_API_KEY rather than putting a secret in the command line, because command-line arguments can be visible in process listings.

**Network exposure warning:** binding to a non-loopback interface makes the service reachable beyond the local machine. Use an API key, network controls and a trusted deployment environment. Do not expose an unauthenticated agent endpoint to an untrusted network.

### Approval behaviour

Without --yes, the API can emit a confirmation event and wait for the caller to approve or reject a tool operation through POST /api/confirm. With --yes, the caller does not need to answer those confirmation requests.

The README explicitly notes that automatic approval does not disable the shell command filter or path restrictions. Approval policy and safety filtering are separate layers.

## Configuration and environment

The tests and README identify these environment variables:

| Variable | Purpose |
|---|---|
| ALBERT_API_KEY | API key for the Albert endpoint used in documented examples |
| CSAGENT_API_KEY | API key protecting the headless API |
| CSAGENT_ENDPOINT | Custom OpenAI-compatible LLM endpoint |
| CSAGENT_MODEL_CODE | Explicit code model |
| CSAGENT_MODEL_CHAT | Explicit chat model |
| CSAGENT_VISION_MODEL | Vision model override |
| CSAGENT_ROUTING | Automatic routing setting |
| CSAGENT_AUDIO_KEEP_DAYS | Optional retention period for old audio recordings |

Environment variables can affect tests. Routing tests deliberately save, clear and restore selected variables, reset LLM configuration and clear the model catalogue cache to avoid leaking state between cases.

## Model endpoint selection

The routing tests cover a default Albert-oriented configuration and a custom local endpoint. The test explicitly demonstrates that a custom endpoint does not automatically receive the Albert chat-model alias; the chat model can instead be set explicitly.

Do not assume every model server supports the same model names or capabilities. Configure a model identifier that exists on the selected endpoint and verify that the endpoint supports features required by the task, such as tool calling or image input.

## Practical deployment checklist

- Keep provider keys and API keys out of source control.
- Prefer environment variables or a secret manager for secrets.
- Keep the API bound to localhost unless remote access is intentional.
- If remote access is enabled, require an API key and apply network-level access controls.
- Review the approval mode before allowing tool execution.
- Keep the command filter and path restrictions enabled.
- Treat memory, recordings and transcripts as local user data; configure backups and retention deliberately.
- Test the exact model endpoint and model identifier used in production.
