using System.Text;
using CsAgent.Core.Mail.Msg;

namespace CsAgent.Core.Agent;

public static partial class ToolDispatcher
{
    private const long MaxMsgFileBytes = 100L * 1024 * 1024;

    /// <summary>read_msg: headers, body and attachment list of an Outlook .msg file. Read-only, works on every OS.</summary>
    private static string ReadMsg(string path, int? maxChars)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!IsSafePath(full))
                return $"Error: read_msg - Path '{full}' is not allowed for reading. Only files in the current working directory are permitted.";
            var err = CheckMsgFile(full);
            if (err is not null) return $"Error: read_msg - {err}";

            var msg = MsgReader.Read(full, new MsgReaderOptions { ReadAttachmentData = false });
            return MsgFormatter.Format(msg, full, maxChars ?? MsgFormatter.DefaultMaxChars);
        }
        catch (InvalidDataException ex) { return $"Error: read_msg — not a readable .msg file: {ex.Message}"; }
        catch (Exception ex) { return $"Error: read_msg — {ex.Message}"; }
    }

    /// <summary>
    /// save_attachment: writes the attachments of a .msg file to disk (destructive: requires confirmation).
    /// Numbers are those shown by read_msg. File names are sanitised and never overwrite an existing file.
    /// </summary>
    private static string SaveAttachment(string path, int? index, string? destination)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!IsSafePath(full))
                return $"Error: save_attachment - Path '{full}' is not allowed. Only files in the current working directory are permitted.";
            var err = CheckMsgFile(full);
            if (err is not null) return $"Error: save_attachment - {err}";

            var dir = Path.GetFullPath(string.IsNullOrWhiteSpace(destination)
                ? Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileNameWithoutExtension(full) + "_attachments")
                : destination);
            if (!IsSafePath(dir))
                return $"Error: save_attachment - Destination '{dir}' must be inside the current working directory.";

            var msg = MsgReader.Read(full, new MsgReaderOptions { ReadAttachmentData = true });
            if (msg.Attachments.Count == 0) return "This message has no attachments.";
            if (index is int idx && (idx < 1 || idx > msg.Attachments.Count))
                return $"Error: save_attachment - index {idx} is out of range (1..{msg.Attachments.Count}).";

            var sb = new StringBuilder();
            int saved = 0;
            for (int i = 0; i < msg.Attachments.Count; i++)
            {
                if (index is int want && want != i + 1) continue;
                var a = msg.Attachments[i];
                string label = $"#{i + 1} {a.FileName ?? a.DisplayName ?? "(unnamed)"}";

                if (a.Method == 5) { sb.AppendLine($"Skipped {label}: embedded message (its text is shown by read_msg)."); continue; }
                if (a.Method != 1) { sb.AppendLine($"Skipped {label}: stored by reference or custom storage."); continue; }
                if (a.Data.Length == 0) { sb.AppendLine($"Skipped {label}: no data."); continue; }

                Directory.CreateDirectory(dir);
                var target = UniquePath(dir, SafeAttachmentName(a.FileName ?? a.DisplayName, i + 1));
                File.WriteAllBytes(target, a.Data);
                saved++;
                sb.AppendLine($"Saved {label} -> '{target}' ({MsgFormatter.Size(a.Data.LongLength)})");
            }
            sb.Append(saved == 0 ? "Nothing was saved." : $"OK: {saved} attachment(s) saved in '{dir}'.");
            return sb.ToString();
        }
        catch (InvalidDataException ex) { return $"Error: save_attachment — not a readable .msg file: {ex.Message}"; }
        catch (Exception ex) { return $"Error: save_attachment — {ex.Message}"; }
    }

    private static string? CheckMsgFile(string full)
    {
        if (!File.Exists(full)) return $"not found '{full}'";
        var len = new FileInfo(full).Length;
        if (len > MaxMsgFileBytes) return $"file too large ({len / (1024 * 1024)} MB, limit {MaxMsgFileBytes / (1024 * 1024)} MB).";
        return null;
    }

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>A file name that cannot escape the destination folder and is valid on every OS.</summary>
    internal static string SafeAttachmentName(string? raw, int index)
    {
        // Take the last segment whatever the separator used by the sender, then drop invalid characters.
        var name = (raw ?? "").Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(c < 32 || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' || Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        name = sb.ToString().Trim().TrimEnd('.', ' ');
        if (name.Length > 150) name = name[..150];
        if (name.Length == 0 || name.All(c => c == '.' || c == '_')) name = $"attachment_{index}";
        if (ReservedWindowsNames.Contains(Path.GetFileNameWithoutExtension(name))) name = "_" + name;
        return name;
    }

    private static string UniquePath(string dir, string fileName)
    {
        var target = Path.Combine(dir, fileName);
        if (!File.Exists(target)) return target;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int n = 1; ; n++)
        {
            target = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(target)) return target;
        }
    }
}
