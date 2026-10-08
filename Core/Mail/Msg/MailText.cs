using System.Net;
using System.Text;

namespace CsAgent.Core.Mail.Msg;

/// <summary>
/// Minimal, dependency-free HTML and RTF to plain-text converters, used to give the
/// agent a readable body when a message has no plain-text part. Best effort: the goal
/// is readable text, not a faithful rendering. Every loop advances, so hostile input
/// cannot hang the converter.
/// </summary>
public static class MailText
{
    static MailText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    // ───────────────────────────── HTML ─────────────────────────────

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "br", "tr", "table", "ul", "ol", "li", "h1", "h2", "h3", "h4", "h5", "h6",
        "blockquote", "pre", "hr", "section", "article", "header", "footer"
    };

    public static string FromHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";

        var sb = new StringBuilder(html.Length / 2);
        int i = 0;
        while (i < html.Length)
        {
            char c = html[i];
            if (c != '<')
            {
                sb.Append(c is '\r' or '\n' or '\t' ? ' ' : c);
                i++;
                continue;
            }

            if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
            {
                int ce = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = ce < 0 ? html.Length : ce + 3;
                continue;
            }

            int end = html.IndexOf('>', i + 1);
            if (end < 0) break;   // unterminated tag: drop the rest

            int p = i + 1;
            bool closing = p < end && html[p] == '/';
            if (closing) p++;
            int nameStart = p;
            while (p < end && (char.IsLetterOrDigit(html[p]))) p++;
            string name = html.Substring(nameStart, p - nameStart).ToLowerInvariant();

            i = end + 1;

            if (!closing && name is "script" or "style" or "head")
            {
                int close = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                if (close < 0) { i = html.Length; break; }
                int cend = html.IndexOf('>', close);
                i = cend < 0 ? html.Length : cend + 1;
                continue;
            }

            if (name == "li" && !closing) sb.Append("\n- ");
            else if (name is "td" or "th") { if (closing) sb.Append(' '); }
            else if (BlockTags.Contains(name)) sb.Append('\n');
        }

        string text = WebUtility.HtmlDecode(sb.ToString()).Replace(' ', ' ');
        return Tidy(text);
    }

    // ───────────────────────────── RTF ─────────────────────────────

    private static readonly HashSet<string> SkippedDestinations = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "header", "footer", "headerl", "headerr",
        "footerl", "footerr", "object", "themedata", "datastore", "latentstyles", "listtable",
        "listoverridetable", "generator", "xmlnstbl", "fldinst", "filetbl", "revtbl", "rsidtbl",
        "colorschememapping", "mmathPr", "wgrffmtfilter"
    };

    public static string FromRtf(string rtf)
    {
        if (string.IsNullOrEmpty(rtf)) return "";

        var sb = new StringBuilder(rtf.Length / 3);
        var skipStack = new Stack<bool>();
        bool skip = false;
        bool htmlrtf = false;       // text between \htmlrtf and \htmlrtf0 is RTF-only duplicate of HTML
        int uc = 1;                 // \ucN: fallback characters that follow \uN
        int pendingSkip = 0;
        var cp1252 = Encoding.GetEncoding(1252);

        int i = 0;
        while (i < rtf.Length)
        {
            char c = rtf[i];

            if (c == '{')
            {
                skipStack.Push(skip);
                i++;
                // {\* ...} is an ignorable destination.
                if (i + 1 < rtf.Length && rtf[i] == '\\' && rtf[i + 1] == '*') skip = true;
                continue;
            }
            if (c == '}')
            {
                skip = skipStack.Count > 0 ? skipStack.Pop() : false;
                i++;
                continue;
            }
            if (c is '\r' or '\n') { i++; continue; }

            if (c != '\\')
            {
                if (pendingSkip > 0) { pendingSkip--; i++; continue; }
                if (!skip && !htmlrtf) sb.Append(c);
                i++;
                continue;
            }

            // Control sequence.
            i++;
            if (i >= rtf.Length) break;
            char n = rtf[i];

            if (n is '\\' or '{' or '}')
            {
                if (pendingSkip > 0) pendingSkip--;
                else if (!skip && !htmlrtf) sb.Append(n);
                i++;
                continue;
            }
            if (n == '\'')
            {
                if (i + 2 < rtf.Length + 0 && Uri.IsHexDigit(rtf[i + 1]) && Uri.IsHexDigit(rtf[i + 2]))
                {
                    if (pendingSkip > 0) pendingSkip--;
                    else if (!skip && !htmlrtf)
                        sb.Append(cp1252.GetString(new[] { Convert.ToByte(rtf.Substring(i + 1, 2), 16) }));
                    i += 3;
                }
                else i++;
                continue;
            }
            if (n == '*') { skip = true; i++; continue; }
            if (!char.IsAsciiLetter(n)) { i++; continue; }   // \~ \- \_ etc.

            int ws = i;
            while (i < rtf.Length && char.IsAsciiLetter(rtf[i])) i++;
            string word = rtf.Substring(ws, i - ws);

            int? param = null;
            int ps = i;
            if (i < rtf.Length && (rtf[i] == '-' || char.IsAsciiDigit(rtf[i])))
            {
                i++;
                while (i < rtf.Length && char.IsAsciiDigit(rtf[i])) i++;
                if (int.TryParse(rtf.AsSpan(ps, i - ps), out var pv)) param = pv;
            }
            if (i < rtf.Length && rtf[i] == ' ') i++;   // delimiter space

            if (SkippedDestinations.Contains(word)) { skip = true; continue; }

            switch (word)
            {
                case "htmlrtf": htmlrtf = param is null or not 0; break;
                case "uc": uc = param ?? 1; break;
                case "u":
                    if (param is int u && !skip && !htmlrtf)
                        sb.Append((char)(u < 0 ? u + 65536 : u));
                    pendingSkip = uc;
                    break;
                case "par": case "line": case "sect": case "page":
                    if (!skip && !htmlrtf) sb.Append('\n'); break;
                case "tab": if (!skip && !htmlrtf) sb.Append('\t'); break;
                case "emdash": if (!skip && !htmlrtf) sb.Append('—'); break;
                case "endash": if (!skip && !htmlrtf) sb.Append('–'); break;
                case "bullet": if (!skip && !htmlrtf) sb.Append('•'); break;
                case "lquote": case "rquote": if (!skip && !htmlrtf) sb.Append('\''); break;
                case "ldblquote": case "rdblquote": if (!skip && !htmlrtf) sb.Append('"'); break;
            }
        }

        return Tidy(sb.ToString());
    }

    // ───────────────────────────── shared ─────────────────────────────

    /// <summary>Trims lines, collapses runs of spaces and limits blank lines to one.</summary>
    private static string Tidy(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder(text.Length);
        int blank = 0;
        foreach (var raw in lines)
        {
            var sbLine = new StringBuilder(raw.Length);
            bool lastSpace = false;
            foreach (char ch in raw)
            {
                bool space = ch is ' ' or '\t';
                if (space) { if (!lastSpace) sbLine.Append(' '); lastSpace = true; }
                else { sbLine.Append(ch); lastSpace = false; }
            }
            var line = sbLine.ToString().Trim();
            if (line.Length == 0)
            {
                if (++blank > 1 || sb.Length == 0) continue;
            }
            else blank = 0;
            sb.Append(line).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }
}
