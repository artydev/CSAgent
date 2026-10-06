using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsAgent.Shared;

/// <summary>
/// Crash-safe persistence helpers for the memory files.
///  • writes go to a temporary file first, then replace the target in one step,
///    so a crash or a concurrent reader never sees a half-written file;
///  • a corrupt file is renamed to "&lt;name&gt;.bad" (kept for inspection) instead of
///    being overwritten or aborting the agent.
/// AOT-safe: JsonNode only.
/// </summary>
public static class AtomicFile
{
    /// <summary>Writes <paramref name="content"/> atomically. Throws on I/O failure.</summary>
    public static async Task WriteAllTextAsync(string path, string content, Encoding? encoding = null)
    {
        // unique name: two writers (e.g. two web requests) never share a temp file
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, content, encoding ?? new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Like <see cref="WriteAllTextAsync"/> but never throws: memory is an aid, and a locked
    /// or read-only file must not stop the agent. Returns false (and logs) on failure.
    /// </summary>
    public static async Task<bool> TryWriteAllTextAsync(string path, string content, Encoding? encoding = null)
    {
        try
        {
            await WriteAllTextAsync(path, content, encoding);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[Memory] cannot save '{path}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Reads a JSON array file. Returns null when the file is missing, unreadable, or corrupt.
    /// A corrupt file (invalid JSON, empty, not an array) is renamed to "&lt;path&gt;.bad".
    /// </summary>
    public static async Task<JsonArray?> ReadJsonArrayAsync(string path)
    {
        if (!File.Exists(path)) return null;

        string text;
        try { text = await File.ReadAllTextAsync(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // locked / no permission: not corruption, do not touch the file
            Console.Error.WriteLine($"[Memory] cannot read '{path}': {ex.Message}");
            return null;
        }

        try
        {
            return JsonNode.Parse(text) as JsonArray
                   ?? throw new JsonException("top-level value is not an array");
        }
        catch (JsonException ex)
        {
            Quarantine(path);
            Console.Error.WriteLine($"[Memory] '{path}' is corrupt ({ex.Message}); moved to '{path}.bad', starting empty.");
            return null;
        }
    }

    /// <summary>
    /// Like <see cref="ReadJsonArrayAsync"/> for a file whose top-level value is a JSON object.
    /// Returns null when missing, unreadable or corrupt (a corrupt file is renamed to ".bad").
    /// </summary>
    public static async Task<JsonObject?> ReadJsonObjectAsync(string path)
    {
        if (!File.Exists(path)) return null;

        string text;
        try { text = await File.ReadAllTextAsync(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[Memory] cannot read '{path}': {ex.Message}");
            return null;
        }

        try
        {
            return JsonNode.Parse(text) as JsonObject
                   ?? throw new JsonException("top-level value is not an object");
        }
        catch (JsonException ex)
        {
            Quarantine(path);
            Console.Error.WriteLine($"[Memory] '{path}' is corrupt ({ex.Message}); moved to '{path}.bad', starting empty.");
            return null;
        }
    }

    /// <summary>Renames <paramref name="path"/> to "&lt;path&gt;.bad", replacing an older one.</summary>
    public static void Quarantine(string path)
    {
        try { File.Move(path, path + ".bad", overwrite: true); }
        catch { /* best effort */ }
    }
}