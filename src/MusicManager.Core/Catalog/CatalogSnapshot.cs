using MusicManager.Core.Data;

namespace MusicManager.Core.Catalog;

/// <summary>Reference to an artist (ID and display name).</summary>
public sealed record ArtistRef(string Id, string Name);

/// <summary>Release date parts as far as known.</summary>
public sealed record PartialDate(int? Year, int? Month, int? Day)
{
    public static PartialDate? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Trim().Split('-');
        int? Part(int i, int max) => parts.Length > i && int.TryParse(parts[i], out var v) && v >= 1 && v <= max ? v : null;
        var year = Part(0, 9999);
        return year == null ? null : new PartialDate(year, Part(1, 12), Part(2, 31));
    }
}

public sealed class CatalogSong
{
    public required string Id { get; init; }
    public int EntryId { get; init; }
    public required string RelativePath { get; init; }
    public required string Title { get; init; }
    public required CatalogAlbum Album { get; init; }
    public string? DisplayArtist { get; init; }
    public List<ArtistRef> Artists { get; init; } = [];
    public string? DisplayAlbumArtist { get; init; }
    public List<ArtistRef> AlbumArtists { get; init; } = [];
    public List<string> Genres { get; init; } = [];
    public int? Year { get; init; }
    public int? TrackNumber { get; init; }
    public int? DiscNumber { get; init; }
    public int Duration { get; init; }
    public int? BitrateKbps { get; init; }
    public long Size { get; init; }
    public DateTime FileModifiedUtc { get; init; }
    public bool HasCover { get; init; }
    public string? MbRecordingId { get; init; }
    public string? MbTrackId { get; init; }
    public string? Isrc { get; init; }
    public DateTimeOffset AddedAt { get; init; }

    /// <summary>File extension without the dot, lower case.</summary>
    public string Suffix => Path.GetExtension(RelativePath).TrimStart('.').ToLowerInvariant();

    /// <summary>Own embedded cover, otherwise the album cover.</summary>
    public string? CoverArt => HasCover ? Id : Album.CoverArt;

    internal string SearchText { get; init; } = "";
}

public sealed class CatalogAlbum
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? DisplayArtist { get; init; }
    public List<ArtistRef> Artists { get; init; } = [];
    public int? Year { get; internal set; }
    public List<string> Genres { get; internal set; } = [];
    public List<string> ReleaseTypes { get; internal set; } = [];
    public PartialDate? OriginalReleaseDate { get; internal set; }
    public bool IsCompilation { get; internal set; }
    public string? MbReleaseId { get; init; }
    public DateTimeOffset Created { get; internal set; }
    public int Duration { get; internal set; }

    /// <summary>Songs in album order (disc, track, title).</summary>
    public List<CatalogSong> Songs { get; } = [];

    /// <summary>Song whose embedded cover is the album cover (null = no cover).</summary>
    public CatalogSong? CoverSong { get; internal set; }

    public string? CoverArt => CoverSong == null ? null : Id;
    public string? ArtistId => Artists.FirstOrDefault()?.Id;

    internal string SortKey { get; set; } = "";
    internal string SearchText { get; set; } = "";
}

public sealed class CatalogArtist
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? MbArtistId { get; init; }

    /// <summary>Albums with this artist as album artist.</summary>
    public List<CatalogAlbum> Albums { get; } = [];

    /// <summary>Albums with songs of this artist where it is not an album artist.</summary>
    public List<CatalogAlbum> AppearsOn { get; } = [];

    public bool IsAlbumArtist => Albums.Count > 0;

    internal string SortKey { get; init; } = "";
}

public sealed record CatalogGenre(string Name, int SongCount, int AlbumCount);

/// <summary>
/// Immutable view of the library index for the Subsonic API: songs, albums, artists and genres,
/// grouped and sorted once. All lists have a deterministic order (ties broken by ID), so paging
/// neither skips nor repeats items as long as the library does not change.
/// </summary>
public sealed class CatalogSnapshot
{
    public const string UnknownAlbum = "[Unknown Album]";
    public const string UnknownArtist = "[Unknown Artist]";

    public long Version { get; private init; }

    /// <summary>All songs, in album order (album artist, album, disc, track).</summary>
    public IReadOnlyList<CatalogSong> Songs { get; private init; } = [];

    /// <summary>All albums, by album artist and name.</summary>
    public IReadOnlyList<CatalogAlbum> Albums { get; private init; } = [];

    /// <summary>Artists that are album artist of at least one album, by name.</summary>
    public IReadOnlyList<CatalogArtist> AlbumArtists { get; private init; } = [];

    /// <summary>Every artist (album and track artists), by name.</summary>
    public IReadOnlyList<CatalogArtist> Artists { get; private init; } = [];

    public IReadOnlyList<CatalogGenre> Genres { get; private init; } = [];

    private Dictionary<string, CatalogSong> _songs = [];
    private Dictionary<string, CatalogAlbum> _albums = [];
    private Dictionary<string, CatalogArtist> _artists = [];

    public CatalogSong? Song(string id) => _songs.GetValueOrDefault(id);
    public CatalogAlbum? Album(string id) => _albums.GetValueOrDefault(id);
    public CatalogArtist? Artist(string id) => _artists.GetValueOrDefault(id);

    /// <summary>Case- and accent-insensitive search; every word of the query must occur.</summary>
    public static Func<string, bool> Matcher(string query)
    {
        var words = CatalogIds.NormalizeName(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return text => words.All(text.Contains);
    }

    public IEnumerable<CatalogArtist> SearchArtists(string query)
    {
        var match = Matcher(query);
        return Artists.Where(a => match(a.SortKey));
    }

    public IEnumerable<CatalogAlbum> SearchAlbums(string query)
    {
        var match = Matcher(query);
        return Albums.Where(a => match(a.SearchText));
    }

    public IEnumerable<CatalogSong> SearchSongs(string query)
    {
        var match = Matcher(query);
        return Songs.Where(s => match(s.SearchText));
    }

    public static CatalogSnapshot Build(IEnumerable<LibraryEntry> entries, long version)
    {
        var rows = entries.Where(e => e.SongId != null).OrderBy(e => e.SongId, StringComparer.Ordinal).ToList();
        var artists = new ArtistResolver(rows);

        var albums = new Dictionary<string, CatalogAlbum>(StringComparer.Ordinal);
        var songs = new List<CatalogSong>(rows.Count);
        foreach (var e in rows)
        {
            var trackArtists = artists.TrackArtists(e);
            var albumArtists = artists.AlbumArtists(e);
            var displayAlbumArtist = e.AlbumArtist ?? (albumArtists.Count > 0 ? string.Join(" & ", albumArtists.Select(a => a.Name)) : null);
            var albumId = CatalogIds.Album(e.MbReleaseId, displayAlbumArtist, e.Album);
            if (!albums.TryGetValue(albumId, out var album))
            {
                album = new CatalogAlbum
                {
                    Id = albumId, Name = e.Album ?? UnknownAlbum, DisplayArtist = displayAlbumArtist ?? UnknownArtist, Artists = albumArtists,
                    MbReleaseId = e.MbReleaseId,
                };
                albums[albumId] = album;
            }

            var displayArtist = e.Artist ?? (trackArtists.Count > 0 ? string.Join(" & ", trackArtists.Select(a => a.Name)) : null);
            var title = e.Title ?? Path.GetFileNameWithoutExtension(e.RelativePath);
            var song = new CatalogSong
            {
                Id = e.SongId!, EntryId = e.Id, RelativePath = e.RelativePath, Title = title, Album = album,
                DisplayArtist = displayArtist ?? UnknownArtist, Artists = trackArtists,
                DisplayAlbumArtist = displayAlbumArtist ?? UnknownArtist, AlbumArtists = albumArtists, Genres = e.Genres, Year = e.Year,
                TrackNumber = e.TrackNumber, DiscNumber = e.DiscNumber, Duration = (int)Math.Round(e.DurationSeconds ?? 0),
                BitrateKbps = e.BitrateKbps, Size = e.FileSize, FileModifiedUtc = e.FileModifiedUtc, HasCover = e.HasCover, MbRecordingId = e.MbRecordingId, MbTrackId = e.MbTrackId,
                Isrc = e.Isrc, AddedAt = e.AddedAt ?? new DateTimeOffset(DateTime.SpecifyKind(e.FileModifiedUtc, DateTimeKind.Utc)),
                SearchText = CatalogIds.NormalizeName($"{title} {displayArtist} {string.Join(" ", trackArtists.Select(a => a.Name))} {e.Album}"),
            };
            album.Songs.Add(song);
            songs.Add(song);

            // Album-level data from its songs.
            if (album.ReleaseTypes.Count == 0 && e.ReleaseTypes.Count > 0) album.ReleaseTypes = e.ReleaseTypes;
            album.OriginalReleaseDate ??= PartialDate.Parse(e.OriginalDate);
            album.IsCompilation |= e.IsCompilation;
        }

        foreach (var album in albums.Values)
        {
            album.Songs.Sort((a, b) =>
            {
                var c = (a.DiscNumber ?? 1).CompareTo(b.DiscNumber ?? 1);
                if (c == 0) c = (a.TrackNumber ?? int.MaxValue).CompareTo(b.TrackNumber ?? int.MaxValue);
                if (c == 0) c = string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
            });
            album.Year = album.Songs.Max(s => s.Year);
            album.Genres = album.Songs.SelectMany(s => s.Genres).GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            album.IsCompilation |= album.ReleaseTypes.Contains("compilation", StringComparer.OrdinalIgnoreCase);
            album.Created = album.Songs.Min(s => s.AddedAt);
            album.Duration = album.Songs.Sum(s => s.Duration);
            album.CoverSong = album.Songs.FirstOrDefault(s => s.HasCover);
            album.SortKey = $"{CatalogIds.NormalizeName(album.DisplayArtist)}\u001f{CatalogIds.NormalizeName(album.Name)}";
            album.SearchText = CatalogIds.NormalizeName($"{album.Name} {album.DisplayArtist}");
        }

        var albumList = albums.Values.OrderBy(a => a.SortKey, StringComparer.Ordinal).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();
        var songList = albumList.SelectMany(a => a.Songs).ToList();

        // Artists with their albums.
        var artistMap = artists.All.ToDictionary(a => a.Id, StringComparer.Ordinal);
        foreach (var album in albumList)
        {
            foreach (var a in album.Artists.DistinctBy(a => a.Id))
                artistMap[a.Id].Albums.Add(album);
            foreach (var a in album.Songs.SelectMany(s => s.Artists).DistinctBy(a => a.Id).Where(a => album.Artists.All(x => x.Id != a.Id)))
                artistMap[a.Id].AppearsOn.Add(album);
        }

        var artistList = artistMap.Values.OrderBy(a => a.SortKey, StringComparer.Ordinal).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();

        var genres = songList.SelectMany(s => s.Genres.Select(g => (Genre: g, Song: s)))
            .GroupBy(x => x.Genre, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CatalogGenre(g.First().Genre, g.Count(), g.Select(x => x.Song.Album.Id).Distinct().Count()))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CatalogSnapshot
        {
            Version = version,
            Songs = songList,
            Albums = albumList,
            Artists = artistList,
            AlbumArtists = artistList.Where(a => a.IsAlbumArtist).ToList(),
            Genres = genres,
            _songs = songList.ToDictionary(s => s.Id, StringComparer.Ordinal),
            _albums = albumList.ToDictionary(a => a.Id, StringComparer.Ordinal),
            _artists = artistMap,
        };
    }

    /// <summary>
    /// Maps artist names (with or without MusicBrainz ID) to one artist each. A name without an ID
    /// joins the artist with the most frequent ID for that name, so tagged and untagged tracks of
    /// the same artist end up together.
    /// </summary>
    private sealed class ArtistResolver
    {
        private readonly Dictionary<string, string> _mbIdByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, int>> _names = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> _mbIds = new(StringComparer.Ordinal);

        public ArtistResolver(IReadOnlyList<LibraryEntry> rows)
        {
            var counts = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            foreach (var e in rows)
            {
                foreach (var (name, mbId) in Pairs(TrackNames(e), e.MbArtistIds).Concat(Pairs(AlbumNames(e), AlbumIds(e))))
                {
                    if (mbId == null) continue;
                    var key = CatalogIds.NormalizeName(name);
                    if (!counts.TryGetValue(key, out var byId)) counts[key] = byId = new(StringComparer.Ordinal);
                    byId[mbId] = byId.GetValueOrDefault(mbId) + 1;
                }
            }

            foreach (var (key, byId) in counts)
                _mbIdByName[key] = byId.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).First().Key;
        }

        public IEnumerable<CatalogArtist> All => _names.Select(n => new CatalogArtist
        {
            Id = n.Key,
            Name = n.Value.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).First().Key,
            MbArtistId = _mbIds[n.Key],
            SortKey = CatalogIds.NormalizeName(n.Value.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).First().Key),
        });

        public List<ArtistRef> TrackArtists(LibraryEntry e) => Resolve(Pairs(TrackNames(e), e.MbArtistIds));

        public List<ArtistRef> AlbumArtists(LibraryEntry e) => Resolve(Pairs(AlbumNames(e), AlbumIds(e)));

        private List<ArtistRef> Resolve(IEnumerable<(string Name, string? MbId)> pairs)
        {
            var result = new List<ArtistRef>();
            foreach (var (name, given) in pairs)
            {
                var mbId = given ?? _mbIdByName.GetValueOrDefault(CatalogIds.NormalizeName(name));
                var id = CatalogIds.Artist(mbId, name);
                if (result.Any(r => r.Id == id)) continue;
                if (!_names.TryGetValue(id, out var spellings)) _names[id] = spellings = new(StringComparer.Ordinal);
                spellings[name] = spellings.GetValueOrDefault(name) + 1;
                _mbIds[id] = mbId?.ToLowerInvariant();
                result.Add(new ArtistRef(id, name));
            }

            return result;
        }

        private static List<string> TrackNames(LibraryEntry e) =>
            e.Artists.Count > 0 ? e.Artists : e.Artist is { Length: > 0 } a ? [a] : [];

        /// <summary>Album artists; tracks without one count as albums of their track artists.</summary>
        private static List<string> AlbumNames(LibraryEntry e) =>
            e.AlbumArtists.Count > 0 ? e.AlbumArtists : e.AlbumArtist is { Length: > 0 } a ? [a] : TrackNames(e);

        private static List<string> AlbumIds(LibraryEntry e) =>
            e.AlbumArtists.Count > 0 || e.AlbumArtist is { Length: > 0 } ? e.MbAlbumArtistIds : e.MbArtistIds;

        /// <summary>Names with the MusicBrainz ID at the same position (only when the lists line up).</summary>
        private static IEnumerable<(string Name, string? MbId)> Pairs(List<string> names, List<string> ids) =>
            names.Select((n, i) => (Name: n.Trim(), MbId: ids.Count <= names.Count && i < ids.Count && Guid.TryParse(ids[i], out _) ? ids[i] : null))
                .Where(x => x.Name.Length > 0);
    }
}
