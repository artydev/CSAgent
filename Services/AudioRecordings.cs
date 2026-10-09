using System.Collections.Concurrent;

namespace CsAgent.Services;

/// <summary>
/// Recordings made in the web UI. The browser sends the audio in small chunks while it records, so a
/// closed tab loses seconds, not the whole recording. Audio goes to recordings/, transcripts to
/// transcripts/, both in the working directory, where the agent's tools can see them.
/// </summary>
public sealed class AudioRecordings
{
    public const long MaxBytes = 100L * 1024 * 1024;

    private const string AudioDir = "recordings";
    private const string TextDir = "transcripts";

    // The extension comes from this list, never from the client.
    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audio/webm"] = ".webm", ["audio/ogg"] = ".ogg", ["audio/mp4"] = ".m4a",
        ["audio/wav"] = ".wav", ["audio/mpeg"] = ".mp3",
    };

    private readonly long _maxBytes;
    private readonly ConcurrentDictionary<string, string> _files = new();   // id -> relative path
    private readonly SemaphoreSlim _gate = new(1, 1);                       // one append at a time

    public AudioRecordings(long maxBytes = MaxBytes) => _maxBytes = maxBytes;

    /// <summary>Creates an empty recording. Null when the content type is not an accepted audio type.</summary>
    public (string Id, string Path)? Start(string contentType)
    {
        var mime = contentType.Split(';')[0].Trim();   // "audio/webm;codecs=opus" -> "audio/webm"
        if (!Extensions.TryGetValue(mime, out var ext)) return null;

        Directory.CreateDirectory(AudioDir);
        var id = Guid.NewGuid().ToString("N");
        var file = Path.Combine(AudioDir, $"rec_{DateTime.Now:yyyyMMdd_HHmmss}_{id[..6]}{ext}");
        File.Create(file).Dispose();
        _files[id] = file;
        return (id, file.Replace('\\', '/'));
    }

    /// <summary>Appends a chunk. False when the id is unknown; throws when the recording would pass the limit.</summary>
    public async Task<bool> AppendAsync(string id, Stream chunk)
    {
        if (!_files.TryGetValue(id, out var file)) return false;

        await _gate.WaitAsync();
        try
        {
            await using var fs = new FileStream(file, FileMode.Append, FileAccess.Write);
            var buffer = new byte[64 * 1024];
            int n;
            while ((n = await chunk.ReadAsync(buffer)) > 0)
            {
                if (fs.Length + n > _maxBytes)
                    throw new InvalidOperationException($"recording too large (limit {_maxBytes / (1024 * 1024)} MB)");
                await fs.WriteAsync(buffer.AsMemory(0, n));
            }
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Days to keep recordings, from CSAGENT_AUDIO_KEEP_DAYS. Null (the default) means "keep everything":
    /// nothing is ever deleted unless the user turns this on. Anything that is not a positive whole number is ignored.
    /// </summary>
    public static int? KeepDaysFromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("CSAGENT_AUDIO_KEEP_DAYS");
        return int.TryParse(raw?.Trim(), out var days) && days > 0 ? days : null;
    }

    /// <summary>
    /// Deletes the audio files of recordings/ not modified for <paramref name="keepDays"/> days. Only files this
    /// class creates (rec_*.webm/.ogg/.m4a/.wav/.mp3) are touched: transcripts/ and any other file stay.
    /// Returns how many files were deleted. A file that cannot be deleted is skipped.
    /// </summary>
    public static int PurgeOld(int keepDays, DateTime? nowUtc = null)
    {
        if (keepDays <= 0 || !Directory.Exists(AudioDir)) return 0;
        var limit = (nowUtc ?? DateTime.UtcNow).AddDays(-keepDays);

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(AudioDir, "rec_*"))
        {
            if (!Extensions.ContainsValue(Path.GetExtension(file).ToLowerInvariant())) continue;
            try
            {
                if (File.GetLastWriteTimeUtc(file) >= limit) continue;
                File.Delete(file);
                deleted++;
            }
            catch { /* in use or read-only: next time */ }
        }
        return deleted;
    }

    /// <summary>
    /// The spoken language the client asks for, as a two-letter ISO-639-1 code ("fr", "en"). "fr-FR" gives "fr".
    /// Null (automatic detection) for an empty or unusable value: the text goes to the transcription API, so
    /// only a plain lower-case code is ever forwarded.
    /// </summary>
    public static string? ParseLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var code = value.Trim().ToLowerInvariant().Split('-', '_')[0];
        return code.Length == 2 && code.All(c => c is >= 'a' and <= 'z') ? code : null;
    }

    /// <summary>Relative path of a recording, or null when the id is unknown.</summary>
    public string? PathOf(string id) => _files.TryGetValue(id, out var file) ? file : null;

    /// <summary>Saves the transcript of a recording next to its name (transcripts/rec_….txt) and returns its path.</summary>
    public string SaveTranscript(string id, string text)
    {
        if (!_files.TryGetValue(id, out var file)) throw new InvalidOperationException("unknown recording");
        Directory.CreateDirectory(TextDir);
        var target = Path.Combine(TextDir, Path.GetFileNameWithoutExtension(file) + ".txt");
        File.WriteAllText(target, text);
        return target.Replace('\\', '/');
    }
}
