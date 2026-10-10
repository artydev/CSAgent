using CsAgent.Core.Abstractions;
using CsAgent.Core.Agent;
using CsAgent.Core.Llm;
using CsAgent.Core.Memory;
using CsAgent.Core.Tasks;
using CsAgent.Infrastructure.Clipboard;
using CsAgent.Services;                    // ← ADD
using CsAgent.Shared;
using CsAgent.Services;

namespace CsAgent.Presentation.Web;

public static class ApiEndpoints
{
    private const long MaxImageBytes = 10 * 1024 * 1024;

    public static IEndpointRouteBuilder MapEndpoints(
        this IEndpointRouteBuilder app,
        string memoryFile,
        string? modelOverride = null,
        string? mcpUrl = null,
        RetryPolicy? retry = null,
        string? taskSlug = null,
        WindowsClipboardMonitor? clipboard = null,
        bool distill = true,
        bool confirm = true,
        bool quiet = false)
    {
        var broker = new ConfirmationBroker();

        // Created once — shared across all requests in this server session
        var memory = new HybridMemoryManager(   // ← ADD
            new SemanticMemory(),
            new ExactMemory());

        app.MapPost("/api/confirm", async (HttpContext ctx) =>
        {
            using var sr = new StreamReader(ctx.Request.Body);
            var body = await sr.ReadToEndAsync();
            var allow = body.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            var resolved = broker.Resolve(allow);
            ctx.Response.StatusCode = resolved ? 200 : 409;
            await ctx.Response.WriteAsync(resolved ? "ok" : "no pending confirmation");
        });

        app.MapGet("/api/chat", async (HttpContext ctx, string prompt) =>
        {
            var slug = ctx.Request.Query["task"].ToString();
            if (string.IsNullOrWhiteSpace(slug)) slug = taskSlug;

            await RunChatAsync(ctx, prompt, null, null,
                               memoryFile, modelOverride, mcpUrl, retry, broker, slug, memory, distill, confirm, quiet);
        });

        app.MapPost("/api/chat", async (HttpContext ctx) =>
        {
            if (!ctx.Request.HasFormContentType)
            { ctx.Response.StatusCode = 415; await ctx.Response.WriteAsync("Expected multipart/form-data"); return; }

            IFormCollection form;
            try { form = await ctx.Request.ReadFormAsync(); }
            catch (Exception ex)
            { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync($"Form read error: {ex.Message}"); return; }

            var prompt = form["prompt"].ToString();
            if (string.IsNullOrWhiteSpace(prompt))
            { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("Missing 'prompt' field"); return; }

            var slug = form["task"].ToString();
            if (string.IsNullOrWhiteSpace(slug)) slug = taskSlug;

            string? imageBase64 = null, imageMime = null;

            var file = form.Files.GetFile("image");
            if (file is { Length: > 0 })
            {
                if (file.Length > MaxImageBytes)
                { ctx.Response.StatusCode = 413; await ctx.Response.WriteAsync($"Image too large (max {MaxImageBytes / 1024 / 1024} MB)"); return; }

                imageMime = ResolveMimeType(file.FileName, file.ContentType);
                if (!IsSupportedImageMime(imageMime))
                { ctx.Response.StatusCode = 415; await ctx.Response.WriteAsync($"Unsupported image type: {imageMime}"); return; }

                using var ms = new MemoryStream((int)file.Length);
                await file.CopyToAsync(ms);
                imageBase64 = Convert.ToBase64String(ms.ToArray());
            }
            else
            {
                var clipped = clipboard?.ConsumeLatest();
                if (clipped is not null)
                {
                    imageBase64 = Convert.ToBase64String(clipped.PngBytes);
                    imageMime = "image/png";
                }
            }

            await RunChatAsync(ctx, prompt, imageBase64, imageMime,
                               memoryFile, modelOverride, mcpUrl, retry, broker, slug, memory, distill, confirm, quiet);
        });

        return app;
    }

    private static async Task RunChatAsync(
        HttpContext ctx,
        string prompt,
        string? imageBase64,
        string? imageMime,
        string memoryFile,
        string? modelOverride,
        string? mcpUrl,
        RetryPolicy? retry,
        ConfirmationBroker broker,
        string? taskSlug,
        HybridMemoryManager memory,           // ← ADD parameter
        bool distill = true,
        bool confirm = true,
        bool quiet = false)
    {
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";

        // --quiet (web UIs): steps, tool calls and results are not sent; --api always sends everything.
        var observer = QuietObserver.Wrap(new SseObserver(ctx.Response, broker), quiet);

        var apiKey = LlmConfig.ResolveApiKey();
        if (string.IsNullOrEmpty(apiKey))
        { await observer.OnError("API Key not set."); return; }

        var msgs = await MemoryStore.LoadAsync(MemoryPaths.Resolve(memoryFile).Conversation);
        if (msgs.Count == 0)
            msgs.Add(CodingAgent.SystemMessage(OperatingSystem.IsWindows()));

        if (imageBase64 is not null && imageMime is not null)
            msgs.Add(JsonHelpers.MultimodalMessage("user", prompt, imageBase64, imageMime));
        else
            msgs.Add(JsonHelpers.Message("user", prompt));

        var needsVision = imageBase64 is not null || JsonHelpers.HistoryContainsImage(msgs);
        var choice = await ModelRouter.ResolveAsync(
            prompt, needsVision, modelOverride, ModelRouter.LastTurnKind(msgs), apiKey);
        var model = choice.Model;
        // Say so when the default code model is not the one answering (chat, vision, or a fallback).
        if (choice.Profile is ModelProfile.Chat || choice.Reason.StartsWith("chat model", StringComparison.Ordinal))
            await observer.OnThought(choice.Describe());

        TaskTracker? tracker = null;
        if (!string.IsNullOrWhiteSpace(taskSlug))
            tracker = TaskTracker.Create(taskSlug, prompt);

        using var agent = new CodingAgent(
            apiKey, LlmConfig.Endpoint, model,
            new AgentOptions(Confirm: confirm, Retry: retry, Tracker: tracker, Distill: distill),
            observer,
            mcpUrl,
            memory);                          // ← ADD

        await agent.RunAsync(msgs, memoryFile);
    }

    private static string ResolveMimeType(string fileName, string browserContentType)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => string.IsNullOrWhiteSpace(browserContentType)
                    ? "application/octet-stream"
                    : browserContentType
        };
    }

    private static bool IsSupportedImageMime(string mime) =>
        mime is "image/jpeg" or "image/png" or "image/gif" or "image/webp";
}