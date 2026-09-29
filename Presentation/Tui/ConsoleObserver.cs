using CsAgent.Core.Abstractions;
using CsAgent.Shared;

namespace CsAgent.Presentation.Tui;

public class ConsoleObserver : IAgentObserver
{
    public Task OnStep(int n, int m) { UI.Step(n, m); return Task.CompletedTask; }
    public Task OnThought(string t) { UI.AssistantText(t); return Task.CompletedTask; }
    public Task OnToolCall(string n, string a) { UI.ToolCall(n, JsonHelpers.PrettyJson(a)); return Task.CompletedTask; }
    public Task OnToolResult(string r, bool e) { UI.ToolResult(r, e); return Task.CompletedTask; }
    public Task OnDone(string m) { UI.Success(m); return Task.CompletedTask; }
    public Task OnError(string m) { UI.Error(m); return Task.CompletedTask; }
    public Task OnWarning(string m) { UI.Warning(m); return Task.CompletedTask; }
    public Task OnDanger(string m) { UI.Danger(m); return Task.CompletedTask; }
    public Task<bool> OnConfirm(string toolName) => Task.FromResult(UI.Confirm($"Allow destructive action '{toolName}'?"));
}