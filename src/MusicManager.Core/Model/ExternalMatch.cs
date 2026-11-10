namespace MusicManager.Core.Model;

/// <summary>A track found in a source outside MusicBrainz (Spotify, Deezer).</summary>
public sealed class ExternalMatch
{
    /// <summary>"spotify" or "deezer".</summary>
    public required string Source { get; init; }

    public required string Id { get; init; }
    public string? Url { get; init; }

    public required string Title { get; init; }

    /// <summary>Main artists of the track.</summary>
    public List<string> Artists { get; init; } = [];

    /// <summary>Featured artists, where the source distinguishes them (Deezer).</summary>
    public List<string> FeaturedArtists { get; init; } = [];

    public string? Album { get; init; }
    public List<string> AlbumArtists { get; init; } = [];

    /// <summary>"album", "single", "ep", "compilation".</summary>
    public string? AlbumType { get; init; }

    public string? ReleaseDate { get; init; }
    public int? TrackNumber { get; init; }
    public int? TrackCount { get; init; }
    public int? DiscNumber { get; init; }
    public double? DurationSeconds { get; init; }
    public string? Isrc { get; init; }
    public string? Label { get; init; }
    public List<string> Genres { get; init; } = [];
    public string? CoverUrl { get; init; }

    /// <summary>True if the track came from a Spotify/Deezer link in the file's tags (exact match).</summary>
    public bool FromTagLink { get; set; }

    /// <summary>Similarity to the file (title, artist, duration), 0..1.</summary>
    public double Score { get; set; }

    public string Key => $"{Source}:{Id}";
}
