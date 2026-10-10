using System.Text;

namespace CsAgent.Core.Llm;

/// <summary>
/// Text helpers shared by the model router and the routing rules: tokenizing, accent folding, links.
/// AOT-safe: no Regex, no ICU (works in invariant-globalization mode).
/// </summary>
internal static class RoutingText
{
    /// <summary>Lower-case words made of letters, digits and "_" or "-", without accents.</summary>
    public static IEnumerable<string> Tokens(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')
            {
                sb.Append(FoldChar(char.ToLowerInvariant(ch)));
            }
            else if (sb.Length > 0)
            {
                yield return sb.ToString().Trim('-');
                sb.Clear();
            }
        }
        if (sb.Length > 0) yield return sb.ToString().Trim('-');
    }

    public static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(FoldChar(char.ToLowerInvariant(ch)));
        return sb.ToString();
    }

    /// <summary>Removes the accents of the French letters.</summary>
    public static char FoldChar(char c) => c switch
    {
        'é' or 'è' or 'ê' or 'ë' => 'e',
        'à' or 'â' or 'ä' => 'a',
        'î' or 'ï' => 'i',
        'ô' or 'ö' => 'o',
        'ù' or 'û' or 'ü' => 'u',
        'ç' => 'c',
        _ => c,
    };

    /// <summary>The text without its links, so that "https" and the host name do not look like code words.</summary>
    public static string WithoutLinks(string text)
    {
        if (!text.Contains("://")) return text;
        var kept = new List<string>();
        foreach (var chunk in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (!chunk.Contains("://")) kept.Add(chunk);
        return string.Join(' ', kept);
    }

    /// <summary>The words of a message, links removed (what the routing rules look at).</summary>
    public static List<string> MessageTokens(string text) => new(Tokens(WithoutLinks(text)));
}

/// <summary>
/// One word or phrase of a routing rule: consecutive words, compared without case or accents.
/// A trailing "*" makes the last word a prefix ("recherch*" matches "recherche" and "recherches").
/// </summary>
internal readonly record struct Phrase(string[] Words, bool PrefixLast)
{
    /// <summary>Parses "sur le web", "Météo", "recherch*". Null when the text has no word.</summary>
    public static Phrase? Parse(string text)
    {
        var s = text.Trim();
        var prefix = s.EndsWith('*');
        var words = new List<string>(RoutingText.Tokens(s));
        return words.Count == 0 ? null : new Phrase(words.ToArray(), prefix);
    }

    /// <summary>The phrase starts at token <paramref name="at"/>.</summary>
    public bool MatchesAt(List<string> tokens, int at)
    {
        if (at < 0 || at + Words.Length > tokens.Count) return false;
        for (var k = 0; k < Words.Length; k++)
        {
            var last = PrefixLast && k == Words.Length - 1;
            var t = tokens[at + k];
            if (last ? !t.StartsWith(Words[k], StringComparison.Ordinal) : t != Words[k]) return false;
        }
        return true;
    }

    public bool IsIn(List<string> tokens)
    {
        for (var i = 0; i + Words.Length <= tokens.Count; i++)
            if (MatchesAt(tokens, i)) return true;
        return false;
    }

    public override string ToString() => string.Join(' ', Words) + (PrefixLast ? "*" : "");
}
