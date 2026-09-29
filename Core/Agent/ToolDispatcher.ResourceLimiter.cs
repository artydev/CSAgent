namespace CsAgent.Core.Agent;

public static partial class ToolDispatcher
{
    private const int ShellTimeoutMs = 60_000;
    private const int MaxSearchResults = 200;
    private const long MaxSearchFileBytes = 1_048_576; // skip binary/large files
    private const int MaxTreeEntries = 500;            // cap tree output to avoid huge responses
    private const int MaxParseOutputBytes = 512_000;   // cap parse_output input size

}
