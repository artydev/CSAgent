using System.Text;
using CsAgent.Core.Mail.Msg;

namespace CsAgent.Core.Agent;

public static partial class ToolDispatcher
{
    private const int MaxGroupFiles = 5000;
    private const int MaxGroupReportLines = 200;

    /// <summary>
    /// group_msg_by_sender: renames the .msg files of a folder to plain ASCII names (no accents, spaces or
    /// special characters), then moves each one into a sub-folder named after its sender. Works on every OS,
    /// needs no Outlook. Only the .msg files directly in the folder are handled; nothing is ever overwritten.
    /// Destructive: requires confirmation.
    /// </summary>
    private static string GroupMsgBySender(string? directory, string? groupBy, bool rename)
    {
        try
        {
            var dir = Path.GetFullPath(string.IsNullOrWhiteSpace(directory) ? "." : directory);
            if (!IsSafePath(dir))
                return $"Error: group_msg_by_sender - Directory '{dir}' is not allowed. Only the current working directory and its sub-folders are permitted.";
            if (!Directory.Exists(dir))
                return $"Error: group_msg_by_sender - folder not found '{dir}'.";

            bool byEmail = string.Equals(groupBy, "email", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(groupBy) && !byEmail && !string.Equals(groupBy, "name", StringComparison.OrdinalIgnoreCase))
                return "Error: group_msg_by_sender - group_by must be 'name' or 'email'.";

            var files = Directory.GetFiles(dir, "*.msg", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0) return $"No .msg file in '{dir}'.";
            if (files.Count > MaxGroupFiles)
                return $"Error: group_msg_by_sender - {files.Count} .msg files in '{dir}', the limit is {MaxGroupFiles} per call.";

            var report = new List<string>();
            var perSender = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var folderOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // sender key -> first spelling
            int renamed = 0, moved = 0, errors = 0;

            foreach (var original in files)
            {
                var current = original;
                var originalName = Path.GetFileName(original);
                try
                {
                    // 1. Plain ASCII file name, so every tool can address the file without quoting problems.
                    if (rename)
                    {
                        var safe = SafeAsciiName(Path.GetFileNameWithoutExtension(originalName), "message", 120) + ".msg";
                        if (!string.Equals(safe, originalName, StringComparison.Ordinal))
                        {
                            var target = Path.Combine(dir, safe);
                            // A different file already has that name: add (1), (2)... A case-only change is the same file.
                            if (File.Exists(target) && !string.Equals(target, original, StringComparison.OrdinalIgnoreCase))
                                target = UniquePath(dir, safe);
                            if (!string.Equals(target, original, StringComparison.Ordinal))
                            {
                                File.Move(original, target);
                                current = target;
                                renamed++;
                                report.Add($"Renamed: {originalName} -> {Path.GetFileName(target)}");
                            }
                        }
                    }

                    // 2. The sender, read from the message itself.
                    var msg = MsgReader.Read(current, new MsgReaderOptions { ReadAttachmentData = false });
                    var sender = byEmail
                        ? FirstNonBlank(msg.SmtpSender, msg.SenderEmail, msg.SenderName)
                        : FirstNonBlank(msg.SenderName, msg.SmtpSender, msg.SenderEmail);
                    var folderName = SafeAsciiName(sender, "UnknownSender", 100);
                    if (!folderOf.TryGetValue(folderName, out var first)) folderOf[folderName] = first = folderName;

                    // 3. Move it into the sender's folder.
                    var destDir = Path.Combine(dir, first);
                    Directory.CreateDirectory(destDir);
                    var dest = UniquePath(destDir, Path.GetFileName(current));
                    File.Move(current, dest);
                    moved++;
                    perSender[first] = perSender.GetValueOrDefault(first) + 1;
                    report.Add($"Moved: {Path.GetFileName(current)} -> {first}/");
                }
                catch (InvalidDataException ex)
                {
                    errors++;
                    report.Add($"Error: {Path.GetFileName(current)} is not a readable .msg file ({ex.Message}); left in place.");
                }
                catch (Exception ex)
                {
                    errors++;
                    report.Add($"Error: {Path.GetFileName(current)}: {ex.Message}");
                }
            }

            var sb = new StringBuilder();
            foreach (var line in report.Take(MaxGroupReportLines)) sb.AppendLine(line);
            if (report.Count > MaxGroupReportLines) sb.AppendLine($"... {report.Count - MaxGroupReportLines} more line(s) not shown.");
            sb.AppendLine($"Done: {files.Count} file(s), {renamed} renamed, {moved} moved into {perSender.Count} sender folder(s), {errors} error(s).");
            foreach (var kv in perSender.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Take(50))
                sb.AppendLine($"  {kv.Key}: {kv.Value}");
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return $"Error: group_msg_by_sender — {ex.Message}"; }
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var v in values) if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return null;
    }

    // The program runs with InvariantGlobalization (NativeAOT), where string.Normalize cannot split accents,
    // so letters are folded with an explicit table: Latin-1 and Latin Extended-A, which covers the Western and
    // Central European languages. Other scripts become '_'.
    private static readonly Dictionary<char, string> AsciiFold = BuildAsciiFold();

    private static Dictionary<char, string> BuildAsciiFold()
    {
        var d = new Dictionary<char, string>();
        void Add(string letters, string ascii) { foreach (var c in letters) d[c] = ascii; }
        Add("ÀÁÂÃÄÅĀĂĄ", "A"); Add("àáâãäåāăą", "a");
        Add("ÇĆĈĊČ", "C"); Add("çćĉċč", "c");
        Add("ĎĐÐ", "D"); Add("ďđð", "d");
        Add("ÈÉÊËĒĔĖĘĚ", "E"); Add("èéêëēĕėęě", "e");
        Add("ĜĞĠĢ", "G"); Add("ĝğġģ", "g");
        Add("ĤĦ", "H"); Add("ĥħ", "h");
        Add("ÌÍÎÏĨĪĬĮİ", "I"); Add("ìíîïĩīĭįı", "i");
        Add("Ĵ", "J"); Add("ĵ", "j");
        Add("Ķ", "K"); Add("ķ", "k");
        Add("ĹĻĽĿŁ", "L"); Add("ĺļľŀł", "l");
        Add("ÑŃŅŇ", "N"); Add("ñńņň", "n");
        Add("ÒÓÔÕÖØŌŎŐ", "O"); Add("òóôõöøōŏő", "o");
        Add("ŔŖŘ", "R"); Add("ŕŗř", "r");
        Add("ŚŜŞŠ", "S"); Add("śŝşš", "s");
        Add("ŢŤŦ", "T"); Add("ţťŧ", "t");
        Add("ÙÚÛÜŨŪŬŮŰŲ", "U"); Add("ùúûüũūŭůűų", "u");
        Add("Ŵ", "W"); Add("ŵ", "w");
        Add("ÝŶŸ", "Y"); Add("ýÿŷ", "y");
        Add("ŹŻŽ", "Z"); Add("źżž", "z");
        d['ß'] = "ss"; d['Æ'] = "AE"; d['æ'] = "ae"; d['Œ'] = "OE"; d['œ'] = "oe"; d['Þ'] = "Th"; d['þ'] = "th";
        return d;
    }

    /// <summary>
    /// A name made only of letters, digits, '.', '-' and '_': accents are removed (é → e, ß → ss), every other
    /// character (spaces, punctuation, symbols, other scripts) becomes '_', repeated '_' are merged. Valid on every OS.
    /// </summary>
    internal static string SafeAsciiName(string? raw, string fallback, int maxLength)
    {
        var text = (raw ?? "").Trim();
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c >= '\u0300' && c <= '\u036F') continue;   // a separate accent mark (decomposed é = e + ´)
            if (AsciiFold.TryGetValue(c, out var folded)) { sb.Append(folded); continue; }
            bool ok = c < 128 && (char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
            char o = ok ? c : '_';
            if (o == '_' && sb.Length > 0 && sb[^1] == '_') continue;
            sb.Append(o);
        }
        var name = sb.ToString().Trim('_', '.', '-', ' ');
        if (name.Length > maxLength) name = name[..maxLength].TrimEnd('_', '.', '-', ' ');
        if (name.Length == 0) name = fallback;
        if (ReservedWindowsNames.Contains(Path.GetFileNameWithoutExtension(name))) name = "_" + name;
        return name;
    }
}
