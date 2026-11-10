using System.Text.RegularExpressions;

namespace MusicManager.Core.Rules;

/// <summary>
/// Titles in non-Latin scripts (Japanese, Cyrillic, …) get an English translation with the
/// original in brackets: "リトルワールド" → "Little World (リトルワールド)".
/// </summary>
public static partial class ForeignTitles
{
    /// <summary>True if the value contains non-Latin letters outside a trailing "(original)" part.</summary>
    public static bool NeedsTranslation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var m = TrailingGroup().Match(value);
        var main = m.Success && HasNonLatinLetters(m.Groups["inner"].Value) && !HasLatinLetters(m.Groups["inner"].Value)
            ? value[..m.Index]
            : value;

        // Already translated: Latin text followed by the non-Latin original in brackets.
        if (main.Length < value.Length && !HasNonLatinLetters(main) && HasLatinLetters(main)) return false;
        return HasNonLatinLetters(value);
    }

    public static string Combine(string original, string? translation)
    {
        var t = translation?.Trim();
        if (string.IsNullOrEmpty(t) || string.Equals(t, original.Trim(), StringComparison.OrdinalIgnoreCase) || HasNonLatinLetters(t))
            return original;
        return $"{t} ({original.Trim()})";
    }

    public static bool HasNonLatinLetters(string s) => s.Any(c => char.IsLetter(c) && !IsLatin(c));

    public static bool HasLatinLetters(string s) => s.Any(c => char.IsLetter(c) && IsLatin(c));

    private static bool IsLatin(char c) =>
        c <= 0x024F                      // Basic Latin … Latin Extended-B
        || c is >= (char)0x1E00 and <= (char)0x1EFF   // Latin Extended Additional
        || c is >= (char)0x2C60 and <= (char)0x2C7F   // Latin Extended-C
        || c is >= (char)0xA720 and <= (char)0xA7FF   // Latin Extended-D
        || c is >= (char)0xFF21 and <= (char)0xFF5A;  // Fullwidth Latin

    [GeneratedRegex(@"\s*\((?<inner>[^()]*)\)\s*$")]
    private static partial Regex TrailingGroup();
}
