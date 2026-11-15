# Architecture and execution model

This chapter describes concepts visible from the repository layout and behavioural tests. It is a conceptual map, not a claim that every internal class or call sequence is covered by tests.

## High-level model

CSAgent combines four cooperating concerns:

1. **Conversation orchestration** — receives a user request, maintains conversation messages, asks the model for the next response and coordinates tool calls.
2. **Model access and routing** — communicates with an OpenAI-compatible endpoint and chooses a model profile appropriate to the request.
3. **Tool dispatch** — exposes callable operations to the model, validates and dispatches requests, and reports results.
4. **Interaction and persistence** — presents progress through CLI or web interfaces, handles confirmations, and retains memory or session data.

The end-to-end suite exercises a real CodingAgent with a mock language-model service. This tests observable agent/model interactions without requiring a live provider.

## Repository map

| Path | Responsibility suggested by its name and project usage |
|---|---|
| Core/ | Core abstractions and agent/domain logic |
| Services/ | Application services and integration operations |
| Infrastructure/Clipboard/ | Clipboard integration |
| Presentation/ | User-facing presentation, including Web and Lean UI assets |
| Shared/ | Shared types and helpers |
| LLMRoutingRules/ | Editable model-routing rules |
| skills/ | Skill definitions/resources shipped with the application |
| python/ | Python client and its tests |
| Tests/ | C# behavioural and integration-style tests |
| docs/ | Project documentation |

The project file is a Web SDK project targeting .NET 10.0. It enables nullable reference types, implicit usings, trimming, single-file publishing and Native AOT. It excludes Tests/** from the main application compilation and embeds Web/Lean UI assets and the root README.

## Conceptual request lifecycle

A typical request can be understood as this sequence:

1. The selected interface receives the user's prompt.
2. Configuration and runtime options determine mode, model endpoint and approval policy.
3. The agent builds or updates conversation context.
4. The model router selects a model profile. The tool selector builds tool definitions for the request.
5. The model responds with text and/or tool calls.
6. Tool dispatch validates the requested operation. Depending on the operation and approval mode, a confirmation may be requested.
7. Tool results are added to the conversation and the agent continues until it produces a final response or encounters an error.
8. Memory/session facilities may preserve information for later turns or sessions.

This is a mental model, not a formal protocol guarantee for every interface. For HTTP streaming details, see [Python client](python-client.md).

## Model routing

The tests identify three routing concepts: **code**, **chat** and **vision**. The routing test group is explicitly named “Model routing (code / chat / vision)”.

Reviewed assertions establish examples of the following:

- Automatic routing is enabled by default in the tested default configuration.
- The tested Albert-oriented defaults are deepseek-v4-flash for code and openweight-large for chat.
- A custom endpoint such as a local Ollama-compatible endpoint does not automatically inherit the Albert chat alias; an explicit chat model can be configured.
- General conversational prompts in French and English are expected to route to the chat profile in tested examples.
- Coding, file, shell and other action-oriented prompts are expected to route to the code profile in tested examples.
- The model choice records a reason/signal rather than silently appearing as an unexplained default.

These are test-backed examples, not a complete list of every routing heuristic. See [ModelRouterTests.cs](../../Tests/ModelRouterTests.cs).

### Configuration inputs visible in tests

| Input | Purpose evidenced by tests |
|---|---|
| --code-model | Explicit code model selection |
| --chat-model | Explicit chat model selection |
| --no-route | Disable automatic routing |
| CSAGENT_MODEL_CODE | Environment override for code model |
| CSAGENT_MODEL_CHAT | Environment override for chat model |
| CSAGENT_ROUTING | Environment switch for routing |

The routing tests save, clear and restore environment variables, reset LLM configuration and clear the model catalogue cache to isolate scenarios.

## Dynamic tool selection

CSAgent does not necessarily send every native tool definition to the model on every turn. The tests establish that:

- An ordinary coding request receives a core set of tools.
- Feature-specific groups can be activated from request wording, with examples in English and French.
- A group already used in the conversation can remain available for a short follow-up.
- An enable_tools meta-tool can expose additional groups when appropriate.
- --all-tools selects all native tools.
- MCP tool exposure is conditional in tested scenarios, including tool count and whether the request indicates a relevant integration.

The tests assert that a typical coding request receives fewer than half the serialized tool-definition characters compared with the complete native tool set. This is a specific fixture result, not a universal performance guarantee. See [ToolSelectorTests.cs](../../Tests/ToolSelectorTests.cs).

## End-to-end testing boundary

[AgentEndToEndTests.cs](../../Tests/AgentEndToEndTests.cs) uses a mock LLM and constructs a real CodingAgent with test memory components. Its scenarios include memory retrieval and handling of model-produced memory-related content.

A mock-backed end-to-end test does not prove compatibility with every provider, model, prompt style or network condition. Provider-specific behaviour should also be validated against the target endpoint.
