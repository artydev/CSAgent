using System.Globalization;

namespace CsAgent.Core.Llm;

/// <summary>
/// Translation of the routing messages (reasons, warnings, --explain-routing, --init-routing and the
/// generated files). English is the source language: the English text is the key, so a message with no
/// translation simply stays in English. Languages: English (default) and French.
/// Chosen by <c>--lang en|fr</c>, then <c>CSAGENT_LANG</c>, then the system language (LC_ALL, LANG, UI culture).
/// </summary>
public static class Loc
{
    private static string? _forced;
    private static string? _detected;

    /// <summary>Two-letter language code in use: "en" or "fr".</summary>
    public static string Language => Normalize(_forced) ?? (_detected ??= Detect());

    /// <summary>Forces the language (null = automatic). Unknown codes fall back to English.</summary>
    public static void Set(string? language) => _forced = string.IsNullOrWhiteSpace(language) ? null : (Normalize(language) ?? "en");

    public static bool IsFrench => Language == "fr";

    /// <summary>Messages that were looked up in French and not found (used by tests to find gaps).</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Missing = new();

    /// <summary>Translates a plain message.</summary>
    public static string T(string english) => Lookup(english) ?? english;

    /// <summary>Translates an interpolated message: the template is the key, the values are filled in afterwards.</summary>
    public static string F(FormattableString english)
    {
        var template = Lookup(english.Format);
        return template is null ? english.ToString(CultureInfo.InvariantCulture)
                                : string.Format(CultureInfo.InvariantCulture, template, english.GetArguments());
    }

    private static string? Lookup(string key)
    {
        if (!IsFrench) return null;
        if (French.Messages.TryGetValue(key, out var fr)) return fr;
        Missing.TryAdd(key, 0);
        return null;
    }

    private static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim().ToLowerInvariant();
        if (c.StartsWith("fr")) return "fr";
        if (c.StartsWith("en") || c == "c" || c == "posix") return "en";
        return null;
    }

    private static string Detect()
    {
        foreach (var name in new[] { "CSAGENT_LANG", "LC_ALL", "LC_MESSAGES", "LANG" })
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(v)) continue;
            var n = Normalize(v);
            if (n is not null) return n;
            if (name == "CSAGENT_LANG") return "en"; // explicit but unsupported
        }
        try { return Normalize(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) ?? "en"; }
        catch { return "en"; }
    }

    /// <summary>Resets the forced language and the detection (tests).</summary>
    internal static void Reset() { _forced = null; _detected = null; Missing.Clear(); }
}
