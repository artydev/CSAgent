using CsAgent.Core.Agent;
using CsAgent.Core.Llm;
using CsAgent.Core.Memory;
using CsAgent.Shared;
using System.Text.Json.Nodes;

namespace CsAgent.Presentation.Tui;

/// <summary>
/// aichat-like session commands for the interactive prompt:
/// <c>.session</c> (info), <c>.compress</c>, <c>.empty</c>, <c>.list</c>.
/// A session is the memory folder chosen with <c>--mem</c> / <c>--session</c>.
/// </summary>
public static class SessionCommands
{
    /// <summary>True when the input was a session command (handled, whatever the outcome).</summary>
    public static async Task<bool> TryHandleAsync(
        string input, JsonArray messages, AgentArguments args, string apiKey)
    {
        var trimmed = input.Trim();
        var cmd = trimmed.ToLowerInvariant();
        if (!cmd.StartsWith('.')) return false;

        var paths = MemoryPaths.Resolve(args.MemoryFile);

        // ".save" (flush now) or ".save <name>" (copy this session to a new named session).
        if (cmd == ".save" || cmd.StartsWith(".save "))
        {
            await MemoryStore.SaveAsync(paths.Conversation, messages);
            var name = trimmed.Length > 5 ? trimmed[5..].Trim() : "";
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
                foreach (var f in new[] { paths.Conversation, paths.Exact, paths.Semantic, paths.Summary })
                    if (File.Exists(f)) File.Copy(f, Path.Combine(name, Path.GetFileName(f)));
                Console.WriteLine($"  Saved as '{name}'. Resume it later with: csagent --session {name}");
            }
            catch (Exception ex) { Console.WriteLine($"  Save failed: {ex.Message}"); }
            return true;
        }

        // ".delete <name>": removes ONLY the four memory files of that session (then the folder if empty).
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
                foreach (var f in new[] { MemoryPaths.ConversationFile, MemoryPaths.ExactFile, MemoryPaths.SemanticFile, MemoryPaths.SummaryFile })
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
                Console.WriteLine("  .delete <name>    delete another session");
                return true;

            case ".session":
            case ".info":
                Console.WriteLine($"  Session   : {paths.Directory}");
                Console.WriteLine($"  Messages  : {messages.Count}");
                Console.WriteLine($"  Size      : {SessionCompressor.SizeChars(messages):N0} chars (~{SessionCompressor.EstimateTokens(messages):N0} tokens)");
                Console.WriteLine($"  Compress  : {(ContextBudget.CompressEnabled ? $"at {ContextBudget.CompressChars:N0} chars" : "off")}; hard limit {ContextBudget.MaxChars:N0}");
                Console.WriteLine($"  Summarised: {(messages.Count > 1 && SessionCompressor.IsSummary(messages[1]) ? "yes" : "no")}");
                return true;

            case ".compress":
                try
                {
                    using var client = new LlmClient(apiKey, LlmConfig.Endpoint,
                        LlmConfig.ChatModel ?? LlmConfig.CodeModel, new RetryPolicy(args.MaxRetries, args.RetryDelayMs));
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                    var done = await SessionCompressor.CompressAsync(messages, client, ContextBudget.KeepRecentMessages, cts.Token);
                    if (done) await MemoryStore.SaveAsync(paths.Conversation, messages);
                    Console.WriteLine(done ? "  Session compressed." : "  Nothing to compress yet.");
                }
                catch (Exception ex) { Console.WriteLine($"  Compression failed: {ex.Message}"); }
                return true;

            case ".empty":
                SessionCompressor.Empty(messages);
                await MemoryStore.SaveAsync(paths.Conversation, messages);
                Console.WriteLine("  Session emptied (system prompt kept).");
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
}
