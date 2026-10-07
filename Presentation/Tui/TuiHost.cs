using CsAgent.Core.Agent;
using CsAgent.Core.Llm;
using CsAgent.Core.Memory;
using CsAgent.Core.Tasks;
using CsAgent.Infrastructure.Clipboard;
using CsAgent.Services;
using CsAgent.Shared;
using CsAgent.Services;

namespace CsAgent.Presentation.Tui;

public static class TuiHost
{
    public static async Task RunAsync(AgentArguments args, WindowsClipboardMonitor clipboard)
    {
        UI.Banner();
        Console.WriteLine($"  CSAgent v{Program.Version}");
        Console.WriteLine();

        var apiKey = Environment.GetEnvironmentVariable("ALBERT_API_KEY") ?? "";
        if (string.IsNullOrEmpty(apiKey))
        {
            Console.WriteLine("Error: ALBERT_API_KEY env var not set.");
            return;
        }

        var messages = await MemoryStore.LoadAsync(MemoryPaths.Resolve(args.MemoryFile).Conversation);
        if (messages.Count == 0)
            messages.Add(CodingAgent.SystemMessage(OperatingSystem.IsWindows()));

        if (!string.IsNullOrWhiteSpace(args.McpUrl))
            Console.WriteLine($"  MCP: {args.McpUrl}");
        if (args.IsDryRun)
            Console.WriteLine("  Dry-run: ON (no changes will be made)");
        if (args.AutoApprove)
            Console.WriteLine("  Auto-approve: ON (tools run without confirmation)");
        if (!string.IsNullOrWhiteSpace(args.TaskSlug))
            Console.WriteLine($"  Task: {args.TaskSlug}");
        Console.WriteLine();

        // Created once — accumulates patterns across all turns in this session
        var memory = new HybridMemoryManager(
            new SemanticMemory(),
            new ExactMemory());

        while (true)
        {
            Console.Write("\n> User (type 'exit' to quit): ");
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) continue;
            if (input.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase)) break;

            var image = clipboard.ConsumeLatest();
            if (image is not null)
            {
                var b64 = Convert.ToBase64String(image.PngBytes);
                messages.Add(JsonHelpers.MultimodalMessage("user", input, b64, "image/png"));
            }
            else
            {
                messages.Add(JsonHelpers.Message("user", input));
            }

            var model = args.ModelOverride
                        ?? (JsonHelpers.HistoryContainsImage(messages)
                                ? LlmSettings.VisionModel
                                : LlmSettings.Model);
            Console.WriteLine($"  [model: {model}]");

            TaskTracker? tracker = null;
            if (!string.IsNullOrWhiteSpace(args.TaskSlug) && !args.IsDryRun)
                tracker = TaskTracker.Create(args.TaskSlug, input);

            using var agent = new CodingAgent(
                apiKey, LlmSettings.Endpoint, model,
                new AgentOptions(
                    Confirm: !args.AutoApprove,
                    DryRun: args.IsDryRun,
                    Distill: args.Distill,
                    Retry: new RetryPolicy(args.MaxRetries, args.RetryDelayMs),
                    Tracker: tracker),
                new ConsoleObserver(),
                args.McpUrl,
                memory);

            await agent.RunAsync(messages, args.MemoryFile);
        }
    }
}