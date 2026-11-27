using CsAgent.Core.Agent;

namespace CsAgent.Tests;

// Runs once when the test assembly loads: the suite starts with compression off (see ContextBudget.Reset),
// so that the model call it adds does not consume the scripted answers of the mock models.
static class TestDefaults
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init() => ContextBudget.Reset();
}
