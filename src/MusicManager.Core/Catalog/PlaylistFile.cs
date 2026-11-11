using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MusicManager.Core.Data;

namespace MusicManager.Core.Catalog;

/// <summary>
/// A playlist as an M3U8 file. Paths are relative to the file, so other players can read it; the
/// <c>#MUSICMANAGER-…</c> lines (comments for every other player) keep what a path alone cannot: the playlist
/// ID, comment, public flag and the song ID of every entry, including songs that are not in the library.
/// </summary>
public static class PlaylistFile
{
    /// <summary>Folder for the playlist files, relative to the library root.</summary>
    public const string Folder = "Playlists";

    public const string Extension = ".m3u8";

    private const string Prefix = "#MUSICMANAGER-";

    public sealed record Entry(string? SongId, string? Path);

    public sealed record Parsed(string? Name, int? Id, string? Comment, bool Public, DateTimeOffset? CreatedAt, List<Entry> Entries);

    /// <summary>The file content; <paramref name="song"/> gives the index row of a song ID (null: not in the library).</summary>
    public static string Render(Playlist playlist, Func<string, LibraryEntry?> song)
    {
        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        sb.Append("#PLAYLIST:").Append(OneLine(playlist.Name)).Append('\n');
        sb.Append(Prefix).Append("ID:").Append(CatalogIds.Playlist(playlist.Id)).Append('\n');
        sb.Append(Prefix).Append("CREATED:").Append(playlist.CreatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append('\n');
        if (playlist.Public) sb.Append(Prefix).Append("PUBLIC:1\n");
        if (!string.IsNullOrWhiteSpace(playlist.Comment)) sb.Append(Prefix).Append("COMMENT:").Append(OneLine(playlist.Comment)).Append('\n');

        foreach (var e in playlist.Entries)
        {
            sb.Append('\n');
            var entry = song(e.SongId);
            if (entry != null)
            {
                var seconds = (int)Math.Round(entry.DurationSeconds ?? -1);
                var label = entry.Artist is { Length: > 0 } a ? $"{a} - {entry.Title}" : entry.Title ?? "";
                sb.Append("#EXTINF:").Append(seconds.ToString(CultureInfo.InvariantCulture)).Append(',').Append(OneLine(label)).Append('\n');
            }

            sb.Append(Prefix).Append("SONG:").Append(e.SongId).Append('\n');
            // Not in the library: only the song ID is kept, so the entry comes back when the song does.
            if (entry != null) sb.Append("../").Append(entry.RelativePath.Replace('\\', '/')).Append('\n');
        }

        return sb.ToString();
    }

    public static Parsed Parse(string content)
    {
        string? name = null, comment = null;
        int? id = null;
        var isPublic = false;
        DateTimeOffset? created = null;
        var entries = new List<Entry>();
        string? pendingSong = null;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0) continue;

            if (line.StartsWith(Prefix, StringComparison.Ordinal))
            {
                var colon = line.IndexOf(':');
                if (colon < 0) continue;
                var key = line[Prefix.Length..colon];
                var value = line[(colon + 1)..].Trim();
                switch (key)
                {
                    case "ID": id = CatalogIds.ParsePlaylist(value); break;
                    case "COMMENT": comment = value; break;
                    case "PUBLIC": isPublic = value is "1" or "true"; break;
                    case "CREATED":
                        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var c)) created = c;
                        break;
                    case "SONG":
                        if (pendingSong != null) entries.Add(new Entry(pendingSong, null));
                        pendingSong = value.Length > 0 ? value : null;
                        break;
                }
            }
            else if (line.StartsWith("#PLAYLIST:", StringComparison.Ordinal))
                name = line["#PLAYLIST:".Length..].Trim();
            else if (!line.StartsWith('#'))
            {
                entries.Add(new Entry(pendingSong, line));
                pendingSong = null;
            }
        }

        if (pendingSong != null) entries.Add(new Entry(pendingSong, null));
        return new Parsed(name, id, comment, isPublic, created, entries);
    }

    public static string Hash(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    public static string Hash(string content) => Hash(Encoding.UTF8.GetBytes(content));

    private static string OneLine(string value) => value.ReplaceLineEndings(" ").Trim();
}
