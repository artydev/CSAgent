using CsAgent.Core.Llm;

namespace CsAgent.Shared;

/// <summary>
/// Parsed CLI arguments — clean record, no parsing logic mixed in.
/// </summary>
public sealed record AgentArguments(
    string MemoryFile,
    string? ModelOverride,
    string? McpUrl,
    string? TaskSlug,
    int Port,
    bool IsUiMode,
    bool IsLeanUiMode,
    bool IsNativeMode,
    bool IsDryRun,
    bool ShowHelp,
    bool ShowVersion,
    bool ShowDoc,
    int MaxRetries = 6,
    int RetryDelayMs = 1000,
    bool UsePropMem = true,
    string PropositionFile = "agent_propositions.json",
    bool Distill = true,
    bool IsApiMode = false,
    bool AutoApprove = false,
    string Host = "localhost",
    string? ApiKey = null);

/// <summary>
/// Pure argument parsing — no side effects, no console output.
/// </summary>
public static class ArgumentParser
{
    public static AgentArguments Parse(string[] args)
    {
        var isUiMode = args.Contains("--ui");
        var isLeanUiMode = args.Contains("--leanui");
        var isNativeMode = args.Contains("--native");
        var isDryRun = args.Contains("--dry-run");
        var showHelp = args.Contains("--help") || args.Contains("-h") || args.Contains("/?");
        var showVersion = args.Contains("--version");
        var showDoc = args.Contains("--doc");
        var memFile = GetMemoryFile(args);
        var modelOverride = GetModelOverride(args);
        var mcpUrl = GetValue(args, "--mcp", "--mcp-url")
                           ?? Environment.GetEnvironmentVariable("CSAGENT_MCP_URL");
        var taskSlug = GetValue(args, "--task");
        var port = GetPort(args);
        var maxRetries = GetInt(args, "--max-retries", RetryPolicy.Default.MaxAttempts);
        var retryDelayMs = GetInt(args, "--retry-delay", RetryPolicy.Default.BaseDelayMs);
        var usePropMem = !args.Contains("--no-propmem");
        var propositionFile = GetValue(args, "--prop") ?? "agent_propositions.json";
        var distill = !args.Contains("--no-distill");
        var isApiMode = args.Contains("--api");
        var autoApprove = args.Contains("--yes") || args.Contains("-y") || args.Contains("--auto-approve");
        var host = GetValue(args, "--host") ?? ApiSecurity.DefaultHost;
        var apiKey = GetValue(args, "--api-key") ?? Environment.GetEnvironmentVariable("CSAGENT_API_KEY");
        if (string.IsNullOrEmpty(apiKey)) apiKey = null;

        return new AgentArguments(memFile, modelOverride, mcpUrl, taskSlug,
            port, isUiMode, isLeanUiMode, isNativeMode, isDryRun,
            showHelp, showVersion, showDoc, maxRetries, retryDelayMs, usePropMem, propositionFile, distill,
            isApiMode, autoApprove, host, apiKey);
    }

    private static string GetMemoryFile(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "--mem" && i + 1 < args.Length) return args[i + 1];

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--model" or "--mcp" or "--mcp-url" or "--port"
                          or "-p" or "--max-retries" or "--retry-delay"
                          or "--task" or "--prop" or "--host" or "--api-key")
            { i++; continue; }

            if (args[i] != "--ui" && args[i] != "--leanui" && args[i] != "--native"
                && args[i] != "--dry-run" && !args[i].StartsWith("-"))
                return args[i];
        }
        return "agent_memory.json";
    }

    private static string? GetModelOverride(string[] args) => GetValue(args, "--model");

    private static string? GetValue(string[] args, params string[] names)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (names.Contains(args[i], StringComparer.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static int GetPort(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
            if ((args[i] == "--port" || args[i] == "-p") && i + 1 < args.Length)
                if (int.TryParse(args[i + 1], out var p) && p > 0 && p < 65536)
                    return p;
        return 5050;
    }

    /// <summary>Reads an integer-valued option, falling back to defaultValue.</summary>
    private static int GetInt(string[] args, string name, int defaultValue)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name && int.TryParse(args[i + 1], out var v) && v > 0)
                return v;
        return defaultValue;
    }
}