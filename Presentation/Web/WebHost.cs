using System.Diagnostics;
using CsAgent.Core.Llm;
using CsAgent.Infrastructure.Clipboard;
using CsAgent.Shared;

namespace CsAgent.Presentation.Web;

public static class WebHost
{
    public static void Run(AgentArguments args, WindowsClipboardMonitor clipboard)
    {
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Logging.SetMinimumLevel(LogLevel.Critical);

        var app = builder.Build();

        app.MapGet("/", () => Results.Content(StaticAssets.HtmlUI, "text/html"));
        app.MapGet("/app.js", () => Results.Content(StaticAssets.JsUI, "application/javascript"));
        app.MapGet("/styles.css", () => Results.Content(StaticAssets.CssUI, "text/css"));

        app.MapEndpoints(
            args.MemoryFile, args.ModelOverride, args.McpUrl,
            new RetryPolicy(args.MaxRetries, args.RetryDelayMs),
            args.TaskSlug, clipboard, distill: args.Distill, confirm: !args.AutoApprove, quiet: args.Quiet);
        app.MapAudioEndpoints();

        var url = $"http://localhost:{args.Port}";

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            Console.WriteLine($"\n--- Server started at {url} ---");
            if (args.Quiet)
                Console.WriteLine("--- Quiet: steps, tool calls and results are hidden ---");
            if (args.AutoApprove)
                Console.WriteLine("--- Auto-approve: ON (tools run without confirmation) ---");
            if (!string.IsNullOrWhiteSpace(args.McpUrl))
                Console.WriteLine($"--- MCP endpoint: {args.McpUrl} ---");
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        });

        app.Run(url);
    }
}