using System.Text;
using MusicManager.Core.Model;

namespace MusicManager.Core.Rules;

/// <summary>
/// File naming after the (lost) final version of the Picard "File Renaming Script":
/// <c>{main album artist}/{album}/{main track artist} - {title}</c>
/// with ASCII-only and Windows-compatible file names.
/// </summary>
public static class PathBuilder
{
    public const string NonAlbumFolder = "[non-album tracks]";
    private const int MaxComponentLength = 180;

    private static readonly char[] WindowsReplacements = ['|', '?', '>', '<', ':', '*', '"'];

    /// <summary>Returns the target path relative to the library root.</summary>
    public static string Build(TagSet tags, string extension = ".m4a")
    {
        var albumArtist = tags.AlbumArtist.NullIfEmpty()
                          ?? tags.Artists.FirstOrDefault().NullIfEmpty()
                          ?? tags.Artist.NullIfEmpty()
                          ?? "Unknown Artist";
        var album = tags.Album.NullIfEmpty() ?? NonAlbumFolder;
        var title = tags.Title.NullIfEmpty() ?? "Unknown Title";

        // The folder uses the main (first) album artist only, the file name the main (first) artist of
        // the track – as in the existing library: "Ceschi/…/Ceschi - Say No More" although ALBUMARTIST is
        // "Ceschi | Commoners Choir", and "Various Artists/Shell Shock/Fox Stevenson - Sandblast".
        var mainArtist = tags.FolderArtist.NullIfEmpty() ?? albumArtist;
        var trackArtist = tags.Artists.FirstOrDefault().NullIfEmpty() ?? tags.Artist.NullIfEmpty() ?? mainArtist;
        var dir1 = SanitizeComponent(mainArtist);
        var dir2 = SanitizeComponent(album);
        var file = SanitizeComponent($"{trackArtist} - {title}", directory: false);

        return Path.Combine(dir1, dir2, file + extension);
    }

    /// <summary>
    /// ASCII only, Windows compatible, slashes in values replaced by "_". Like Picard, a trailing dot
    /// of a folder name becomes "_" ("Fred again.." → "Fred again._", "J.B.O." → "J.B.O_"); file
    /// names keep it because the extension follows ("Born in the U.S.A..m4a").
    /// </summary>
    public static string SanitizeComponent(string value, bool directory = true)
    {
        // "Little World (リトルワールド)" → "Little World": bracket parts without any Latin letters
        // would only become underscores in an ASCII file name.
        var withoutForeign = System.Text.RegularExpressions.Regex.Replace(value, @"\s*[\(\[]([^\)\]]*)[\)\]]",
            m => ForeignTitles.HasNonLatinLetters(m.Value) && !ForeignTitles.HasLatinLetters(m.Groups[1].Value) ? "" : m.Value).Trim();
        if (withoutForeign.Length > 0) value = withoutForeign;

        var s = TextUtil.ToAscii(value);
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch is '/' or '\\' || WindowsReplacements.Contains(ch))
                sb.Append('_');
            else if (!char.IsControl(ch))
                sb.Append(ch);
        }

        var result = sb.ToString().Trim();
        if (result.Length > MaxComponentLength)
            result = result[..MaxComponentLength].TrimEnd();

        // Windows does not allow folder names ending in a dot.
        if (directory && result.EndsWith('.'))
            result = result[..^1] + "_";

        return result.Length == 0 ? "_" : result;
    }
}
