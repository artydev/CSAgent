using System.Text;

namespace CsAgent.Services;

/// <summary>
/// Turns free text (assistant thoughts, tool errors, pattern summaries) into a set of
/// comparable keywords. English and French. No dependencies, no ICU, AOT-safe.
/// </summary>
public static class TextTokenizer
{
    private const int MinTokenLength = 3;

    // ── Stop words (all lower-case, accent-folded: compare AFTER Fold) ───────────

    private static readonly string[] English =
    {
        "the","and","for","with","that","this","these","those","from","into","then","than",
        "let","try","trying","will","would","should","could","can","are","was","were","been",
        "you","your","our","not","but","next","now","again","sure","just","also","use","using",
        "need","needs","want","going","there","here","what","when","which","while"
    };

    private static readonly string[] French =
    {
        "les","des","une","aux","est","sont","suis","etes","etait","etaient","ete","etre",
        "avec","sans","pour","par","dans","sur","sous","vers","chez","entre","comme",
        "que","qui","quoi","dont","mais","donc","car","ainsi","alors","puis","ensuite",
        "je","tu","il","elle","nous","vous","ils","elles","ce","cet","cette","ces",
        "mon","mes","ton","tes","son","ses","notre","votre","leur","leurs","lui",
        "vais","vas","va","vont","allons","ai","avons","avez","ont","avait","fait","faire",
        "essayer","essaie","essaye","essayons","voici","voila","maintenant","encore","aussi",
        "tres","plus","moins","pas","non","oui","bien","juste","peut","peux","veux","dois",
        "faut","besoin","utiliser","utilise","verifier","verifie","toujours","jamais"
    };

    // Words that appear in almost every auto-generated pattern, so they carry no signal.
    private static readonly string[] Generic =
    {
        "error","errors","succeeded","solution","file",
        "fichier","fichiers","erreur","erreurs","succes","reussi","reussie"
    };

    private static readonly HashSet<string> StopWords =
        new(English.Concat(French).Concat(Generic), StringComparer.Ordinal);

    // ── Public API ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Lower-cases and removes French diacritics (é→e, ç→c, œ→oe …), so "Accès refusé"
    /// and "acces refuse" are the same text. A manual map is used on purpose:
    /// string.Normalize() depends on ICU, which InvariantGlobalization turns off.
    /// </summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var sb = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            switch (c)
            {
                case 'à': case 'â': case 'ä': sb.Append('a'); break;
                case 'é': case 'è': case 'ê': case 'ë': sb.Append('e'); break;
                case 'î': case 'ï': sb.Append('i'); break;
                case 'ô': case 'ö': sb.Append('o'); break;
                case 'ù': case 'û': case 'ü': sb.Append('u'); break;
                case 'ÿ': sb.Append('y'); break;
                case 'ç': sb.Append('c'); break;
                case 'œ': sb.Append("oe"); break;
                case 'æ': sb.Append("ae"); break;
                case '’': case '‘': sb.Append('\''); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Extracts keywords:
    ///  • split on anything except letters, digits and  . _ -   (so '/' and spaces separate;
    ///    "/opt/data.json" gives "opt" and "data.json");
    ///  • a token containing . _ - is also split into its parts ("not-found" → "found");
    ///  • tokens shorter than 3 characters and stop words are dropped;
    ///  • simple plural folding: "permissions" also yields "permission".
    /// Returns an empty set for empty / all-noise input.
    /// </summary>
    public static HashSet<string> Tokenize(string? text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var word = new StringBuilder();

        foreach (var c in Fold(text))
        {
            if (char.IsLetterOrDigit(c) || c is '.' or '_' or '-')
                word.Append(c);
            else
                FlushWord(word, tokens);
        }
        FlushWord(word, tokens);

        return tokens;
    }

    // ── Internals ────────────────────────────────────────────────────────────────

    private static void FlushWord(StringBuilder word, HashSet<string> tokens)
    {
        if (word.Length == 0) return;

        var raw = word.ToString().Trim('.', '_', '-');
        word.Clear();
        if (raw.Length == 0) return;

        AddToken(raw, tokens);

        if (raw.IndexOfAny(Separators) >= 0)
            foreach (var part in raw.Split(Separators))
                AddToken(part, tokens);
    }

    private static readonly char[] Separators = { '.', '_', '-' };

    private static void AddToken(string token, HashSet<string> tokens)
    {
        if (token.Length < MinTokenLength || StopWords.Contains(token)) return;

        tokens.Add(token);

        // plural → singular ("permissions", "erreurs"); never strip "ss" ("access")
        if (token.Length > 4 && token[^1] == 's' && !token.EndsWith("ss", StringComparison.Ordinal))
        {
            var singular = token[..^1];
            if (!StopWords.Contains(singular)) tokens.Add(singular);
        }
    }
}