using System.Text.Json.Nodes;
using CsAgent.Core.Agent;
using CsAgent.Services;

namespace CsAgent.Presentation.Web;

/// <summary>The recorder of the web UI (not mapped in --api mode): upload in chunks, then transcribe.</summary>
public static class AudioEndpoints
{
    public static IEndpointRouteBuilder MapAudioEndpoints(this IEndpointRouteBuilder app)
    {
        var recordings = new AudioRecordings();

        // Opt-in clean-up of old audio, once per start (CSAGENT_AUDIO_KEEP_DAYS=30). Transcripts are never deleted.
        if (AudioRecordings.KeepDaysFromEnvironment() is { } keepDays)
        {
            var purged = AudioRecordings.PurgeOld(keepDays);
            if (purged > 0) Console.WriteLine($"--- Deleted {purged} recording(s) older than {keepDays} day(s) from recordings/ ---");
        }

        // A page from another site must not be able to write files here.
        static bool SameOrigin(HttpContext ctx)
        {
            var origin = ctx.Request.Headers["Origin"].ToString();
            return origin.Length == 0 || origin == $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        }

        // 1. start: POST /api/audio/start?type=audio/webm;codecs=opus  ->  {"id", "path"}
        app.MapPost("/api/audio/start", async (HttpContext ctx) =>
        {
            if (!SameOrigin(ctx)) { ctx.Response.StatusCode = 403; return; }
            var started = recordings.Start(ctx.Request.Query["type"].ToString());
            if (started is null) { ctx.Response.StatusCode = 415; await ctx.Response.WriteAsync("Unsupported audio type"); return; }

            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(new JsonObject { ["id"] = started.Value.Id, ["path"] = started.Value.Path }.ToJsonString());
        });

        // 2. chunks: POST /api/audio/{id}, the raw bytes in the body, in order
        app.MapPost("/api/audio/{id}", async (HttpContext ctx, string id) =>
        {
            if (!SameOrigin(ctx)) { ctx.Response.StatusCode = 403; return; }
            try
            {
                if (!await recordings.AppendAsync(id, ctx.Request.Body))
                    ctx.Response.StatusCode = 404;
            }
            catch (InvalidOperationException ex)
            {
                ctx.Response.StatusCode = 413;
                await ctx.Response.WriteAsync(ex.Message);
            }
        });

        // 3. transcribe: POST /api/audio/{id}/transcribe?lang=fr  ->  SSE: progress, then done or error
        app.MapPost("/api/audio/{id}/transcribe", async (HttpContext ctx, string id) =>
        {
            if (!SameOrigin(ctx)) { ctx.Response.StatusCode = 403; return; }
            var path = recordings.PathOf(id);
            if (path is null) { ctx.Response.StatusCode = 404; return; }

            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";

            async Task Send(JsonObject message)
            {
                await ctx.Response.WriteAsync($"data: {message.ToJsonString()}\n\n");
                await ctx.Response.Body.FlushAsync();
            }

            try
            {
                var lang = ctx.Request.Query["lang"].ToString();
                var text = await ToolDispatcher.TranscribeFileAsync(path, lang.Length == 0 ? null : lang,
                    (part, total) => Send(new JsonObject { ["type"] = "progress", ["part"] = part, ["total"] = total }));

                if (text.Length == 0) { await Send(new JsonObject { ["type"] = "error", ["message"] = "No speech detected." }); return; }
                var saved = recordings.SaveTranscript(id, text);
                await Send(new JsonObject { ["type"] = "done", ["path"] = saved, ["text"] = text });
            }
            catch (Exception ex)
            {
                await Send(new JsonObject { ["type"] = "error", ["message"] = ex.Message });
            }
        });

        return app;
    }
}
