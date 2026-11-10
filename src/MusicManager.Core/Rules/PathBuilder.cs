using System.Text;
using MusicManager.Core.Model;

namespace MusicManager.Core.Rules;

/// <summary>
/// File naming, port of the Picard "File Renaming Script":
/// <c>$if2(%albumartist%,$getmulti(%artist%,0),%artist%)/%album%/$if2(...) - %title%</c>
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

        var dir1 = SanitizeComponent(albumArtist);
        var dir2 = SanitizeComponent(album);
        var file = SanitizeComponent($"{albumArtist} - {title}");

        return Path.Combine(dir1, dir2, file + extension);
    }

    /// <summary>ASCII only, Windows compatible, slashes in values replaced by "_".</summary>
    public static string SanitizeComponent(string value)
    {
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

        // Windows does not allow trailing dots or spaces.
        result = result.TrimEnd('.', ' ');
        if (result.Length > MaxComponentLength)
            result = result[..MaxComponentLength].TrimEnd('.', ' ');

        return result.Length == 0 ? "_" : result;
    }
}
