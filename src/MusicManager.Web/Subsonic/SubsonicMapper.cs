using System.Globalization;
using Microsoft.AspNetCore.StaticFiles;
using MusicManager.Core.Catalog;

namespace MusicManager.Web.Subsonic;

/// <summary>
/// Maps catalog items to API objects. Created per request: user data (stars, ratings, play counts)
/// belongs here, so every endpoint returns it the same way.
/// </summary>
public sealed class SubsonicMapper
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new()
    {
        Mappings = { [".m4a"] = "audio/mp4", [".opus"] = "audio/ogg", [".flac"] = "audio/flac", [".ogg"] = "audio/ogg", [".wma"] = "audio/x-ms-wma" },
    };

    public static string ContentType(string path) =>
        ContentTypes.TryGetContentType(path, out var type) ? type : "application/octet-stream";

    public static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static List<ArtistRefDto> Refs(IEnumerable<ArtistRef> artists) => artists.Select(a => new ArtistRefDto(a.Id, a.Name)).ToList();

    public SongDto Song(CatalogSong s) => new()
    {
        Id = s.Id,
        Parent = s.Album.Id,
        Title = s.Title,
        Album = s.Album.Name,
        Artist = s.DisplayArtist,
        Track = s.TrackNumber,
        Year = s.Year,
        Genre = s.Genres.FirstOrDefault(),
        CoverArt = s.CoverArt,
        Size = s.Size,
        ContentType = ContentType(s.RelativePath),
        Suffix = s.Suffix,
        Duration = s.Duration,
        BitRate = s.BitrateKbps,
        Path = s.RelativePath,
        DiscNumber = s.DiscNumber,
        Created = Iso(s.AddedAt),
        AlbumId = s.Album.Id,
        ArtistId = s.Artists.FirstOrDefault()?.Id,
        MusicBrainzId = s.MbRecordingId ?? "",
        Isrc = s.Isrc == null ? [] : [s.Isrc],
        Genres = s.Genres.Select(g => new GenreRefDto(g)).ToList(),
        Artists = Refs(s.Artists),
        DisplayArtist = s.DisplayArtist ?? "",
        AlbumArtists = Refs(s.AlbumArtists),
        DisplayAlbumArtist = s.DisplayAlbumArtist ?? "",
    };

    public AlbumDto Album(CatalogAlbum a, bool withSongs = false) => new()
    {
        Id = a.Id,
        Name = a.Name,
        Artist = a.DisplayArtist,
        ArtistId = a.ArtistId,
        CoverArt = a.CoverArt,
        SongCount = a.Songs.Count,
        Duration = a.Duration,
        Created = Iso(a.Created),
        Year = a.Year,
        Genre = a.Genres.FirstOrDefault(),
        MusicBrainzId = a.MbReleaseId ?? "",
        Genres = a.Genres.Select(g => new GenreRefDto(g)).ToList(),
        Artists = Refs(a.Artists),
        DisplayArtist = a.DisplayArtist ?? "",
        ReleaseTypes = a.ReleaseTypes,
        OriginalReleaseDate = a.OriginalReleaseDate is { } d ? new ItemDateDto { Year = d.Year, Month = d.Month, Day = d.Day } : new ItemDateDto(),
        IsCompilation = a.IsCompilation,
        Song = withSongs ? a.Songs.Select(Song).ToList() : null,
    };

    /// <summary>Artist; <paramref name="albums"/> are listed for getArtist.</summary>
    public ArtistDto Artist(CatalogArtist a, IReadOnlyList<CatalogAlbum>? albums = null) => new()
    {
        Id = a.Id,
        Name = a.Name,
        CoverArt = a.Id,
        AlbumCount = albums?.Count ?? a.Albums.Count,
        MusicBrainzId = a.MbArtistId ?? "",
        Album = albums?.Select(x => Album(x)).ToList(),
    };
}
