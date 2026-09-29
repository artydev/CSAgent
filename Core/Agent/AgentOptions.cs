using CsAgent.Core.Llm;
using CsAgent.Core.Tasks;

namespace CsAgent.Core.Agent;

public sealed record AgentOptions(
    int MaxSteps = 30,
    bool DryRun = false,
    bool Confirm = true,
    RetryPolicy? Retry = null,
    string? ResumeTaskId = null,
    TaskTracker? Tracker = null,
    bool UsePropMem = true,
    string PropositionFile = "agent_propositions.json");