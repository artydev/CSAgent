using CsAgent.Core.Llm;

namespace CsAgent.Shared;

/// <summary>
/// Renders the help text to the console.
/// </summary>
public static class HelpDisplay
{
    public static void Show(string version)
    {
        var useColor = !Console.IsOutputRedirected
                      && (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TERM"))
                          || OperatingSystem.IsLinux()
                          || OperatingSystem.IsMacOS());

        void C(string? color = null)
        {
            if (useColor && color is not null)
                Console.ForegroundColor = color switch
                {
                    "cyan" => ConsoleColor.Cyan,
                    "green" => ConsoleColor.Green,
                    "yellow" => ConsoleColor.Yellow,
                    "magenta" => ConsoleColor.Magenta,
                    "dark" => ConsoleColor.DarkGray,
                    _ => ConsoleColor.Gray
                };
            else
                Console.ResetColor();
        }

        C("magenta");
        Console.WriteLine();
        Console.WriteLine(@"   ██████╗███████╗ █████╗  ██████╗ ███████╗███╗   ██╗████████╗");
        Console.WriteLine(@"  ██╔════╝██╔════╝██╔══██╗██╔════╝ ██╔════╝████╗  ██║╚══██╔══╝");
        Console.WriteLine(@"  ██║     ███████╗███████║██║  ███╗█████╗  ██╔██╗ ██║   ██║   ");
        Console.WriteLine(@"  ██║     ╚════██║██╔══██║██║   ██║██╔══╝  ██║╚██╗██║   ██║   ");
        Console.WriteLine(@"  ╚██████╗███████║██║  ██║╚██████╔╝███████╗██║ ╚████║   ██║   ");
        Console.WriteLine(@"   ╚═════╝╚══════╝╚═╝  ╚═╝ ╚═════╝ ╚══════╝╚═╝  ╚═══╝   ╚═══╝  ");
        Console.ResetColor();
        C("dark");
        Console.WriteLine("  Cross-platform autonomous coding agent  |  zero NuGet deps");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine($"  USAGE:  CsAgent [options] [memory-file]");
        Console.WriteLine();
        Console.WriteLine($"  VERSION:  {version}");
        Console.WriteLine();

        C("green");
        Console.WriteLine("  MODES");
        Console.ResetColor();
        Console.WriteLine("    (no flag)     CLI mode — interactive terminal session");
        Console.WriteLine("    --ui          Web UI mode — starts a web server");
        Console.WriteLine("    --leanui      Lean UI mode — lightweight, terminal-style variant of the Web UI");
        Console.WriteLine("    --api         Headless API mode — SSE server without web UI, for orchestrators");
        Console.WriteLine();

        C("green");
        Console.WriteLine("  OPTIONS");
        Console.ResetColor();
        Console.WriteLine("    --help, -h, /?    Show this help message and exit");
        Console.WriteLine("    --version         Show version number and exit");
        Console.WriteLine("    --doc             Display full documentation in terminal and exit");
        Console.WriteLine("    --mem <name>      Memory folder: holds conversation.json, exact.json,");
        Console.WriteLine("                       semantic.json and summary.json (default: agent_memory)");
        Console.WriteLine("    --model <name>    Override the LLM model for the current mode");
        Console.WriteLine($"                       (default: {LlmSettings.Model})");
        Console.WriteLine("    --endpoint <url>  OpenAI-compatible base URL (default: " + LlmSettings.Endpoint + ")");
        Console.WriteLine("                       e.g. http://localhost:11434/v1 for Ollama (no key needed)");
        Console.WriteLine("    --vision-model <n> Model used when the conversation contains an image");
        Console.WriteLine($"                       (default: {LlmSettings.VisionModel})");
        Console.WriteLine("    --code-model <n>  Model for code, files and tools (default: " + LlmSettings.Model + ")");
        Console.WriteLine("    --chat-model <n>  Model for general conversation without code or files");
        Console.WriteLine("                       (default on the Albert endpoint: " + LlmSettings.ChatModel + ";");
        Console.WriteLine("                       none on a custom --endpoint unless you set it)");
        Console.WriteLine("    --no-route        Always use the code model (no automatic model choice)");
        Console.WriteLine("    --init-routing    Create the LLMRoutingRules folder (models.json, keywords.json,");
        Console.WriteLine("                       rules.json) to adjust the model choice, then exit");
        Console.WriteLine("    --explain-routing <message>  Show which model a message would get, and why,");
        Console.WriteLine("                       without calling any model, then exit");
        Console.WriteLine("    --rules <folder>  Use this folder instead of ./LLMRoutingRules");
        Console.WriteLine("    --prompt <text>   Run one request, print the answer and exit (no interactive loop).");
        Console.WriteLine("                       Script mode: only the final answer goes to stdout (pipe it);");
        Console.WriteLine("                       warnings and errors go to stderr; exit code 1 on error;");
        Console.WriteLine("                       a tool that needs confirmation is declined when input is");
        Console.WriteLine("                       redirected (use --yes). --verbose shows the full display");
        Console.WriteLine("    --lang <en|fr>    Language of the routing messages (default: the system language;");
        Console.WriteLine("                       or CSAGENT_LANG)");
        Console.WriteLine("    --mcp <url>       Connect to an MCP server (Streamable HTTP); its tools are offered");
        Console.WriteLine("                       to the model as mcp_<name> and each call asks for confirmation.");
        Console.WriteLine("                       Repeat it for several servers (names become mcp_<server>_<name>)");
        Console.WriteLine("    --mcp-stdio \"<command>\"  Start a local MCP server and talk to it over stdin/stdout");
        Console.WriteLine("    --mcp-header \"Name: value\"  Header sent to the --mcp servers (${VAR} = env variable)");
        Console.WriteLine("    --mcp-config <file>  JSON file describing the MCP servers (\"mcpServers\")");
        Console.WriteLine("    --port, -p <n>    Web UI port number (default: 5050)");
        Console.WriteLine("    --task <slug>     Track the work in .csagent/tasks/<slug>-<date>/ (plan and progress");
        Console.WriteLine("                       files created by the host, not by the model)");
        Console.WriteLine("    --dry-run         Simulate tool execution without making changes");
        Console.WriteLine("    --max-retries <n> Max attempts for 429 rate-limit retries");
        Console.WriteLine($"                       (default: {RetryPolicy.Default.MaxAttempts})");
        Console.WriteLine("    --retry-delay <ms> Base backoff delay in ms for 429 retries");
        Console.WriteLine($"                       (default: {RetryPolicy.Default.BaseDelayMs})");
        Console.WriteLine("    --context-chars <n> Size above which the oldest messages are dropped from the");
        Console.WriteLine("                       history sent at every step (default: 96000, minimum 8000).");
        Console.WriteLine("                       Lower it to spend fewer input tokens");
        Console.WriteLine("    --no-compact      Keep old tool results whole (by default, results older than the");
        Console.WriteLine("                       last 6 are cut to 1500 characters in the history)");
        Console.WriteLine("    --all-tools       Describe every tool to the model at each step (by default only the");
        Console.WriteLine("                       tools the request needs: files, search and shell always; git, web,");
        Console.WriteLine("                       mail, audio, Excel, clipboard... when the request calls for them)");
        Console.WriteLine("    --no-distill      Do not summarise the session at the end of a run");
        Console.WriteLine("                       (saves one LLM call; an existing summary is still used)");
        Console.WriteLine("    --quiet, -q       CLI, --ui, --leanui: show only the assistant's messages, warnings");
        Console.WriteLine("                       and errors (hides steps, tool calls and results; a tool call is");
        Console.WriteLine("                       still shown when it asks for your confirmation). Not used by --api");
        Console.WriteLine("    --verbose         With --prompt: show the banner, steps, tool calls and token lines");
        Console.WriteLine("                       as in interactive mode (default: only the answer)");
        Console.WriteLine("    --yes, -y         Approve every tool call automatically (no confirmation");
        Console.WriteLine("                       prompts). The shell command filter stays active");
        Console.WriteLine("    --host <addr>     With --api: address to listen on (default: localhost).");
        Console.WriteLine("                       A non-local address requires an API key");
        Console.WriteLine("    --api-key <key>   With --api: require this key on every request");
        Console.WriteLine("                       (Authorization: Bearer <key> or X-API-Key: <key>)");
        Console.WriteLine();

        C("green");
        Console.WriteLine("  ENVIRONMENT");
        Console.ResetColor();
        Console.WriteLine("    ALBERT_API_KEY    API key for the OpenAI-compatible endpoint (required,");
        Console.WriteLine("                       except for a local --endpoint such as Ollama)");
        Console.WriteLine("    CSAGENT_ENDPOINT  Same as --endpoint");
        Console.WriteLine("    CSAGENT_VISION_MODEL  Same as --vision-model");
        Console.WriteLine("    CSAGENT_API_KEY   Key clients must present in --api mode (same as --api-key)");
        Console.WriteLine("    CSAGENT_MCP_URL   Same as --mcp");
        Console.WriteLine("    CSAGENT_MCP_CONFIG  Same as --mcp-config");
        Console.WriteLine();

        C("green");
        Console.WriteLine("  EXAMPLES");
        Console.ResetColor();
        Console.WriteLine("    csagent                                       CLI mode");
        Console.WriteLine("    csagent --ui                                  Web UI mode (port 5050)");
        Console.WriteLine("    csagent --leanui                              Lean UI mode (port 5050)");
        Console.WriteLine("    csagent --api --yes                           Headless SSE server, no confirmations");
        Console.WriteLine("    csagent --api --yes --host 0.0.0.0 --api-key s3cret   Reachable from the network");
        Console.WriteLine("    csagent --ui --port 8080                      Web UI on port 8080");
        Console.WriteLine("    csagent --model gpt-4o                        CLI with custom model");
        Console.WriteLine("    csagent --ui --model gpt-4o                   Web UI with custom model");
        Console.WriteLine("    csagent --mem my_history.json                 CLI with custom memory file");
        Console.WriteLine("    csagent --ui --mem my_history.json            Web UI with custom memory file");
        Console.WriteLine("    csagent --dry-run                             Dry-run mode (no changes)");
        Console.WriteLine("    csagent --max-retries 5 --retry-delay 2000    Tune 429 retry/backoff");
        Console.WriteLine("    csagent --doc                                 Show documentation");
        Console.WriteLine("    csagent --version                             Show version");
        Console.WriteLine("    csagent --help                                Show this help");
        Console.WriteLine();

        C("dark");
        Console.WriteLine("  NOTE: All destructive actions (write_file) require user confirmation.");
        Console.WriteLine("  File operations are restricted to the current working directory.");
        Console.ResetColor();
        Console.WriteLine();
    }
}