using System.Globalization;
using System.Text.RegularExpressions;
using MusicManager.Core.Configuration;

namespace MusicManager.Core.Rules;

/// <summary>
/// Choosing and spelling genres. Votes from a source are weighted, filtered (blacklist, minimum share of
/// the strongest genre, maximum count) and written in title case like the existing library
/// ("Drum And Bass", "Hip Hop"), with abbreviations in capitals ("UK Garage", "EDM") and the user's
/// spelling map applied last.
/// </summary>
public static partial class GenreRules
{
    /// <summary>Folksonomy spellings (Last.fm, Deezer) → MusicBrainz genre names (lower case).</summary>
    private static readonly Dictionary<string, string> Variants = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hip-hop"] = "hip hop",
        ["hiphop"] = "hip hop",
        ["rap/hip hop"] = "hip hop",
        ["drum & bass"] = "drum and bass",
        ["drum n bass"] = "drum and bass",
        ["drum'n'bass"] = "drum and bass",
        ["drum 'n' bass"] = "drum and bass",
        ["drumnbass"] = "drum and bass",
        ["dnb"] = "drum and bass",
        ["d&b"] = "drum and bass",
        ["rnb"] = "r&b",
        ["r'n'b"] = "r&b",
        ["rhythm and blues"] = "rhythm & blues",
        ["electronica/dance"] = "electronic",
        ["dance/electronic"] = "electronic",
        ["electro house"] = "electro house",
        ["synthpop"] = "synth-pop",
        ["synth pop"] = "synth-pop",
        ["lofi"] = "lo-fi",
        ["lo fi"] = "lo-fi",
        ["uk garage"] = "uk garage",
        ["ukg"] = "uk garage",
        ["2step"] = "2-step",
        ["2 step"] = "2-step",
    };

    /// <summary>Words written in capitals after title-casing.</summary>
    private static readonly Dictionary<string, string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["uk"] = "UK", ["us"] = "US", ["usa"] = "USA", ["nyc"] = "NYC", ["la"] = "LA", ["dj"] = "DJ",
        ["edm"] = "EDM", ["idm"] = "IDM", ["ebm"] = "EBM", ["mpb"] = "MPB", ["aor"] = "AOR", ["ccm"] = "CCM",
        ["r&b"] = "R&B", ["j-pop"] = "J-Pop", ["k-pop"] = "K-Pop", ["j-rock"] = "J-Rock", ["c-pop"] = "C-Pop",
    };

    /// <summary>Lower-case key used to compare genres from different sources.</summary>
    public static string Key(string genre)
    {
        var s = Spaces().Replace(genre.Trim().ToLowerInvariant(), " ");
        return Variants.TryGetValue(s, out var v) ? v : s;
    }

    /// <summary>Title case like the library ("drum and bass" → "Drum And Bass"), abbreviations in capitals.</summary>
    public static string Display(string key)
    {
        var titled = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key);
        return Word().Replace(titled, m => Abbreviations.TryGetValue(m.Value, out var a) ? a : m.Value);
    }

    /// <summary>
    /// Picks genres from votes: drops filtered names, keeps those with at least <see cref="AppSettings.MinGenreUsagePercent"/>
    /// of the strongest genre's votes, at most <see cref="AppSettings.MaxGenres"/>. Keys must already be normalized.
    /// </summary>
    public static List<string> Select(IReadOnlyDictionary<string, double> votes, AppSettings settings)
    {
        var filter = settings.GenreFilter.Select(Key).ToHashSet();
        var candidates = votes.Where(v => v.Value > 0 && !filter.Contains(v.Key)).ToList();
        if (candidates.Count == 0) return [];

        var max = candidates.Max(v => v.Value);
        return candidates
            .Where(v => v.Value * 100 >= max * settings.MinGenreUsagePercent)
            .OrderByDescending(v => v.Value).ThenBy(v => v.Key, StringComparer.Ordinal)
            .Take(settings.MaxGenres)
            .Select(v => v.Key)
            .ToList();
    }

    /// <summary>Adds votes under their normalized key.</summary>
    public static void Add(Dictionary<string, double> target, IEnumerable<KeyValuePair<string, int>> votes, double weight = 1)
    {
        foreach (var (name, count) in votes)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var key = Key(name);
            target[key] = target.GetValueOrDefault(key) + count * weight;
        }
    }

    /// <summary>
    /// Final spelling: title case, then the user's map ("Drum And Bass = Drum & Bass"; an empty right side drops
    /// the genre), duplicates removed, order kept.
    /// </summary>
    public static List<string> Finish(IEnumerable<string> genres, AppSettings settings)
    {
        var map = ParseMap(settings.GenreMap);
        var result = new List<string>();
        foreach (var g in genres)
        {
            if (string.IsNullOrWhiteSpace(g)) continue;
            var key = Key(g);
            var name = map.TryGetValue(key, out var mapped) ? mapped : Display(key);
            if (name.Length > 0 && !result.Contains(name, StringComparer.OrdinalIgnoreCase))
                result.Add(name);
        }

        return result;
    }

    /// <summary>"from = to" lines; keys are compared like <see cref="Key"/>.</summary>
    public static Dictionary<string, string> ParseMap(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var i = line.IndexOf('=');
            if (i <= 0) continue;
            var from = Key(line[..i]);
            if (from.Length > 0) map[from] = line[(i + 1)..].Trim();
        }

        return map;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[\p{L}\p{N}&-]+")]
    private static partial Regex Word();
}
