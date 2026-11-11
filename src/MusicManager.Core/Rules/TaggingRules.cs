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
    /// <summary>Version of the rule code. Raise it when the rules change in a way that should re-tag files.</summary>
    public const int Version = 3;

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

        // Folder: only the main album artist (or main artist).
        t.FolderArtist ??= rules.MainArtist(t.AlbumArtist) ?? rules.MainArtist(t.Artist) ?? t.Artists.FirstOrDefault();

        return t;
    }

    /// <summary>
    /// Additional artists: if MusicBrainz credits more than one artist for the track, only those
    /// count. If MusicBrainz only knows the main artist, the additional artists of the source
    /// (artist list and "feat." in the title) are added – but only if the source's first artist
    /// is the same as the MusicBrainz main artist (otherwise e.g. band members of a duo would
    /// show up next to the duo name).
    /// </summary>
    public static TagSet AddSourceArtists(TagSet mb, TagSet? source, ArtistRuleSet rules)
    {
        if (source == null || mb.Artists.Count != 1) return mb;
        var main = mb.Artists[0];

        var sourceArtists = (source.Artists.Count > 0 ? source.Artists : rules.Split(source.Artist)).Select(rules.ReplaceName).ToList();
        var (_, featuredText) = ExtractFeatures(source.Title ?? "");
        var featured = featuredText == null ? [] : rules.Split(featuredText).Select(rules.ReplaceName).ToList();

        var known = new HashSet<string> { TextUtil.Normalize(main) };

        // The main artist may itself be a name with a separator ("JJ Grey & Mofro"), which the
        // source split into parts – those parts are the main artist, not collaborators.
        var consumed = MainArtistParts(sourceArtists, main);
        if (consumed == 0)
        {
            var first = sourceArtists.FirstOrDefault();
            if (first == null || TextUtil.Similarity(first, main) < 0.8) return mb;
            consumed = 1;
        }

        foreach (var part in sourceArtists.Take(consumed)) known.Add(TextUtil.Normalize(part));
        var feats = featured.Where(f => known.Add(TextUtil.Normalize(f))).ToList();
        var others = sourceArtists.Skip(consumed).Where(a => known.Add(TextUtil.Normalize(a))).ToList();
        if (feats.Count == 0 && others.Count == 0) return mb;

        var t = mb.Clone();
        t.Artists = [main, .. others, .. feats];
        var display = others.Count > 0 ? JoinList([mb.Artist ?? main, .. others]) : mb.Artist ?? main;
        t.Artist = feats.Count > 0 ? $"{display} feat. {JoinList(feats)}" : display;
        return t;
    }

    private static readonly HashSet<string> Connectors = ["and", "x", "with", "feat", "ft", "vs"];

    private static List<string> NameTokens(string name) =>
        TextUtil.Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !Connectors.Contains(t)).ToList();

    /// <summary>
    /// Number of leading source artists that together spell the main artist (e.g. "JJ Grey" +
    /// "Mofro" = "JJ Grey &amp; Mofro"), or 0 if they do not.
    /// </summary>
    private static int MainArtistParts(IReadOnlyList<string> sourceArtists, string main)
    {
        var target = NameTokens(main);
        var acc = new List<string>();
        for (var i = 0; i < sourceArtists.Count; i++)
        {
            acc.AddRange(NameTokens(sourceArtists[i]));
            if (acc.Count > target.Count || !acc.SequenceEqual(target.Take(acc.Count))) return 0;
            if (acc.Count == target.Count) return i + 1;
        }

        return 0;
    }

    /// <summary>"A", "A & B", "A, B & C".</summary>
    private static string JoinList(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Concat(items) : $"{string.Join(", ", items.Take(items.Count - 1))} & {items[^1]}";

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

}
