using System.Text;
using System.Text.Json.Nodes;

namespace CsAgent.Core.Agent;

public static partial class ToolDispatcher
{
    private static string WriteFile(string path, string content)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!IsSafePath(full))
                return $"Error: write_file - Path '{full}' is not allowed for writing. Only files in the current working directory are permitted.";

            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(full, content, new UTF8Encoding(false));
            return $"OK: wrote {new FileInfo(full).Length} bytes to '{full}'";
        }
        catch (Exception ex) { return $"Error: write_file — {ex.Message}"; }
    }
    private static string ReadFile(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!IsSafePath(full))
                return $"Error: read_file - Path '{full}' is not allowed for reading. Only files in the current working directory are permitted.";

            if (!File.Exists(full)) return $"Error: not found '{full}'";
            var len = new FileInfo(full).Length;
            if (len > 512_000) return $"Error: file too large ({len / 1024} KB). Use sh to grep/head.";
            return File.ReadAllText(full, Encoding.UTF8);
        }
        catch (Exception ex) { return $"Error: read_file — {ex.Message}"; }
    }
    private static string ListDir(string path, bool recursive)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!IsSafePath(full))
                return $"Error: list_dir - Path '{full}' is not allowed for listing. Only directories in the current working directory are permitted.";

            if (!Directory.Exists(full)) return $"Error: directory not found '{full}'";

            var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var sb = new StringBuilder();

            foreach (var d in Directory.EnumerateDirectories(full, "*", opt))
            {
                var dirName = Path.GetFileName(d);
                if (dirName.StartsWith(".")) continue;
                sb.AppendLine($"[DIR]  {Path.GetRelativePath(full, d)}/");
            }

            foreach (var f in Directory.EnumerateFiles(full, "*", opt))
            {
                var relPath = Path.GetRelativePath(full, f);
                if (relPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p.StartsWith(".")))
                    continue;
                sb.AppendLine($"[FILE] {relPath}  ({Sz(new FileInfo(f).Length)})");
            }

            return sb.Length == 0 ? "(empty)" : sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return $"Error: list_dir — {ex.Message}"; }
    }
    private static string Tree(string path, int depth)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!IsSafePath(full))
                return $"Error: tree - Path '{full}' is not allowed for listing. Only directories in the current working directory are permitted.";

            if (!Directory.Exists(full)) return $"Error: directory not found '{full}'";

            var sb = new StringBuilder();
            var count = 0;

            // Root line: show the directory name (or '.' for the current dir).
            var rootName = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(rootName)) rootName = full;
            sb.AppendLine(rootName + "/");

            AppendTreeLevel(full, "", depth, sb, ref count);

            if (count >= MaxTreeEntries)
                sb.AppendLine($"... (truncated at {MaxTreeEntries} entries)");

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return $"Error: tree — {ex.Message}"; }
    }
    /// <summary>
    /// Recursively appends the contents of a directory to the tree output using
    /// box-drawing characters. 'prefix' carries the indentation for ancestors.
    /// </summary>
    private static void AppendTreeLevel(
        string dir,
        string prefix,
        int depth,
        StringBuilder sb,
        ref int count)
    {
        if (count >= MaxTreeEntries) return;

        // Gather and sort entries: directories first, then files, each alphabetically.
        var dirs = new List<string>();
        var files = new List<string>();

        foreach (var d in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(d);
            if (name.StartsWith(".")) continue;
            if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("obj", StringComparison.OrdinalIgnoreCase)) continue;
            dirs.Add(name);
        }

        foreach (var f in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(f);
            if (name.StartsWith(".")) continue;
            files.Add(name);
        }

        dirs.Sort(StringComparer.OrdinalIgnoreCase);
        files.Sort(StringComparer.OrdinalIgnoreCase);

        var entries = dirs.Select(d => (Name: d, IsDir: true))
                          .Concat(files.Select(f => (Name: f, IsDir: false)))
                          .ToList();

        for (int i = 0; i < entries.Count; i++)
        {
            if (count >= MaxTreeEntries) return;

            var (name, isDir) = entries[i];
            var isLast = i == entries.Count - 1;

            // Branch glyph: '└── ' for the last child, '├── ' otherwise.
            var branch = isLast ? "└── " : "├── ";
            sb.AppendLine(prefix + branch + name + (isDir ? "/" : ""));
            count++;

            if (isDir && (depth < 0 || depth > 0))
            {
                // Child prefix: '    ' for last child, '│   ' otherwise.
                var childPrefix = prefix + (isLast ? "    " : "│   ");
                var childDepth = depth < 0 ? -1 : depth - 1;
                AppendTreeLevel(Path.Combine(dir, name), childPrefix, childDepth, sb, ref count);
            }
        }
    }
    private static string SearchFiles(string pattern, string path, string glob)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return "Error: search_files - 'pattern' argument is required.";

            var full = Path.GetFullPath(path);
            if (!IsSafePath(full))
                return $"Error: search_files - Path '{full}' is not allowed for searching. Only directories in the current working directory are permitted.";

            if (!Directory.Exists(full)) return $"Error: directory not found '{full}'";

            var sb = new StringBuilder();
            var count = 0;
            var needle = pattern;

            foreach (var file in Directory.EnumerateFiles(full, glob, SearchOption.AllDirectories))
            {
                var relPath = Path.GetRelativePath(full, file);
                var parts = relPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                // Skip hidden directories (starting with '.')
                if (parts.Any(p => p.StartsWith(".")))
                    continue;

                // Skip build output directories (bin/ and obj/)
                if (parts.Any(p => p.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                                   p.Equals("obj", StringComparison.OrdinalIgnoreCase)))
                    continue;

                var fi = new FileInfo(file);
                if (fi.Length > MaxSearchFileBytes) continue;

                // Skip binary files (detect null bytes in the first chunk)
                if (IsBinaryFile(file)) continue;

                string[] lines;
                try { lines = File.ReadAllLines(file, Encoding.UTF8); }
                catch { continue; }

                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains(needle, StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"{relPath}:{i + 1}: {lines[i].Trim()}");
                        if (++count >= MaxSearchResults)
                        {
                            sb.AppendLine($"... (truncated at {MaxSearchResults} matches)");
                            return sb.ToString().TrimEnd();
                        }
                    }
                }
            }

            return count == 0
                ? $"No matches for '{pattern}' under '{full}'."
                : sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return $"Error: search_files — {ex.Message}"; }
    }
    private static string EditFile(string path, JsonNode? editsNode)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!IsSafePath(full))
                return $"Error: edit_file - Path '{full}' is not allowed for editing. Only files in the current working directory are permitted.";

            if (!File.Exists(full)) return $"Error: not found '{full}'";

            if (editsNode is not JsonArray edits || edits.Count == 0)
                return "Error: edit_file - 'edits' must be a non-empty array of {old_string, new_string} objects.";

            var original = File.ReadAllText(full, Encoding.UTF8);
            var working = original;
            var applied = new List<string>();

            foreach (var edit in edits)
            {
                if (edit is not JsonObject obj ||
                    obj["old_string"] is not JsonValue oldVal ||
                    obj["new_string"] is not JsonValue newVal)
                    return "Error: edit_file - each edit must be an object with 'old_string' and 'new_string' string fields.";

                var oldStr = oldVal.GetValue<string>();
                var newStr = newVal.GetValue<string>();

                if (string.IsNullOrEmpty(oldStr))
                    return "Error: edit_file - 'old_string' cannot be empty.";

                // Count occurrences in the current working text.
                int idx = working.IndexOf(oldStr, StringComparison.Ordinal);
                if (idx < 0)
                    return $"Error: edit_file - 'old_string' not found in '{full}':\n{oldStr}";

                if (working.IndexOf(oldStr, idx + oldStr.Length, StringComparison.Ordinal) >= 0)
                    return $"Error: edit_file - 'old_string' appears more than once in '{full}'. Provide more context to make it unique:\n{oldStr}";

                working = working.Remove(idx, oldStr.Length).Insert(idx, newStr);
                applied.Add(oldStr);
            }

            // All edits validated — write atomically.
            File.WriteAllText(full, working, new UTF8Encoding(false));
            return $"OK: applied {applied.Count} edit(s) to '{full}'.";
        }
        catch (Exception ex) { return $"Error: edit_file — {ex.Message}"; }
    }
    private static string CopyFile(string source, string destination)
    {
        try
        {
            var srcFull = Path.GetFullPath(source);
            var dstFull = Path.GetFullPath(destination);
            if (!IsSafePath(srcFull) || !IsSafePath(dstFull))
                return $"Error: copy_file - Paths must be inside the current working directory.";

            if (!File.Exists(srcFull)) return $"Error: copy_file - source not found '{srcFull}'";

            var dir = Path.GetDirectoryName(dstFull);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            File.Copy(srcFull, dstFull, overwrite: true);
            return $"OK: copied '{srcFull}' -> '{dstFull}' ({Sz(new FileInfo(dstFull).Length)})";
        }
        catch (Exception ex) { return $"Error: copy_file — {ex.Message}"; }
    }
    private static string MoveFile(string source, string destination)
    {
        try
        {
            var srcFull = Path.GetFullPath(source);
            var dstFull = Path.GetFullPath(destination);
            if (!IsSafePath(srcFull) || !IsSafePath(dstFull))
                return $"Error: move_file - Paths must be inside the current working directory.";

            if (!File.Exists(srcFull)) return $"Error: move_file - source not found '{srcFull}'";

            var dir = Path.GetDirectoryName(dstFull);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            File.Move(srcFull, dstFull, overwrite: true);
            return $"OK: moved '{srcFull}' -> '{dstFull}'";
        }
        catch (Exception ex) { return $"Error: move_file — {ex.Message}"; }
    }
    private static string DeleteFile(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!IsSafePath(full))
                return $"Error: delete_file - Path '{full}' is not allowed for deletion. Only files in the current working directory are permitted.";

            if (!File.Exists(full)) return $"Error: delete_file - not found '{full}'";

            File.Delete(full);
            return $"OK: deleted '{full}'";
        }
        catch (Exception ex) { return $"Error: delete_file — {ex.Message}"; }
    }
}
