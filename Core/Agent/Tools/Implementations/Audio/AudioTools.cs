using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using CsAgent.Core.Llm;

namespace CsAgent.Core.Agent;

public static partial class ToolDispatcher
{
    private const long MaxAudioBytes = 100L * 1024 * 1024;
    private const long MaxDirectAudioBytes = 24L * 1024 * 1024;   // sent as is when ffmpeg is missing
    private const int AudioSegmentSeconds = 600;                  // 10 min of 16 kHz mono WAV = 19 MB
    private const int DefaultTranscriptChars = 50_000;
    private const int MaxTranscriptChars = 200_000;

    /// <summary>transcribe_audio (tool): the transcript of an audio file, cut at max_chars. Read-only.</summary>
    private static async Task<string> TranscribeAudioAsync(string path, string? language, int? maxChars)
    {
        try
        {
            var text = await TranscribeFileAsync(path, language, null);
            if (text.Length == 0) return "(no speech detected)";

            var max = Math.Clamp(maxChars ?? DefaultTranscriptChars, 1, MaxTranscriptChars);
            return text.Length <= max
                ? text
                : text[..max] + $"\n[... truncated: {text.Length} characters in total, raise max_chars to read more]";
        }
        catch (Exception ex) { return $"Error: transcribe_audio — {ex.Message}"; }
    }

    /// <summary>
    /// Speech to text through the endpoint's /audio/transcriptions (Whisper). With ffmpeg the audio is
    /// converted to 16 kHz mono WAV and cut into 10-minute parts, one request per part; without it only
    /// .wav / .mp3 files up to 24 MB are accepted. <paramref name="progress"/> gets (part, total) before
    /// each part. Returns "" when there is no speech; throws with a readable message on any failure.
    /// Shared by the transcribe_audio tool and the web UI's recorder.
    /// </summary>
    internal static async Task<string> TranscribeFileAsync(string path, string? language, Func<int, int, Task>? progress)
    {
        var full = Path.GetFullPath(path);
        if (!IsSafePath(full))
            throw new Exception($"Path '{full}' is not allowed for reading. Only files in the current working directory are permitted.");
        if (!File.Exists(full)) throw new Exception($"not found '{full}'");
        var len = new FileInfo(full).Length;
        if (len == 0) throw new Exception($"'{full}' is empty.");
        if (len > MaxAudioBytes)
            throw new Exception($"file too large ({len / (1024 * 1024)} MB, limit {MaxAudioBytes / (1024 * 1024)} MB).");

        var apiKey = LlmConfig.ResolveApiKey();
        if (string.IsNullOrEmpty(apiKey)) throw new Exception(LlmConfig.MissingKeyMessage);

        string? tmp = null;
        try
        {
            tmp = Directory.CreateTempSubdirectory("csagent-audio-").FullName;
            var parts = await SplitAudioAsync(full, len, tmp);

            var model = Environment.GetEnvironmentVariable("CSAGENT_TRANSCRIBE_MODEL");
            if (string.IsNullOrWhiteSpace(model)) model = LlmSettings.TranscribeModel;

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var texts = new List<string>();
            for (int i = 0; i < parts.Count; i++)
            {
                if (progress is not null) await progress(i + 1, parts.Count);
                texts.Add(await TranscribePartAsync(client, apiKey, model, language, parts[i]));
            }
            return string.Join(" ", texts).Trim();
        }
        finally
        {
            if (tmp is not null) { try { Directory.Delete(tmp, true); } catch { /* best effort */ } }
        }
    }

    /// <summary>The audio parts to send. ffmpeg when available; else the file itself if Whisper takes it as is.</summary>
    private static async Task<List<string>> SplitAudioAsync(string full, long len, string tmp)
    {
        var ffmpeg = Environment.GetEnvironmentVariable("CSAGENT_FFMPEG");
        if (string.IsNullOrWhiteSpace(ffmpeg)) ffmpeg = "ffmpeg";

        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[]
        {
            "-nostdin", "-y", "-v", "error", "-i", full, "-vn",
            "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le",
            "-f", "segment", "-segment_time", AudioSegmentSeconds.ToString(),
            Path.Combine(tmp, "part_%03d.wav")
        })
            psi.ArgumentList.Add(a);

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Win32Exception) { proc = null; }   // ffmpeg is not installed

        if (proc is null)
        {
            var ext = Path.GetExtension(full).ToLowerInvariant();
            if ((ext is ".wav" or ".mp3") && len <= MaxDirectAudioBytes) return new List<string> { full };
            throw new Exception("ffmpeg was not found (install it or set CSAGENT_FFMPEG). Without ffmpeg only .wav and .mp3 files up to 24 MB are accepted.");
        }

        using (proc)
        {
            var stderr = proc.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { /* already gone */ }
                throw new Exception("ffmpeg timed out after 10 minutes.");
            }
            if (proc.ExitCode != 0)
                throw new Exception($"ffmpeg failed (exit {proc.ExitCode}): {Head(await stderr, 500)}");
        }

        var parts = Directory.GetFiles(tmp, "part_*.wav");
        Array.Sort(parts, StringComparer.Ordinal);
        if (parts.Length == 0) throw new Exception("ffmpeg produced no audio (does the file have an audio track?).");
        return new List<string>(parts);
    }

    private static string Head(string s, int max) => s.Length <= max ? s : s[..max];

    private static async Task<string> TranscribePartAsync(HttpClient client, string apiKey, string model, string? language, string part)
    {
        var ext = Path.GetExtension(part).ToLowerInvariant();

        using var request = new HttpRequestMessage(HttpMethod.Post, LlmConfig.Endpoint + "/audio/transcriptions");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(part));
        file.Headers.TryAddWithoutValidation("Content-Type", ext == ".mp3" ? "audio/mpeg" : "audio/wav");
        form.Add(file, "file", "audio" + ext);   // fixed ASCII name: the real one may hold any character
        form.Add(new StringContent(model), "model");
        if (!string.IsNullOrWhiteSpace(language)) form.Add(new StringContent(language), "language");
        request.Content = form;

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new Exception($"API returned {(int)response.StatusCode}: {Head(body, 500)}");

        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch { throw new Exception($"could not parse the response: {Head(body, 200)}"); }
        return root?["text"]?.GetValue<string>() ?? throw new Exception($"no 'text' in the response: {Head(body, 200)}");
    }
}
