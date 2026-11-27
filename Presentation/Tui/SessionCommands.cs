using CsAgent.Core.Agent;
using CsAgent.Core.Llm;
using CsAgent.Core.Memory;
using CsAgent.Services;
using CsAgent.Shared;
using System.Text.Json.Nodes;

namespace CsAgent.Presentation.Tui;

/// <summary>
/// aichat-like session commands for the interactive prompt:
/// <c>.session</c> (info), <c>.list</c>, <c>.save</c>, <c>.use</c>, <c>.compress</c>, <c>.empty</c>, <c>.delete</c>.
/// A session is the memory folder chosen with <c>--mem</c> / <c>--session</c>.
/// </summary>
public static class SessionCommands
{
    /// <summary>
    /// <paramref name="Handled"/>: the input was a session command (whatever the outcome).
    /// <paramref name="SwitchTo"/>: the name of the session to load next (".use"); the caller does the switch.
    /// </summary>
    public readonly record struct Result(bool Handled, string? SwitchTo = null);

    public static async Task<Result> TryHandleAsync(
        string input, JsonArray messages, AgentArguments args, string apiKey)
    {
        var t = input.Trim();
        if (t.Equals(".use", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith(".use ", StringComparison.OrdinalIgnoreCase))
            return Use(t.Length > 4 ? t[4..].Trim() : "", args);
        return new Result(await HandleAsync(input, messages, args, apiKey));
    }

    private static Result Use(string name, AgentArguments args)
    {
        if (name.Length == 0) { Console.WriteLine("  Usage: .use <name>   (see .list; a new name starts a new session)"); return new(true); }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || name.Contains(' '))
        { Console.WriteLine("  Invalid name: use letters, digits, - and _ only."); return new(true); }
        if (Path.GetFullPath(name) == Path.GetFullPath(MemoryPaths.Resolve(args.MemoryFile).Directory))
        { Console.WriteLine($"  Already in session '{name}'."); return new(true); }
        return new(true, name);
    }

    /// <summary>True when the input was a session command (handled, whatever the outcome).</summary>
    private static async Task<bool> HandleAsync(
        string input, JsonArray messages, AgentArguments args, string apiKey)
    {
        var trimmed = input.Trim();
        var cmd = trimmed.ToLowerInvariant();
        if (!cmd.StartsWith('.')) return false;

        var paths = MemoryPaths.Resolve(args.MemoryFile);

        // ".save" (flush now) or ".save <name>" (copy this session to a new named session).
        if (cmd == ".save" || cmd.StartsWith(".save "))
        {
            var name = trimmed.Length > 5 ? trimmed[5..].Trim() : "";
            if (!SessionPersistence.Enabled && name.Length == 0)
            {
                Console.WriteLine("  --no-save is on: nothing is written. Use .save <name> to keep this conversation.");
                return true;
            }
            if (SessionPersistence.Enabled) await MemoryStore.SaveAsync(paths.Conversation, messages);
            if (name.Length == 0)
            {
                Console.WriteLine($"  Session saved in {paths.Directory}/");
                return true;
            }
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || name.Contains(' '))
            {
                Console.WriteLine("  Invalid name: use letters, digits, - and _ only.");
                return true;
            }
            if (Directory.Exists(name))
            {
                Console.WriteLine($"  '{name}' already exists: choose another name (nothing was overwritten).");
                return true;
            }
            try
            {
                Directory.CreateDirectory(name);
                if (!SessionPersistence.Enabled)
                {
                    // --no-save: the memory files were never updated, so only the conversation is kept.
                    await MemoryStore.SaveAsync(Path.Combine(name, MemoryPaths.ConversationFile), messages, force: true);
                    Console.WriteLine($"  Saved as '{name}' (conversation only, --no-save is on). Resume it with: csagent --session {name}");
                    return true;
                }
                foreach (var f in MemoryPaths.FileNames)
                {
                    var source = Path.Combine(paths.Directory, f);
                    if (File.Exists(source)) File.Copy(source, Path.Combine(name, f));
                }
                Console.WriteLine($"  Saved as '{name}'. Resume it later with: csagent --session {name}");
            }
            catch (Exception ex) { Console.WriteLine($"  Save failed: {ex.Message}"); }
            return true;
        }

        // ".delete <name>": removes ONLY the memory files of that session, archive included (then the folder if empty).
        if (cmd == ".delete" || cmd.StartsWith(".delete "))
        {
            var name = trimmed.Length > 7 ? trimmed[7..].Trim() : "";
            if (name.Length == 0) { Console.WriteLine("  Usage: .delete <name>   (see .list)"); return true; }
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || name.Contains(' '))
            { Console.WriteLine("  Invalid name."); return true; }
            if (Path.GetFullPath(name) == Path.GetFullPath(paths.Directory))
            { Console.WriteLine("  This is the current session: use .empty to clear it, or restart with another --session first."); return true; }
            if (!File.Exists(Path.Combine(name, MemoryPaths.ConversationFile)))
            { Console.WriteLine($"  No session named '{name}' here (see .list)."); return true; }

            Console.Write($"  Delete session '{name}'? This cannot be undone (y/N): ");
            var answer = Console.ReadLine();
            if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            { Console.WriteLine("  Cancelled."); return true; }
            try
            {
                foreach (var f in MemoryPaths.FileNames)
                {
                    var path = Path.Combine(name, f);
                    if (File.Exists(path)) File.Delete(path);
                }
                if (!Directory.EnumerateFileSystemEntries(name).Any()) Directory.Delete(name);
                Console.WriteLine($"  Session '{name}' deleted.");
            }
            catch (Exception ex) { Console.WriteLine($"  Delete failed: {ex.Message}"); }
            return true;
        }

        switch (cmd)
        {
            case ".help":
                Console.WriteLine("  .session          info on the current session");
                Console.WriteLine("  .list             list the sessions in this folder");
                Console.WriteLine("  .save [name]      save now, or copy to a new named session");
                Console.WriteLine("  .compress         summarise the older messages now");
                Console.WriteLine("  .empty            clear the current session");
                Console.WriteLine("  .use <name>       switch to another session (a new name starts a new one)");
                Console.WriteLine("  .delete <name>    delete another session");
                return true;

            case ".session":
            case ".info":
                Console.WriteLine($"  Session   : {paths.Directory}");
                Console.WriteLine($"  Messages  : {messages.Count}");
                Console.WriteLine($"  Size      : {SessionCompressor.SizeChars(messages):N0} chars (~{SessionCompressor.EstimateTokens(messages):N0} tokens)");
                Console.WriteLine($"  Compress  : {(ContextBudget.CompressEnabled ? $"at {ContextBudget.CompressChars:N0} chars" : "off")}; hard limit {ContextBudget.MaxChars:N0}");
                Console.WriteLine($"  Summarised: {(messages.Count > 1 && SessionCompressor.IsSummary(messages[1]) ? "yes" : "no")}");
                var archive = new FileInfo(paths.Archive);
                Console.WriteLine($"  Archive   : {(archive.Exists ? $"{archive.Length / 1024.0:N0} KB of older messages (searched by recall)" : "empty")}");
                Console.WriteLine($"  Saving    : {(SessionPersistence.Enabled ? "on" : "off (--no-save)")}");
                return true;

            case ".compress":
                try
                {
                    using var client = new LlmClient(apiKey, LlmConfig.Endpoint,
                        LlmConfig.ChatModel ?? LlmConfig.CodeModel, new RetryPolicy(args.MaxRetries, args.RetryDelayMs));
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                    var before = messages.ToList();
                    var done = await SessionCompressor.CompressAsync(messages, client, ContextBudget.KeepRecentMessages, cts.Token);
                    if (done)
                    {
                        ArchiveQuietly(before, messages, paths);
                        await MemoryStore.SaveAsync(paths.Conversation, messages);
                    }
                    Console.WriteLine(done ? "  Session compressed." : "  Nothing to compress yet.");
                }
                catch (Exception ex) { Console.WriteLine($"  Compression failed: {ex.Message}"); }
                return true;

            case ".empty":
                var emptied = messages.ToList();
                SessionCompressor.Empty(messages);
                ArchiveQuietly(emptied, messages, paths);
                await MemoryStore.SaveAsync(paths.Conversation, messages);
                Console.WriteLine(File.Exists(paths.Archive)
                    ? "  Session emptied (system prompt kept). The messages stay searchable by recall; delete archive.jsonl to forget them."
                    : "  Session emptied (system prompt kept).");
                return true;

            case ".list":
                var current = Path.GetFullPath(paths.Directory);
                var dirs = Directory.GetDirectories(".")
                    .Where(d => File.Exists(Path.Combine(d, MemoryPaths.ConversationFile)))
                    .OrderBy(d => Path.GetFileName(d)).ToList();
                if (dirs.Count == 0) { Console.WriteLine("  No session in this folder."); return true; }
                Console.WriteLine("  Sessions (* = current; start one with --session <name>):");
                foreach (var d in dirs)
                {
                    var fi = new FileInfo(Path.Combine(d, MemoryPaths.ConversationFile));
                    var mark = Path.GetFullPath(d) == current ? "*" : " ";
                    Console.WriteLine($"   {mark} {Path.GetFileName(d),-24} {fi.Length / 1024,6:N0} KB   {fi.LastWriteTime:yyyy-MM-dd HH:mm}");
                }
                return true;

            default:
                return false;   // unknown ".xyz": treated as normal text
        }
    }

    /// <summary>Messages removed by .compress or .empty go to the archive, like the ones the agent trims.</summary>
    private static void ArchiveQuietly(List<JsonNode?> before, JsonArray messages, MemoryPaths paths)
    {
        try { ConversationArchive.ArchiveRemoved(before, messages, paths.Archive); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Console.WriteLine($"  Archive not updated: {ex.Message}"); }
    }
}
