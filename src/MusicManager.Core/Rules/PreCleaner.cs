using System.Text.RegularExpressions;
using MusicManager.Core.Data;
using MusicManager.Core.Model;

namespace MusicManager.Core.Rules;

/// <summary>
/// Pre-cleaning before matching: source specific rules (e.g. the MusicFab import script)
/// and generic removal of noise like "(Official Video) [HD]".
/// </summary>
public static partial class PreCleaner
{
    public sealed record Result(TagSet Tags, bool LooksMessy, List<string> Changes);

    public static Result Clean(TagSet original, string fileName, SourceProfile? profile)
    {
        var t = original.Clone();
        var changes = new List<string>();
        var messy = false;

        if (profile is { Enabled: true })
            ApplyProfile(t, profile, changes);

        // Generic noise in title and artist.
        if (t.Title != null)
        {
            var cleaned = CleanNoise(t.Title);
            if (cleaned != t.Title)
            {
                changes.Add($"Titel bereinigt: \"{t.Title}\" → \"{cleaned}\"");
                t.Title = cleaned;
                messy = true;
            }
        }

        if (t.Artist != null)
        {
            var cleaned = CleanNoise(t.Artist);
            if (cleaned != t.Artist)
            {
                changes.Add($"Künstler bereinigt: \"{t.Artist}\" → \"{cleaned}\"");
                t.Artist = cleaned;
                messy = true;
            }
        }

        // "Artist - Title" inside the title tag, or no tags at all → use the file name.
        if (string.IsNullOrWhiteSpace(t.Artist))
        {
            messy = true;
            var source = !string.IsNullOrWhiteSpace(t.Title) && t.Title.Contains(" - ") ? t.Title : ParseableFileName(fileName);
            var parts = source.Split(" - ", 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
            {
                t.Artist = parts[0];
                t.Title = CleanNoise(parts[1]);
                changes.Add($"Künstler/Titel aus \"{source}\" gelesen");
            }
            else if (string.IsNullOrWhiteSpace(t.Title))
            {
                t.Title = CleanNoise(source);
                changes.Add("Titel aus Dateiname gelesen");
            }
        }
        else if (string.IsNullOrWhiteSpace(t.Title))
        {
            messy = true;
            t.Title = CleanNoise(ParseableFileName(fileName));
            changes.Add("Titel aus Dateiname gelesen");
        }

        if (t.Title != null && (t.Title.Contains('_') || UrlLike().IsMatch(t.Title)))
            messy = true;
        if (t.Artist != null && t.Title != null && string.Equals(t.Artist, t.Title, StringComparison.OrdinalIgnoreCase))
            messy = true;

        return new Result(t, messy, changes);
    }

    public static void ApplyProfile(TagSet t, SourceProfile profile, List<string> changes)
    {
        // "A,B" → multiple artists (comma without whitespace).
        if (profile.SplitCommaWithoutSpace && t.Artist != null && CommaWithoutSpace().IsMatch(t.Artist))
        {
            t.Artists = t.Artist.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            var joined = string.Join(", ", t.Artists);
            changes.Add($"[{profile.Name}] Künstler aufgeteilt: \"{t.Artist}\" → {string.Join(" | ", t.Artists)}");
            t.Artist = joined;
        }

        foreach (var tag in profile.DeleteTags)
        {
            if (DeleteTag(t, tag.Trim()))
                changes.Add($"[{profile.Name}] Tag \"{tag}\" gelöscht");
        }

        if (profile.TrimTrackTotal && t.Extra.TryGetValue("tracknumber", out var raw))
        {
            var trimmed = Regex.Replace(raw, "/.+$", "");
            if (trimmed != raw)
            {
                t.Extra["tracknumber"] = trimmed;
                changes.Add($"[{profile.Name}] Tracknummer \"{raw}\" → \"{trimmed}\"");
            }
        }

        if (profile.TrimTrackTotal && t.TrackCount != null)
            t.TrackCount = null;
    }

    private static bool DeleteTag(TagSet t, string tag)
    {
        switch (tag.ToLowerInvariant())
        {
            case "discnumber":
                var hadDisc = t.DiscNumber != null || t.DiscCount != null;
                t.DiscNumber = null;
                t.DiscCount = null;
                return hadDisc;
            case "tracknumber":
                var hadTrack = t.TrackNumber != null;
                t.TrackNumber = null;
                t.TrackCount = null;
                return hadTrack;
            case "album":
                var hadAlbum = t.Album != null;
                t.Album = null;
                return hadAlbum;
            case "albumartist":
                var hadAa = t.AlbumArtist != null;
                t.AlbumArtist = null;
                t.AlbumArtists.Clear();
                return hadAa;
            case "date":
            case "year":
                var hadDate = t.Date != null;
                t.Date = null;
                return hadDate;
            case "genre":
                var hadGenre = t.Genres.Count > 0;
                t.Genres.Clear();
                return hadGenre;
            default:
                return t.Extra.Remove(tag);
        }
    }

    /// <summary>Removes "(Official Video)", "[HD]", "(Lyrics)" and similar from a title.</summary>
    public static string CleanNoise(string value)
    {
        var s = Noise().Replace(value, "");
        s = s.Replace('_', ' ');
        s = Regex.Replace(s, @"\s{2,}", " ").Trim().Trim('-', '|').Trim();
        return s.Length == 0 ? value.Trim() : s;
    }

    /// <summary>File name without extension, track number prefix and underscores.</summary>
    public static string ParseableFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ');
        name = LeadingTrackNumber().Replace(name, "");
        return Regex.Replace(name, @"\s{2,}", " ").Trim();
    }

    [GeneratedRegex(@"\s*[\(\[]\s*(official\s*(music\s*)?(video|audio|visuali[sz]er|lyric(s)?\s*video)?|(official\s*)?lyric(s)?(\s*video)?|music\s*video|video\s*clip|videoclip|audio|visuali[sz]er|hd|hq|4k|1080p|720p|full\s*hd|explicit\s*video|clean|dirty|free\s*download|out\s*now|premiere)\s*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"\S,\S")]
    private static partial Regex CommaWithoutSpace();

    [GeneratedRegex(@"^\s*\d{1,3}\s*[\.\-_)]?\s+(?=\S)")]
    private static partial Regex LeadingTrackNumber();

    [GeneratedRegex(@"(www\.|https?://|\.com\b|\.net\b)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlLike();
}
