using System.Text.RegularExpressions;
using MusicManager.Core.Model;

namespace MusicManager.Core.Rules;

/// <summary>
/// Generic tagging rules, derived from the intent of the Picard "Automatic Script" (see
/// docs/tagging-rules.md). Applied to every file before writing, regardless of whether the
/// tags came from MusicBrainz, a fallback source or manual input.
///
/// When the tags come from MusicBrainz, <see cref="TagBuilder"/> already fills the
/// artist lists from the structured artist credits; string splitting is only used as a
/// fallback for data without credits. The function is pure and idempotent.
/// </summary>
public static partial class TaggingRules
{
    public static TagSet Apply(TagSet input, ArtistRuleSet rules, bool convertPunctuation = true)
    {
        var t = input.Clone();

        if (convertPunctuation)
        {
            t.Title = Conv(t.Title);
            t.Artist = Conv(t.Artist);
            t.AlbumArtist = Conv(t.AlbumArtist);
            t.Album = Conv(t.Album);
            t.Remixer = Conv(t.Remixer);
            t.Artists = t.Artists.Select(a => TextUtil.ConvertPunctuation(a)).ToList();
            t.AlbumArtists = t.AlbumArtists.Select(a => TextUtil.ConvertPunctuation(a)).ToList();
        }

        // Title cleanup: " (explicit)" and " (album version)", case-insensitive.
        if (t.Title != null)
            t.Title = TitleNoise().Replace(t.Title, "").Trim();

        // Artist list: from the MusicBrainz credits if present, otherwise split the display string.
        if (t.Artists.Count == 0)
            t.Artists = rules.Split(t.Artist);

        // Features belong to the artist field, not into the title.
        if (t.Title != null)
        {
            var (cleanTitle, featured) = ExtractFeatures(t.Title);
            if (featured != null)
            {
                t.Title = cleanTitle;
                foreach (var f in rules.Split(featured))
                {
                    if (!t.Artists.Contains(f, StringComparer.OrdinalIgnoreCase))
                        t.Artists.Add(f);
                }

                if (t.Artist == null)
                    t.Artist = string.Join(" feat. ", new[] { t.Artists.FirstOrDefault(), featured }.Where(x => x != null));
                else if (!rules.Split(featured).All(f => rules.Split(t.Artist).Contains(f, StringComparer.OrdinalIgnoreCase)))
                    t.Artist = $"{t.Artist} feat. {featured}";
            }
        }

        // Remixer: read from the title "(Name Remix)" if the tag is missing.
        if (string.IsNullOrWhiteSpace(t.Remixer) && t.Title != null)
        {
            var m = RemixerPattern().Match(t.Title);
            if (m.Success)
                t.Remixer = m.Groups["name"].Value.Trim();
        }

        if (!string.IsNullOrWhiteSpace(t.Remixer))
        {
            foreach (var r in rules.Split(t.Remixer))
            {
                if (!t.Artists.Contains(r, StringComparer.OrdinalIgnoreCase))
                    t.Artists.Add(r);
            }
        }

        // Album artist list.
        if (t.AlbumArtists.Count == 0 && !string.IsNullOrWhiteSpace(t.AlbumArtist))
        {
            var albumArtistSplit = rules.Split(t.AlbumArtist);
            var artistSplit = rules.Split(t.Artist);
            t.AlbumArtists = SameNames(albumArtistSplit, artistSplit) ? [.. t.Artists] : albumArtistSplit;
        }

        // Forced splits and replacements (lists and display strings).
        t.Artists = rules.ApplyToList(t.Artists);
        t.AlbumArtists = rules.ApplyToList(t.AlbumArtists);
        t.Artist = rules.ApplyReplacements(t.Artist);
        t.AlbumArtist = rules.ApplyReplacements(t.AlbumArtist);

        if (string.IsNullOrWhiteSpace(t.Artist) && t.Artists.Count > 0)
            t.Artist = string.Join(" & ", t.Artists);

        return t;
    }

    /// <summary>
    /// Adds featured artists named in the original title ("Song (feat. B)") that the MusicBrainz
    /// credits do not contain, so features are not lost when MusicBrainz omits them.
    /// </summary>
    public static TagSet AddFeaturesFromOriginalTitle(TagSet tags, string? originalTitle, ArtistRuleSet rules)
    {
        if (string.IsNullOrWhiteSpace(originalTitle)) return tags;
        var (_, featured) = ExtractFeatures(originalTitle);
        if (featured == null) return tags;

        var known = tags.Artists.Select(TextUtil.Normalize).ToHashSet();
        var missing = rules.Split(featured).Where(f => !known.Contains(TextUtil.Normalize(f))).ToList();
        if (missing.Count == 0) return tags;

        var t = tags.Clone();
        t.Artists.AddRange(missing);
        var joined = string.Join(" & ", missing);
        t.Artist = string.IsNullOrWhiteSpace(t.Artist)
            ? string.Join(" & ", t.Artists)
            : FeatureMarker().IsMatch(t.Artist) ? $"{t.Artist} & {joined}" : $"{t.Artist} feat. {joined}";
        return t;
    }

    /// <summary>Removes "(feat. X)" / " ft. X" from a title and returns the featured artist string.</summary>
    public static (string Title, string? Featured) ExtractFeatures(string title)
    {
        var m = BracketFeature().Match(title);
        if (!m.Success)
            m = TrailingFeature().Match(title);
        if (!m.Success)
            return (title, null);

        var cleaned = (title[..m.Index] + title[(m.Index + m.Length)..]).Trim();
        cleaned = MultiSpace().Replace(cleaned, " ");
        return (cleaned, m.Groups["a"].Value.Trim());
    }

    private static bool SameNames(List<string> a, List<string> b) =>
        a.Count == b.Count && a.Select(TextUtil.Normalize).Order().SequenceEqual(b.Select(TextUtil.Normalize).Order());

    private static string? Conv(string? s) => s == null ? null : TextUtil.ConvertPunctuation(s);

    // Variant markers that are not part of the song title: "(Explicit)", "[Explicit Version]", "(Album Version)".
    [GeneratedRegex(@"\s*[\(\[](explicit(\s+version)?|album\s+version)[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex TitleNoise();

    [GeneratedRegex(@"\((?<name>[^()]+?)\s+remix\)", RegexOptions.IgnoreCase)]
    private static partial Regex RemixerPattern();

    [GeneratedRegex(@"\s*[\(\[](?:feat\.?|ft\.?|featuring)\s+(?<a>[^\)\]]+)[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex BracketFeature();

    [GeneratedRegex(@"\s+(?:feat\.|ft\.|featuring)\s+(?<a>[^\(\[]+?)(?=\s*[\(\[]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingFeature();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();

    [GeneratedRegex(@"\s(feat\.|ft\.|featuring)\s", RegexOptions.IgnoreCase)]
    private static partial Regex FeatureMarker();
}
