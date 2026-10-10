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
    /// Called right after each assistant message with the model that wrote it
    /// ("openweight-large", or "openweight-large (served: openai/gpt-oss-120b)").
    /// Optional: observers that do not care can ignore it.
    /// </summary>
    Task OnAssistantModel(string model) => Task.CompletedTask;

    /// <summary>
    /// Ask the user whether a destructive tool call should be allowed.
    /// Returns true to proceed, false to decline.
    /// </summary>
    Task<bool> OnConfirm(string toolName);
}