using CsAgent.Core.Llm;
using CsAgent.Shared;

namespace CsAgent.Presentation.Web;

/// <summary>
/// Headless API mode (<c>--api</c>): the same SSE endpoints as the Web UI
/// (<c>/api/chat</c>, <c>/api/confirm</c>), but no HTML/JS/CSS, no browser
/// launch and no clipboard access — meant to be driven by another program.
/// </summary>
public static class ApiHost
{
    public static int Run(AgentArguments args)
    {
        var error = ApiSecurity.CheckStartup(args.Host, args.ApiKey);
        if (error is not null)
        {
            Console.Error.WriteLine("Error: " + error);
            return 2;
        }

        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Logging.SetMinimumLevel(LogLevel.Critical);

        var app = builder.Build();

        // Authentication: every request must carry the key when one is configured.
        if (args.ApiKey is not null)
        {
            var key = args.ApiKey;
            app.Use(async (ctx, next) =>
            {
                if (ApiSecurity.IsAuthorized(key,
                        ctx.Request.Headers.Authorization.ToString(),
                        ctx.Request.Headers["X-API-Key"].ToString()))
                {
                    await next();
                    return;
                }
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.Headers.WWWAuthenticate = "Bearer";
                await ctx.Response.WriteAsync("Unauthorized");
            });
        }

        app.MapEndpoints(
            args.MemoryFile, args.ModelOverride, args.McpUrl,
            new RetryPolicy(args.MaxRetries, args.RetryDelayMs),
            args.TaskSlug,
            clipboard: null,                 // never attach a clipboard image to an API request
            distill: args.Distill,
            confirm: !args.AutoApprove);

        var url = $"http://{args.Host}:{args.Port}";

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            Console.WriteLine($"\n--- CSAgent API (SSE) listening on {url} ---");
            Console.WriteLine("--- Endpoints: GET/POST /api/chat, POST /api/confirm ---");
            Console.WriteLine(args.ApiKey is null
                ? "--- Authentication: none (localhost only) ---"
                : "--- Authentication: API key required (Authorization: Bearer <key> or X-API-Key) ---");
            Console.WriteLine(args.AutoApprove
                ? "--- Auto-approve: ON (tools run without confirmation; the shell command filter stays active) ---"
                : "--- Auto-approve: OFF (destructive tools wait for POST /api/confirm) ---");
            if (!string.IsNullOrWhiteSpace(args.McpUrl))
                Console.WriteLine($"--- MCP endpoint: {args.McpUrl} ---");
            Console.WriteLine($"--- LLM endpoint: {LlmConfig.Endpoint} ---");
            if (string.IsNullOrEmpty(LlmConfig.ResolveApiKey()))
                Console.WriteLine("--- WARNING: ALBERT_API_KEY is not set; every request will fail ---");
        });

        app.Run(url);
        return 0;
    }
}
