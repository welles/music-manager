namespace MusicManager.Core.Model;

/// <summary>
/// The metadata of one file, used for old tags, cleaned tags and the final tags.
/// </summary>
public sealed class TagSet
{
    public string? Title { get; set; }

    /// <summary>Display artist string, e.g. "A feat. B".</summary>
    public string? Artist { get; set; }

    /// <summary>Individual artists (multi-value tag ARTISTS).</summary>
    public List<string> Artists { get; set; } = [];

    public string? Album { get; set; }

    /// <summary>Display album artist string.</summary>
    public string? AlbumArtist { get; set; }

    /// <summary>Main album artist, used for the folder name (not written as a tag).</summary>
    public string? FolderArtist { get; set; }

    /// <summary>Individual album artists (multi-value tag ALBUMARTISTS).</summary>
    public List<string> AlbumArtists { get; set; } = [];

    public int? TrackNumber { get; set; }
    public int? TrackCount { get; set; }
    public int? DiscNumber { get; set; }
    public int? DiscCount { get; set; }

    /// <summary>Release date (YYYY, YYYY-MM or YYYY-MM-DD).</summary>
    public string? Date { get; set; }

    public string? OriginalDate { get; set; }

    public List<string> Genres { get; set; } = [];

    /// <summary>Where the genres come from and how reliable they are (<see cref="Model.GenreOrigin"/>, "source|confidence").</summary>
    public string? GenreOrigin { get; set; }

    public string? Remixer { get; set; }
    public string? Isrc { get; set; }
    public string? Label { get; set; }
    public string? ReleaseCountry { get; set; }
    public string? ReleaseStatus { get; set; }
    public string? ReleaseType { get; set; }

    /// <summary>AcoustID track ID (tag "Acoustid Id").</summary>
    public string? AcoustId { get; set; }

    public string? MbRecordingId { get; set; }
    public string? MbTrackId { get; set; }
    public string? MbReleaseId { get; set; }
    public string? MbReleaseGroupId { get; set; }
    public List<string> MbArtistIds { get; set; } = [];
    public List<string> MbAlbumArtistIds { get; set; } = [];

    /// <summary>Other tags read from the file (lower-case keys), e.g. "show" for MusicFab files.</summary>
    public Dictionary<string, string> Extra { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int? Year => Date is { Length: >= 4 } && int.TryParse(Date[..4], out var y) ? y : null;

    public TagSet Clone()
    {
        var c = (TagSet)MemberwiseClone();
        c.Artists = [.. Artists];
        c.AlbumArtists = [.. AlbumArtists];
        c.Genres = [.. Genres];
        c.MbArtistIds = [.. MbArtistIds];
        c.MbAlbumArtistIds = [.. MbAlbumArtistIds];
        c.Extra = new Dictionary<string, string>(Extra, StringComparer.OrdinalIgnoreCase);
        return c;
    }

    /// <summary>First artist from the list, falling back to the display string.</summary>
    public string? PrimaryArtist => Artists.FirstOrDefault() ?? Artist;
}
