using System.Text.Json.Nodes;
using CsAgent.Shared;

namespace CsAgent.Tests;

static partial class Tests
{
    // An image makes only the turn it is sent with a vision turn; later turns go back to the normal routing.
    static async Task VisionHistoryGroup()
    {
        Group("Vision: only the current message's image counts");

        static JsonArray History()
        {
            var a = new JsonArray();
            a.Add(JsonHelpers.Message("system", "sys"));
            a.Add(JsonHelpers.MultimodalMessage("user", "describe this", "AAAA", "image/png"));
            a.Add(JsonHelpers.Message("assistant", "A brown button."));
            return a;
        }

        await T("image in the latest user message: vision turn", async () =>
        {
            var h = History();
            await Task.CompletedTask;
            return JsonHelpers.LastUserMessageHasImage(h) ? null : "expected true";
        });

        await T("image sent earlier, new text question: not a vision turn", async () =>
        {
            var h = History();
            h.Add(JsonHelpers.Message("user", "what is diarization?"));
            await Task.CompletedTask;
            return JsonHelpers.LastUserMessageHasImage(h) ? "expected false" : null;
        });

        await T("tool messages after the image do not hide it", async () =>
        {
            var h = new JsonArray();
            h.Add(JsonHelpers.Message("system", "sys"));
            h.Add(JsonHelpers.MultimodalMessage("user", "look", "AAAA", "image/png"));
            h.Add(JsonHelpers.Message("assistant", "ok"));
            await Task.CompletedTask;
            return JsonHelpers.LastUserMessageHasImage(h) ? null : "expected true";
        });

        await T("DropImages: image becomes a placeholder, text is kept, content is a plain string", async () =>
        {
            var h = History();
            int n = JsonHelpers.DropImages(h);
            await Task.CompletedTask;
            if (n != 1) return $"changed {n}";
            if (JsonHelpers.HistoryContainsImage(h)) return "image still there";
            var c = h[1]!["content"];
            if (c is not JsonValue v || !v.TryGetValue<string>(out var s)) return "content is not a string";
            if (!s.Contains("describe this") || !s.Contains(JsonHelpers.OldImagePlaceholder)) return "got: " + s;
            return null;
        });

        await T("DropImages: nothing to do on a text-only history", async () =>
        {
            var h = new JsonArray { JsonHelpers.Message("system", "s"), JsonHelpers.Message("user", "hi") };
            await Task.CompletedTask;
            return JsonHelpers.DropImages(h) == 0 ? null : "changed something";
        });
    }
}
