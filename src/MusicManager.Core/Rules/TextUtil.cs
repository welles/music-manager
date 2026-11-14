using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicManager.Core.Rules;

public static partial class TextUtil
{
    private static readonly Dictionary<char, string> Transliterations = new()
    {
        ['ß'] = "ss", ['ẞ'] = "SS", ['æ'] = "ae", ['Æ'] = "AE", ['œ'] = "oe", ['Œ'] = "OE",
        ['ø'] = "o", ['Ø'] = "O", ['đ'] = "d", ['Đ'] = "D", ['ð'] = "d", ['Ð'] = "D",
        ['þ'] = "th", ['Þ'] = "TH", ['ł'] = "l", ['Ł'] = "L", ['ı'] = "i", ['×'] = "x",
        ['‘'] = "'", ['’'] = "'", ['‚'] = "'", ['‛'] = "'", ['′'] = "'",
        ['“'] = "\"", ['”'] = "\"", ['„'] = "\"", ['‟'] = "\"", ['″'] = "\"",
        ['‐'] = "-", ['‑'] = "-", ['‒'] = "-", ['–'] = "-", ['—'] = "-", ['―'] = "-", ['−'] = "-",
        ['…'] = "...", ['«'] = "<<", ['»'] = ">>", ['‹'] = "<", ['›'] = ">",
        [' '] = " ", [' '] = " ", [' '] = " ",
    };

    /// <summary>Typographic punctuation → ASCII punctuation (Picard "convert_punctuation").</summary>
    public static string ConvertPunctuation(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            // Only punctuation/whitespace replacements here; letters stay untouched.
            if (Transliterations.TryGetValue(ch, out var r) && !char.IsLetter(ch))
                sb.Append(r);
            else
                sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>Converts to plain ASCII: removes diacritics, transliterates, replaces the rest with "_".</summary>
    public static string ToAscii(string s)
    {
        var sb = new StringBuilder(s.Length);
        // By rune, not by char: a character outside the BMP (an emoji in a MusicBrainz title) is a surrogate pair,
        // and normalizing one half of it throws.
        foreach (var rune in WithoutLoneSurrogates(s).Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            if (rune.IsAscii)
            {
                sb.Append((char)rune.Value);
                continue;
            }

            if (rune.IsBmp && Transliterations.TryGetValue((char)rune.Value, out var t))
            {
                sb.Append(t);
                continue;
            }

            var decomposed = rune.ToString().Normalize(NormalizationForm.FormD);
            var stripped = new StringBuilder();
            foreach (var d in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(d) != UnicodeCategory.NonSpacingMark && d < 128)
                    stripped.Append(d);
            }

            sb.Append(stripped.Length > 0 ? stripped.ToString() : "_");
        }

        return sb.ToString();
    }

    /// <summary>Broken UTF-16 (a surrogate without its partner, e.g. from damaged tags) becomes U+FFFD, so it can be normalized.</summary>
    private static string WithoutLoneSurrogates(string s)
    {
        if (!s.Any(char.IsSurrogate)) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                sb.Append(s[i]).Append(s[++i]);
            else
                sb.Append(char.IsSurrogate(s[i]) ? '�' : s[i]);
        }

        return sb.ToString();
    }

    /// <summary>Lower-case, ASCII, alphanumerics only, single spaces. Used for fuzzy comparisons.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var ascii = ToAscii(s).ToLowerInvariant().Replace("&", " and ");
        ascii = NonAlnum().Replace(ascii, " ");
        return MultiSpace().Replace(ascii, " ").Trim();
    }

    /// <summary>Similarity 0..1 combining edit distance and token overlap.</summary>
    public static double Similarity(string? a, string? b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        if (na.Length == 0 || nb.Length == 0) return 0;
        if (na == nb) return 1;

        var lev = 1.0 - (double)Levenshtein(na, nb) / Math.Max(na.Length, nb.Length);

        var ta = na.Split(' ').ToHashSet();
        var tb = nb.Split(' ').ToHashSet();
        var inter = ta.Intersect(tb).Count();
        // Token-set ratio: tolerant against additions like "(Deluxe Edition)" or "Remastered".
        var tokenSet = (double)inter / Math.Min(ta.Count, tb.Count);
        var jaccard = (double)inter / ta.Union(tb).Count();

        return Math.Max(lev, 0.7 * tokenSet + 0.3 * jaccard);
    }

    public static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }

    public static string? NullIfEmpty(this string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [GeneratedRegex(@"[^a-z0-9 ]")]
    private static partial Regex NonAlnum();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpace();
}
