using MusicManager.Core.Data;
using MusicManager.Core.Model;
using MusicManager.Core.Pipeline;

namespace MusicManager.Web.Components.Shared;

/// <summary>
/// What <see cref="SongCard"/> shows for one song, whatever it comes from: a file's tags, a library track,
/// a MusicBrainz candidate or a Spotify/Deezer match. Every place that shows a song builds one of these.
/// </summary>
public sealed record SongInfo
{
    public string? Title { get; init; }
    public string? Artist { get; init; }

    /// <summary>Individual artists; with <see cref="LinkArtists"/> each one links to its library page.</summary>
    public IReadOnlyList<string> Artists { get; init; } = [];

    public bool LinkArtists { get; init; }

    public string? Album { get; init; }

    /// <summary>Link of the album (library page or the release in the source).</summary>
    public string? AlbumHref { get; init; }

    public bool AlbumHrefExternal { get; init; }

    /// <summary>Shown only when it differs from the artist.</summary>
    public string? AlbumArtist { get; init; }

    public string? Date { get; init; }
    public string? Country { get; init; }

    /// <summary>Release type, e.g. "Album + Compilation · Official · CD".</summary>
    public string? Type { get; init; }

    public string? Track { get; init; }
    public string? Duration { get; init; }
    public string? Label { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public string? Isrc { get; init; }

    /// <summary>Codec and bitrate of the file.</summary>
    public string? Format { get; init; }

    public string? Path { get; init; }

    public string? CoverUrl { get; init; }

    /// <summary>Tried in order when the cover (or the previous fallback) cannot be loaded.</summary>
    public IReadOnlyList<string> CoverFallbacks { get; init; } = [];

    /// <summary>Additional labelled fields of one place (e.g. how a candidate was recognised).</summary>
    public IReadOnlyList<(string Label, string Value)> Extra { get; init; } = [];

    public string? ArtistLine => Artists.Count > 0 ? string.Join(", ", Artists) : Artist;

    public static string? FormatLength(double? seconds) =>
        seconds is > 0 and var s ? TimeSpan.FromSeconds(s).ToString(s >= 3600 ? @"h\:mm\:ss" : @"m\:ss") : null;

    public static string? FormatTrack(int? number, int? count) =>
        number == null ? null : count is > 0 ? $"{number}/{count}" : number.ToString();

    public static string? FormatAudio(string? codec, int? bitrateKbps) =>
        Join(" · ", codec, bitrateKbps != null ? $"{bitrateKbps} kbps" : null);

    private static string? Join(string separator, params string?[] parts)
    {
        var text = string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return text.Length == 0 ? null : text;
    }

    private static string? ReleaseUrl(string? mbReleaseId) =>
        mbReleaseId == null ? null : $"https://musicbrainz.org/release/{mbReleaseId}";

    public static SongInfo From(TagSet? t) => t == null ? new SongInfo() : new SongInfo
    {
        Title = t.Title,
        Artist = t.Artist,
        Artists = t.Artists,
        Album = t.Album,
        AlbumHref = ReleaseUrl(t.MbReleaseId),
        AlbumHrefExternal = true,
        AlbumArtist = t.AlbumArtist,
        Date = t.Date,
        Country = t.ReleaseCountry,
        Type = Join(" · ", t.ReleaseType, t.ReleaseStatus),
        Track = FormatTrack(t.TrackNumber, t.TrackCount),
        Label = t.Label,
        Genres = t.Genres,
        Isrc = t.Isrc,
    };

    public static SongInfo From(LibraryTrack t) => new()
    {
        Title = t.Title ?? System.IO.Path.GetFileName(t.RelativePath),
        Artist = t.Artist,
        Artists = t.ArtistNames,
        LinkArtists = true,
        Album = t.Album,
        AlbumHref = t.Album == null ? null : TrackGrid.AlbumHref(t),
        AlbumArtist = t.AlbumArtist,
        Date = t.Year?.ToString(),
        Track = FormatTrack(t.TrackNumber, null),
        Duration = FormatLength(t.DurationSeconds),
        Genres = t.Genres,
        Format = FormatAudio(t.Codec, t.BitrateKbps),
        Path = t.RelativePath,
        CoverUrl = $"api/library/{t.Id}/cover",
    };

    public static SongInfo From(LibraryEntry e) => new()
    {
        Title = e.Title ?? System.IO.Path.GetFileName(e.RelativePath),
        Artist = e.Artist,
        Album = e.Album,
        AlbumHref = ReleaseUrl(e.MbReleaseId),
        AlbumHrefExternal = true,
        AlbumArtist = e.AlbumArtist,
        Date = e.Date,
        Duration = FormatLength(e.DurationSeconds),
        Isrc = e.Isrc,
        Format = FormatAudio(e.Codec, e.BitrateKbps),
        Path = e.RelativePath,
        CoverUrl = $"api/library/{e.Id}/cover",
    };

    public static SongInfo From(DuplicateMatch m) => new()
    {
        Title = m.Title ?? System.IO.Path.GetFileName(m.RelativePath),
        Artist = m.Artist,
        Album = m.Album,
        AlbumHref = ReleaseUrl(m.MbReleaseId),
        AlbumHrefExternal = true,
        Date = m.Date,
        Duration = FormatLength(m.DurationSeconds),
        Format = FormatAudio(m.Codec, m.BitrateKbps),
        Path = m.RelativePath,
        CoverUrl = $"api/library/{m.LibraryEntryId}/cover",
    };

    /// <summary>A MusicBrainz candidate; the cover comes from the Cover Art Archive (release, then release group).</summary>
    public static SongInfo From(ReleaseCandidate c, double? fileSeconds = null)
    {
        var r = c.Release;
        return new SongInfo
        {
            Title = c.RecordingTitle,
            Artist = c.RecordingArtist,
            Album = r.Title,
            AlbumHref = ReleaseUrl(r.ReleaseId),
            AlbumHrefExternal = true,
            AlbumArtist = r.ArtistString,
            Date = r.Date,
            Country = r.Country,
            Type = Join(" · ",
                Join(" + ", [r.PrimaryType, .. r.SecondaryTypes]),
                r.Status,
                Join(", ", [.. r.Formats])),
            Track = FormatTrack(r.TrackPosition, r.TrackCount),
            Duration = WithFileLength(c.RecordingLengthSeconds, fileSeconds),
            CoverUrl = $"https://coverartarchive.org/release/{r.ReleaseId}/front-250",
            CoverFallbacks = r.ReleaseGroupId == null ? [] : [$"https://coverartarchive.org/release-group/{r.ReleaseGroupId}/front-250"],
            Extra = [("Erkennung", $"{MatchSourceName(c.MatchSource)} {c.MatchScore:P0}")],
        };
    }

    public static SongInfo From(ExternalMatch m, double? fileSeconds = null) => new()
    {
        Title = m.Title,
        Artist = string.Join(", ", m.Artists) + (m.FeaturedArtists.Count > 0 ? $" feat. {string.Join(", ", m.FeaturedArtists)}" : ""),
        Album = m.Album,
        AlbumHref = m.Url,
        AlbumHrefExternal = true,
        AlbumArtist = m.AlbumArtists.Count > 0 ? string.Join(", ", m.AlbumArtists) : null,
        Date = m.ReleaseDate,
        Type = m.AlbumType,
        Track = FormatTrack(m.TrackNumber, m.TrackCount),
        Duration = WithFileLength(m.DurationSeconds, fileSeconds),
        Label = m.Label,
        Genres = m.Genres,
        Isrc = m.Isrc,
        CoverUrl = m.CoverUrl,
    };

    /// <summary>"1:37 (Datei 1:39)": the length in the source next to the length of the file it is compared with.</summary>
    private static string? WithFileLength(double? seconds, double? fileSeconds)
    {
        var length = FormatLength(seconds);
        var file = FormatLength(fileSeconds);
        return file == null ? length : $"{length ?? "–"} (Datei {file})";
    }

    private static string MatchSourceName(string source) => source switch
    {
        "acoustid" => "AcoustID",
        "search" => "Textsuche",
        "manual" => "manuell",
        _ => source,
    };
}
