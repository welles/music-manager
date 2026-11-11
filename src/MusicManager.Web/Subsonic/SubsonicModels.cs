using System.Text.Json;
using System.Text.Json.Serialization;

namespace MusicManager.Web.Subsonic;

// Response objects of the OpenSubsonic API (JSON only). Null properties are left out; OpenSubsonic
// fields the server supports are always sent (empty when there is no data), as the spec asks.

public sealed class SubsonicError
{
    public int Code { get; init; }
    public string? Message { get; init; }
}

public sealed record ArtistRefDto(string Id, string Name);

public sealed record GenreRefDto(string Name);

public sealed class ItemDateDto
{
    public int? Year { get; init; }
    public int? Month { get; init; }
    public int? Day { get; init; }
}

/// <summary>Child (song).</summary>
public sealed class SongDto
{
    public required string Id { get; init; }
    public string? Parent { get; init; }
    public bool IsDir => false;
    public required string Title { get; init; }
    public string? Album { get; init; }
    public string? Artist { get; init; }
    public int? Track { get; init; }
    public int? Year { get; init; }
    public string? Genre { get; init; }
    public string? CoverArt { get; init; }
    public long Size { get; init; }
    public string? ContentType { get; init; }
    public string? Suffix { get; init; }
    public int Duration { get; init; }
    public int? BitRate { get; init; }
    public string? Path { get; init; }
    public bool IsVideo => false;
    public int? DiscNumber { get; init; }
    public string? Created { get; init; }
    public string? AlbumId { get; init; }
    public string? ArtistId { get; init; }
    public string Type => "music";
    public string MediaType => "song";
    public string MusicBrainzId { get; init; } = "";
    public List<string> Isrc { get; init; } = [];
    public List<GenreRefDto> Genres { get; init; } = [];
    public List<ArtistRefDto> Artists { get; init; } = [];
    public string DisplayArtist { get; init; } = "";
    public List<ArtistRefDto> AlbumArtists { get; init; } = [];
    public string DisplayAlbumArtist { get; init; } = "";
    public string? Starred { get; init; }
    public int? UserRating { get; init; }
    public long PlayCount { get; init; }
    public string? Played { get; init; }
}

/// <summary>AlbumID3 (with <see cref="Song"/> for getAlbum).</summary>
public sealed class AlbumDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Artist { get; init; }
    public string? ArtistId { get; init; }
    public string? CoverArt { get; init; }
    public int SongCount { get; init; }
    public int Duration { get; init; }
    public required string Created { get; init; }
    public int? Year { get; init; }
    public string? Genre { get; init; }
    public string MusicBrainzId { get; init; } = "";
    public List<GenreRefDto> Genres { get; init; } = [];
    public List<ArtistRefDto> Artists { get; init; } = [];
    public string DisplayArtist { get; init; } = "";
    public List<string> ReleaseTypes { get; init; } = [];
    public ItemDateDto OriginalReleaseDate { get; init; } = new();
    public bool IsCompilation { get; init; }
    public string? Starred { get; init; }
    public int? UserRating { get; init; }
    public long PlayCount { get; init; }
    public string? Played { get; init; }
    public List<SongDto>? Song { get; init; }
}

/// <summary>ArtistID3 (with <see cref="Album"/> for getArtist).</summary>
public sealed class ArtistDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? CoverArt { get; init; }
    public int AlbumCount { get; init; }
    public string MusicBrainzId { get; init; } = "";
    public string? Starred { get; init; }
    public int? UserRating { get; init; }
    public List<AlbumDto>? Album { get; init; }
}

/// <summary>Playlist (with <see cref="Entry"/> for getPlaylist, createPlaylist).</summary>
public sealed class PlaylistDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Comment { get; init; }
    public required string Owner { get; init; }
    public bool Public { get; init; }
    public int SongCount { get; init; }
    public int Duration { get; init; }
    public required string Created { get; init; }
    public required string Changed { get; init; }
    public string? CoverArt { get; init; }
    public List<SongDto>? Entry { get; init; }
}

public static class SubsonicJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
