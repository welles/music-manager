using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MusicManager.Core.Catalog;

/// <summary>
/// Public IDs of the library catalog. The prefix says what an ID is ("tr-" song, "al-" album,
/// "ar-" artist, "pl-" playlist); the cover art ID of an item is the item's ID.
/// </summary>
public static class CatalogIds
{
    public const string SongPrefix = "tr-";
    public const string AlbumPrefix = "al-";
    public const string ArtistPrefix = "ar-";
    public const string PlaylistPrefix = "pl-";

    /// <summary>True for song, album and artist IDs (the items that can be starred and rated).</summary>
    public static bool IsItem(string id) =>
        id.Length > 3 && (id.StartsWith(SongPrefix, StringComparison.Ordinal) || id.StartsWith(AlbumPrefix, StringComparison.Ordinal)
                          || id.StartsWith(ArtistPrefix, StringComparison.Ordinal));

    public static bool IsSong(string id) => id.Length > SongPrefix.Length && id.StartsWith(SongPrefix, StringComparison.Ordinal);

    public static string Playlist(int id) => $"{PlaylistPrefix}{id.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Database key of a playlist ID, null if it is none.</summary>
    public static int? ParsePlaylist(string? id) =>
        id != null && id.StartsWith(PlaylistPrefix, StringComparison.Ordinal)
                   && int.TryParse(id.AsSpan(PlaylistPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;

    /// <summary>Random song ID for files without a (free) MusicBrainz release track ID.</summary>
    public static string NewSongId() => $"{SongPrefix}{Guid.NewGuid():N}";

    /// <summary>MusicBrainz release ID, otherwise a hash of album artist and album name.</summary>
    public static string Album(string? mbReleaseId, string? albumArtist, string? album) =>
        mbReleaseId is { Length: > 0 }
            ? $"{AlbumPrefix}{mbReleaseId.Trim().ToLowerInvariant()}"
            : $"{AlbumPrefix}{Hash($"{NormalizeName(albumArtist)}\u001f{NormalizeName(album)}")}";

    /// <summary>MusicBrainz artist ID, otherwise a hash of the normalized name.</summary>
    public static string Artist(string? mbArtistId, string name) =>
        mbArtistId is { Length: > 0 } ? $"{ArtistPrefix}{mbArtistId.Trim().ToLowerInvariant()}" : $"{ArtistPrefix}{Hash(NormalizeName(name))}";

    /// <summary>
    /// Case- and accent-insensitive key with collapsed whitespace. Unlike <see cref="Rules.TextUtil.Normalize"/>
    /// it keeps non-Latin scripts, so different Japanese or Cyrillic names do not collapse into one key.
    /// </summary>
    public static string NormalizeName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(ch))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space) sb.Append(' ');
            space = false;
            sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];
}
