namespace CsAgent.Services;

/// <summary>
/// Compact, LLM-distilled record of what a session decided and learned.
/// Persisted next to the memory file and injected at the start of the next run.
/// </summary>
public record SessionSummary(
    string[] Decisions,
    string[] Constraints,
    string[] Pending,
    string[] FailedApproaches,
    DateTime CreatedAt)
{
    public bool IsEmpty =>
        Decisions.Length == 0 && Constraints.Length == 0 &&
        Pending.Length == 0 && FailedApproaches.Length == 0;
}
