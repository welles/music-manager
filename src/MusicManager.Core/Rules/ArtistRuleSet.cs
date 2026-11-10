using System.Text.RegularExpressions;
using MusicManager.Core.Data;

namespace MusicManager.Core.Rules;

/// <summary>
/// Artist splitting configuration. Port of the Picard "Automatic Script":
/// splitter strings, forced splits, forced replacements and (new) an exception list.
/// </summary>
public sealed class ArtistRuleSet
{
    /// <summary>Separators from the Picard script (<c>_artist_splitter_strings</c>).</summary>
    public static readonly string[] DefaultSplitters =
        [" & ", " ft. ", " feat. ", ", ", " × ", " with ", " and ", " special appearance by "];

    public IReadOnlyList<string> Splitters { get; init; } = DefaultSplitters;
    public IReadOnlyList<string> ForceSplit { get; init; } = [];
    public IReadOnlyList<(string From, string To)> ForceReplace { get; init; } = [];
    public IReadOnlyList<string> NoSplit { get; init; } = [];

    public static ArtistRuleSet FromRules(IEnumerable<ArtistRule> rules, IReadOnlyList<string>? splitters = null)
    {
        var list = rules.ToList();
        return new ArtistRuleSet
        {
            Splitters = splitters is { Count: > 0 } ? splitters : DefaultSplitters,
            ForceSplit = list.Where(r => r.Kind == ArtistRuleKind.ForceSplit).Select(r => r.Pattern).ToList(),
            ForceReplace = list.Where(r => r.Kind == ArtistRuleKind.ForceReplace && r.Replacement != null)
                .Select(r => (r.Pattern, r.Replacement!)).ToList(),
            NoSplit = list.Where(r => r.Kind == ArtistRuleKind.NoSplit).Select(r => r.Pattern).ToList(),
        };
    }

    private Regex? _splitRegex;

    private Regex SplitRegex => _splitRegex ??= new Regex(
        string.Join("|", Splitters.Select(Regex.Escape)), RegexOptions.IgnoreCase);

    /// <summary>
    /// Splits a composed artist string ("A feat. B & C") into individual artists.
    /// Names on the exception list are never split.
    /// </summary>
    public List<string> Split(string? artist) => SplitCore(artist, NoSplit);

    /// <summary>
    /// Main artist of a composed string (used for folder names). Names on the force-split and
    /// replacement lists count as one artist here: "Audio88 &amp; Yassin feat. X" → "Audio88 &amp; Yassin".
    /// </summary>
    public string? MainArtist(string? artist)
    {
        var first = SplitCore(artist, NoSplit.Concat(ForceSplit).Concat(ForceReplace.Select(r => r.From)).ToList()).FirstOrDefault();
        return first == null ? null : ReplaceName(first);
    }

    private List<string> SplitCore(string? artist, IReadOnlyList<string> keepTogether)
    {
        if (string.IsNullOrWhiteSpace(artist)) return [];

        // Protect exception names by replacing them with placeholders.
        var protectedNames = new List<string>();
        var text = artist;
        foreach (var name in keepTogether.OrderByDescending(n => n.Length))
        {
            var idx = text.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                var placeholder = $"\u0001{protectedNames.Count}\u0001";
                protectedNames.Add(text.Substring(idx, name.Length));
                text = text[..idx] + placeholder + text[(idx + name.Length)..];
                idx = text.IndexOf(name, idx + placeholder.Length, StringComparison.OrdinalIgnoreCase);
            }
        }

        return SplitRegex.Split(text)
            .Select(p => Regex.Replace(p, "\u0001(\\d+)\u0001", m => protectedNames[int.Parse(m.Groups[1].Value)]))
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Splits a name with the configured splitters, ignoring the exception list (used for forced splits).</summary>
    public List<string> SplitUnconditionally(string artist) =>
        SplitRegex.Split(artist).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

    /// <summary>
    /// Applies forced replacements inside a composed display string. Only relevant for strings
    /// that did not come from MusicBrainz credits (manual input, fallback sources).
    /// </summary>
    public string? ApplyReplacements(string? value)
    {
        if (value == null) return null;
        foreach (var (from, to) in ForceReplace)
            value = ReplaceIgnoreCase(value, from, to);
        return value;
    }

    /// <summary>
    /// Replacement for one individual artist (whole-name match). Applied per MusicBrainz credit,
    /// so "Bob Seger &amp; the Silver Bullet Band" becomes "Bob Seger" wherever it is credited.
    /// </summary>
    public string ReplaceName(string name)
    {
        foreach (var (from, to) in ForceReplace)
        {
            if (string.Equals(from.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                return to;
        }

        return name;
    }

    /// <summary>Applies forced splits and replacements to a list of individual artists.</summary>
    public List<string> ApplyToList(IEnumerable<string> artists)
    {
        var result = new List<string>();
        foreach (var a in artists)
        {
            var name = ReplaceName(a);
            var forced = ForceSplit.FirstOrDefault(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
            if (forced != null)
                result.AddRange(SplitUnconditionally(name));
            else
                result.Add(name);
        }

        return result.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ReplaceIgnoreCase(string input, string from, string to)
    {
        if (string.IsNullOrEmpty(from)) return input;
        return Regex.Replace(input, Regex.Escape(from), to.Replace("$", "$$"), RegexOptions.IgnoreCase);
    }
}
