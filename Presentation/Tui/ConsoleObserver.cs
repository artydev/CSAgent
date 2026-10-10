using CsAgent.Core.Abstractions;
using CsAgent.Shared;

namespace CsAgent.Presentation.Tui;

/// <summary>
/// Console output of the CLI. In quiet mode only the assistant's messages, warnings, errors and
/// confirmation prompts are shown: steps, tool calls and results are hidden. The last tool call
/// is remembered and printed right before a confirmation prompt, so the user always sees what
/// they are about to allow. A failed tool call is still reported.
/// </summary>
public class ConsoleObserver : IAgentObserver
{
    private readonly bool _quiet;
    private (string Name, string Args)? _lastCall;

    public ConsoleObserver(bool quiet = false) { _quiet = quiet; }

    public Task OnStep(int n, int m) { if (!_quiet) UI.Step(n, m); return Task.CompletedTask; }
    public Task OnThought(string t) { UI.AssistantText(t); return Task.CompletedTask; }
    public Task OnAssistantModel(string model) { UI.ModelTag(model); return Task.CompletedTask; }

    public Task OnToolCall(string n, string a)
    {
        var pretty = JsonHelpers.PrettyJson(a);
        if (_quiet) _lastCall = (n, pretty);
        else UI.ToolCall(n, pretty);
        return Task.CompletedTask;
    }

    public Task OnToolResult(string r, bool e)
    {
        if (!_quiet) UI.ToolResult(r, e);
        else if (e) UI.Warning($"{_lastCall?.Name ?? "tool"} failed: {FirstLine(r)}");
        return Task.CompletedTask;
    }

    public Task OnDone(string m) { UI.Success(m); return Task.CompletedTask; }
    public Task OnError(string m) { UI.Error(m); return Task.CompletedTask; }
    public Task OnWarning(string m) { UI.Warning(m); return Task.CompletedTask; }
    public Task OnDanger(string m) { UI.Danger(m); return Task.CompletedTask; }

    public Task<bool> OnConfirm(string toolName)
    {
        if (_quiet && _lastCall is { } c && c.Name == toolName) UI.ToolCall(c.Name, c.Args);
        return Task.FromResult(UI.Confirm($"Allow destructive action '{toolName}'?"));
    }

    private static string FirstLine(string s)
    {
        var line = s.Split('\n')[0].Trim();
        return line.Length > 200 ? line[..200] + "..." : line;
    }
}
