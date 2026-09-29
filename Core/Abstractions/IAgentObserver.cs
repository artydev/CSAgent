namespace CsAgent.Core.Abstractions;

public interface IAgentObserver
{
    Task OnStep(int n, int max);
    Task OnThought(string text);
    Task OnToolCall(string name, string args);
    Task OnToolResult(string result, bool isError);
    Task OnDone(string message);
    Task OnError(string message);
    Task OnWarning(string message);
    Task OnDanger(string message);

    /// <summary>
    /// Ask the user whether a destructive tool call should be allowed.
    /// Returns true to proceed, false to decline.
    /// </summary>
    Task<bool> OnConfirm(string toolName);
}