namespace CsAgent.Core.Memory;

/// <summary>
/// Where one memory lives on disk: a folder holding the four files of the memory system.
/// <code>
/// agent_memory/
/// ├── conversation.json   full conversation history (sent to the LLM)
/// ├── exact.json          ExactMemory: the last 50 steps
/// ├── semantic.json       SemanticMemory: error / solution patterns
/// └── summary.json        distilled session summary
/// </code>
/// </summary>
public sealed record MemoryPaths(string Directory, string Conversation, string Exact, string Semantic, string Summary)
{
    /// <summary>Memory name used when <c>--mem</c> is not given.</summary>
    public const string DefaultName = "agent_memory";

    public const string ConversationFile = "conversation.json";
    public const string ExactFile = "exact.json";
    public const string SemanticFile = "semantic.json";
    public const string SummaryFile = "summary.json";

    /// <summary>
    /// Builds the paths for a memory name (<c>--mem</c>): the name is the folder.
    /// A trailing ".json" is dropped, so an old-style <c>--mem my_task.json</c> gives the folder <c>my_task</c>.
    /// An empty name gives <see cref="DefaultName"/>.
    /// </summary>
    public static MemoryPaths Resolve(string? memory)
    {
        var name = (memory ?? "").Trim();
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            name = name[..^".json".Length];
        name = name.TrimEnd('/', '\\');
        if (name.Length == 0) name = DefaultName;

        return new MemoryPaths(
            name,
            Path.Combine(name, ConversationFile),
            Path.Combine(name, ExactFile),
            Path.Combine(name, SemanticFile),
            Path.Combine(name, SummaryFile));
    }
}
