using Microsoft.EntityFrameworkCore;
using MusicManager.Core.Data;

namespace MusicManager.Core.Pipeline;

/// <summary>A library file for browsing (index data without the fingerprint).</summary>
public sealed class LibraryTrack
{
    public int Id { get; init; }
    public required string RelativePath { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public List<string> Artists { get; init; } = [];
    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }
    public List<string> AlbumArtists { get; init; } = [];
    public List<string> Genres { get; init; } = [];
    public int? Year { get; init; }
    public int? TrackNumber { get; init; }
    public int? DiscNumber { get; init; }
    public double? DurationSeconds { get; init; }
    public string? Codec { get; init; }
    public int? BitrateKbps { get; init; }
    public long FileSize { get; init; }
    public bool HasCover { get; init; }
    public string? MbRecordingId { get; init; }
    public string? MbReleaseId { get; init; }
    public string? TagSource { get; init; }
    public string? GenreOrigin { get; init; }
    public string? RulesVersion { get; init; }
    public List<string> MbArtistIds { get; init; } = [];
    public List<string> MbAlbumArtistIds { get; init; } = [];

    public List<string> ArtistNames => Artists.Count > 0 ? Artists : Artist == null ? [] : [Artist];
    public string? MainAlbumArtist => AlbumArtists.FirstOrDefault() ?? AlbumArtist ?? ArtistNames.FirstOrDefault();
    public string AlbumKey => $"{MainAlbumArtist}\u001f{Album}";

    public List<LibraryProblem> Problems(string rulesVersion)
    {
        var p = new List<LibraryProblem>();
        if (TagSource == null) p.Add(LibraryProblem.NotFromThisTool);
        else if (RulesVersion != rulesVersion) p.Add(LibraryProblem.OldRules);
        if (MbRecordingId == null) p.Add(LibraryProblem.NoMusicBrainz);
        if (!HasCover) p.Add(LibraryProblem.NoCover);
        if (Genres.Count == 0) p.Add(LibraryProblem.NoGenre);
        else if (Model.GenreOrigin.Parse(GenreOrigin)?.Confidence is Model.GenreConfidence.Low or Model.GenreConfidence.VeryLow)
            p.Add(LibraryProblem.WeakGenre);
        if (BitrateKbps is < 192) p.Add(LibraryProblem.LowBitrate);
        return p;
    }
}

public enum LibraryProblem
{
    NotFromThisTool,
    OldRules,
    NoMusicBrainz,
    NoCover,
    NoGenre,

    /// <summary>Genres from a weak source (artist tags, AI suggestion).</summary>
    WeakGenre,
    LowBitrate,
}

public sealed record ArtistSummary(string Name, string? MbArtistId, int Albums, int Tracks);

public sealed record AlbumSummary(string Key, string Album, string? AlbumArtist, int? Year, int Tracks, int CoverTrackId);

public sealed record GenreSummary(string Genre, int Tracks, int Artists);

/// <summary>Browsing views over the library index: artists, albums, genres, tracks.</summary>
public sealed class LibraryCatalog(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<List<LibraryTrack>> GetTracksAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Library.AsNoTracking().Select(e => new LibraryTrack
        {
            Id = e.Id, RelativePath = e.RelativePath, Title = e.Title, Artist = e.Artist, Artists = e.Artists, Album = e.Album,
            AlbumArtist = e.AlbumArtist, AlbumArtists = e.AlbumArtists, Genres = e.Genres, Year = e.Year, TrackNumber = e.TrackNumber,
            DiscNumber = e.DiscNumber, DurationSeconds = e.DurationSeconds, Codec = e.Codec, BitrateKbps = e.BitrateKbps,
            FileSize = e.FileSize, HasCover = e.HasCover, MbRecordingId = e.MbRecordingId, MbReleaseId = e.MbReleaseId,
            TagSource = e.TagSource, GenreOrigin = e.GenreOrigin, RulesVersion = e.RulesVersion, MbArtistIds = e.MbArtistIds, MbAlbumArtistIds = e.MbAlbumArtistIds,
        }).ToListAsync(ct);
    }

    /// <summary>Every individual artist (track and album artists) with album and track counts.</summary>
    public static List<ArtistSummary> Artists(IEnumerable<LibraryTrack> tracks)
    {
        var map = new Dictionary<string, (string? MbId, HashSet<string> Albums, int Tracks)>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tracks)
        {
            var names = t.ArtistNames.Select((n, i) => (n, Id: t.MbArtistIds.ElementAtOrDefault(i)))
                .Concat(t.AlbumArtists.Select((n, i) => (n, Id: t.MbAlbumArtistIds.ElementAtOrDefault(i))))
                .DistinctBy(x => x.n, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, id) in names)
            {
                var entry = map.TryGetValue(name, out var existing) ? existing : (MbId: id, Albums: new HashSet<string>(), Tracks: 0);
                if (t.Album != null) entry.Albums.Add(t.AlbumKey);
                map[name] = (entry.MbId ?? id, entry.Albums, entry.Tracks + 1);
            }
        }

        return map.Select(m => new ArtistSummary(m.Key, m.Value.MbId, m.Value.Albums.Count, m.Value.Tracks))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<AlbumSummary> Albums(IEnumerable<LibraryTrack> tracks) =>
        tracks.Where(t => t.Album != null).GroupBy(t => t.AlbumKey)
            .Select(g => new AlbumSummary(g.Key, g.First().Album!, g.First().MainAlbumArtist, g.Max(t => t.Year), g.Count(),
                (g.FirstOrDefault(t => t.HasCover) ?? g.First()).Id))
            .OrderBy(a => a.AlbumArtist, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Year).ThenBy(a => a.Album)
            .ToList();

    public static List<GenreSummary> Genres(IEnumerable<LibraryTrack> tracks) =>
        tracks.SelectMany(t => t.Genres.Select(g => (Genre: g, Track: t)))
            .GroupBy(x => x.Genre, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GenreSummary(g.Key, g.Count(), g.SelectMany(x => x.Track.ArtistNames).Distinct(StringComparer.OrdinalIgnoreCase).Count()))
            .OrderByDescending(g => g.Tracks).ToList();

    public static bool HasArtist(LibraryTrack t, string name) =>
        t.ArtistNames.Contains(name, StringComparer.OrdinalIgnoreCase) || t.AlbumArtists.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<LibraryTrack> AlbumOrder(IEnumerable<LibraryTrack> tracks) =>
        tracks.OrderBy(t => t.DiscNumber ?? 1).ThenBy(t => t.TrackNumber ?? 999).ThenBy(t => t.Title);
}
