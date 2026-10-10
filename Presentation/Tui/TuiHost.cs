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
    public static async Task<int> RunAsync(AgentArguments args, WindowsClipboardMonitor clipboard)
    {
        UI.Banner();
        Console.WriteLine($"  CSAgent v{Program.Version}");
        Console.WriteLine();

        var apiKey = LlmConfig.ResolveApiKey();
        if (string.IsNullOrEmpty(apiKey))
        {
            Console.WriteLine($"Error: {LlmConfig.MissingKeyMessage}");
            return args.Prompt is null ? 0 : 1;
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

        // One turn: the user's text in, the answer out (shared by the loop and --prompt).
        async Task<bool> TurnAsync(string input)
        {
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

            var choice = await ModelRouter.ResolveAsync(
                input, JsonHelpers.HistoryContainsImage(messages), args.ModelOverride,
                ModelRouter.LastTurnKind(messages), apiKey);
            var model = choice.Model;
            foreach (var note in choice.Notes) UI.Warning(note);
            Console.WriteLine($"  {choice.Describe()}");

            TaskTracker? tracker = null;
            if (!string.IsNullOrWhiteSpace(args.TaskSlug) && !args.IsDryRun)
                tracker = TaskTracker.Create(args.TaskSlug, input);

            using var agent = new CodingAgent(
                apiKey, LlmConfig.Endpoint, model,
                new AgentOptions(
                    Confirm: !args.AutoApprove,
                    DryRun: args.IsDryRun,
                    Distill: args.Distill,
                    Retry: new RetryPolicy(args.MaxRetries, args.RetryDelayMs),
                    Tracker: tracker),
                new ConsoleObserver(args.Quiet),
                args.McpUrl,
                memory);

            await agent.RunAsync(messages, args.MemoryFile);
            return true;
        }

        // --prompt "text": one request, then exit (the conversation is saved in the memory folder as usual).
        if (!string.IsNullOrWhiteSpace(args.Prompt))
        {
            Console.WriteLine($"\n> User: {args.Prompt}");
            try { await TurnAsync(args.Prompt!); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
            return 0;
        }

        while (true)
        {
            Console.Write("\n> User (type 'exit' to quit): ");
            var input = Console.ReadLine();
            if (input is null) break; // end of input (pipe closed): stop instead of looping forever
            if (string.IsNullOrWhiteSpace(input)) continue;
            if (input.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase)) break;
            await TurnAsync(input);
        }
        return 0;
    }
}
