using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using MusicManager.Core.Data;

namespace MusicManager.Core.Catalog;

/// <summary>
/// Finds the library song of a Spotify track: by ISRC first (the same recording), else by title and artist with the
/// length as a check. Built once per catalog snapshot, so matching follows the library without another Spotify sync.
/// </summary>
public sealed partial class SpotifyMatcher
{
    /// <summary>Lengths further apart are another version (live, extended …), not the same song.</summary>
    public static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(10);

    private static readonly ConditionalWeakTable<CatalogSnapshot, SpotifyMatcher> Cache = new();

    private readonly Dictionary<string, List<CatalogSong>> _byIsrc = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CatalogSong>> _byTitle = new(StringComparer.Ordinal);

    private SpotifyMatcher(CatalogSnapshot catalog)
    {
        foreach (var song in catalog.Songs)
        {
            if (!string.IsNullOrWhiteSpace(song.Isrc)) Add(_byIsrc, song.Isrc.Trim().ToUpperInvariant(), song);
            Add(_byTitle, NormalizeTitle(song.Title), song);
        }
    }

    public static SpotifyMatcher For(CatalogSnapshot catalog) => Cache.GetValue(catalog, c => new SpotifyMatcher(c));

    private static void Add(Dictionary<string, List<CatalogSong>> index, string key, CatalogSong song)
    {
        if (key.Length == 0) return;
        if (!index.TryGetValue(key, out var list)) index[key] = list = [];
        list.Add(song);
    }

    public CatalogSong? Match(SpotifyPlaylistTrack track) =>
        Match(track.Title, track.Artists, track.DurationMs, track.Isrc);

    public CatalogSong? Match(string title, IReadOnlyList<string> artists, int? durationMs, string? isrc)
    {
        TimeSpan? length = durationMs is > 0 ? TimeSpan.FromMilliseconds(durationMs.Value) : null;

        // Same ISRC: the same recording, also when the titles are spelled differently.
        if (!string.IsNullOrWhiteSpace(isrc) && _byIsrc.TryGetValue(isrc.Trim().ToUpperInvariant(), out var byIsrc))
            return Closest(byIsrc, length);

        if (!_byTitle.TryGetValue(NormalizeTitle(title), out var byTitle)) return null;
        var wanted = artists.Select(NormalizeName).Where(a => a.Length > 0).ToHashSet();
        var candidates = byTitle.Where(s => SongArtists(s).Overlaps(wanted)).ToList();
        if (length != null)
            candidates = candidates.Where(s => s.Duration <= 0 || (TimeSpan.FromSeconds(s.Duration) - length.Value).Duration() <= DurationTolerance).ToList();
        return candidates.Count == 0 ? null : Closest(candidates, length);
    }

    private static CatalogSong Closest(List<CatalogSong> songs, TimeSpan? length) =>
        length == null ? songs[0] : songs.MinBy(s => s.Duration <= 0 ? double.MaxValue : Math.Abs(s.Duration - length.Value.TotalSeconds))!;

    private static HashSet<string> SongArtists(CatalogSong song)
    {
        var names = song.Artists.Select(a => NormalizeName(a.Name)).ToHashSet();
        // The display line also covers names the artist list splits differently ("A & B").
        foreach (var part in ArtistSeparator().Split(song.DisplayArtist ?? "")) names.Add(NormalizeName(part));
        names.Remove("");
        return names;
    }

    /// <summary>
    /// Title without what differs between services but not between recordings: case, accents, punctuation, "(feat. …)",
    /// " - Remastered 2011", "(Radio Edit)" stays (another edit). Parts like "(Aerial Remix)" stay as well.
    /// </summary>
    public static string NormalizeTitle(string? title)
    {
        var t = title ?? "";
        t = Featuring().Replace(t, " ");
        t = RemasterSuffix().Replace(t, " ");
        return NormalizeName(t);
    }

    /// <summary>Lower case, no accents, "&amp;" as "and", only letters and digits.</summary>
    public static string NormalizeName(string? name)
    {
        var decomposed = (name ?? "").Replace("&", " and ").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"[\(\[]\s*(feat\.?|ft\.?|featuring|with)\s[^\)\]]*[\)\]]|\s(feat\.?|ft\.?|featuring)\s.*$", RegexOptions.IgnoreCase)]
    private static partial Regex Featuring();

    [GeneratedRegex(@"\s[-–]\s.*\b(remaster(ed)?|mono|stereo)\b.*$|[\(\[][^\)\]]*\bremaster(ed)?\b[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex RemasterSuffix();

    [GeneratedRegex(@"\s*(?:,|&|\bfeat\.?|\bft\.?|\band\b|\bx\b)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSeparator();
}
