namespace CsAgent.Core.Abstractions;

/// <summary>
/// The --quiet filter for the web interfaces (the CLI does the same in ConsoleObserver). Only what the
/// user needs is passed on: the assistant's messages, warnings, errors, the final line and confirmation
/// requests. Steps, tool calls and results are held back. Two details keep it safe to rely on:
/// <list type="bullet">
/// <item>a tool call that fails is still reported, as one warning line;</item>
/// <item>before a confirmation request, the tool call it is about is passed on, so the user always
/// sees what they are about to allow.</item>
/// </list>
/// </summary>
public sealed class QuietObserver(IAgentObserver inner) : IAgentObserver
{
    private (string Name, string Args)? _lastCall;

    /// <summary>The observer to give the agent: filtered when <paramref name="quiet"/>, as is otherwise.</summary>
    public static IAgentObserver Wrap(IAgentObserver inner, bool quiet) => quiet ? new QuietObserver(inner) : inner;

    public Task OnStep(int n, int max) => Task.CompletedTask;
    public Task OnThought(string text) => inner.OnThought(text);

    public Task OnToolCall(string name, string args)
    {
        _lastCall = (name, args);
        return Task.CompletedTask;
    }

    public Task OnToolResult(string result, bool isError) =>
        isError ? inner.OnWarning($"{_lastCall?.Name ?? "tool"} failed: {FirstLine(result)}") : Task.CompletedTask;

    public Task OnDone(string message) => inner.OnDone(message);
    public Task OnError(string message) => inner.OnError(message);
    public Task OnWarning(string message) => inner.OnWarning(message);
    public Task OnDanger(string message) => inner.OnDanger(message);

    // The model of each assistant message stays visible in quiet mode: it is part of the message.
    public Task OnAssistantModel(string model) => inner.OnAssistantModel(model);

    public async Task<bool> OnConfirm(string toolName)
    {
        if (_lastCall is { } call && call.Name == toolName)
            await inner.OnToolCall(call.Name, call.Args);
        return await inner.OnConfirm(toolName);
    }

    private static string FirstLine(string s)
    {
        var line = s.Split('\n')[0].Trim();
        return line.Length > 200 ? line[..200] + "..." : line;
    }
}
