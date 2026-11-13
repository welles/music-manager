using System.Text;
using MusicManager.Core.Model;

namespace MusicManager.Core.Rules;

/// <summary>
/// File naming after the (lost) final version of the Picard "File Renaming Script":
/// <c>{main album artist}/{album}/{main track artist} - {title}</c>
/// with the names exactly as in the tags (Unicode, see <see cref="SanitizeComponent"/>).
/// </summary>
public static class PathBuilder
{
    public const string NonAlbumFolder = "[non-album tracks]";
    private const int MaxComponentLength = 180;
    private const int MaxComponentBytes = 240;

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
        var file = SanitizeComponent($"{trackArtist} - {title}");

        return Path.Combine(dir1, dir2, file + extension);
    }

    /// <summary>
    /// Keeps the name exactly as in the tags (Unicode, NFC) – Linux only forbids "/" and NUL in a name.
    /// The few unavoidable changes use look-alike characters instead of "_": "/" → "∕" (U+2215, "AC∕DC"),
    /// a leading dot → "․" (U+2024, otherwise the folder would be hidden and skipped by the library index).
    /// Control characters are dropped and long names are cut to fit the 255-byte limit of the file system.
    /// </summary>
    public static string SanitizeComponent(string value)
    {
        var s = value.Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch == '/')
                sb.Append('∕');
            else if (!char.IsControl(ch))
                sb.Append(ch);
        }

        var result = sb.ToString().Trim();
        if (result.StartsWith('.'))
            result = "․" + result[1..];

        // ext4/ZFS allow 255 bytes per name; leave room for the extension.
        while (result.Length > 0 && (result.Length > MaxComponentLength || Encoding.UTF8.GetByteCount(result) > MaxComponentBytes))
        {
            var cut = result.Length - 1;
            if (char.IsLowSurrogate(result[cut])) cut--;
            result = result[..cut].TrimEnd();
        }

        return result.Length == 0 ? "_" : result;
    }
}
