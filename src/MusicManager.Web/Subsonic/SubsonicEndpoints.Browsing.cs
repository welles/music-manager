using MusicManager.Core.Catalog;

namespace MusicManager.Web.Subsonic;

// Browsing and search: artists, albums, songs, genres, album lists, search3.
public static partial class SubsonicEndpoints
{
    /// <summary>Upper limit for list sizes (getAlbumList2, getRandomSongs, getSongsByGenre).</summary>
    private const int MaxListSize = 500;

    /// <summary>Upper limit per type for search3 (Symfonium syncs in large pages).</summary>
    private const int MaxSearchCount = 5000;

    private static void MapBrowsing()
    {
        Add("getArtists", async r =>
        {
            var catalog = await Catalog(r);
            var mapper = await Mapper(r);
            var index = catalog.AlbumArtists
                .GroupBy(a => IndexName(a.Name))
                .Select(g => new { name = g.Key, artist = g.Select(a => mapper.Artist(a)).ToList() })
                .ToList();
            return Ok("artists", new { ignoredArticles = "", index });
        });

        Add("getArtist", async r =>
        {
            var catalog = await Catalog(r);
            var artist = catalog.Artist(r.Require("id")) ?? throw SubsonicException.NotFound("Artist");
            // Artists that are only track artists show the albums they appear on.
            var albums = artist.Albums.Count > 0 ? artist.Albums : artist.AppearsOn;
            return Ok("artist", (await Mapper(r)).Artist(artist, albums));
        });

        Add("getAlbum", async r =>
        {
            var catalog = await Catalog(r);
            var album = catalog.Album(r.Require("id")) ?? throw SubsonicException.NotFound("Album");
            return Ok("album", (await Mapper(r)).Album(album, withSongs: true));
        });

        Add("getSong", async r =>
        {
            var catalog = await Catalog(r);
            var song = catalog.Song(r.Require("id")) ?? throw SubsonicException.NotFound("Song");
            return Ok("song", (await Mapper(r)).Song(song));
        });

        Add("getGenres", async r =>
        {
            var catalog = await Catalog(r);
            return Ok("genres", new
            {
                genre = catalog.Genres.Select(g => new { value = g.Name, songCount = g.SongCount, albumCount = g.AlbumCount }).ToList(),
            });
        });

        Add("getAlbumList2", async r =>
        {
            var catalog = await Catalog(r);
            var type = r.Require("type");
            var size = r.GetInt("size", 10, 0, MaxListSize);
            var offset = r.GetInt("offset", 0);
            var user = await UserData(r);
            var albums = AlbumList(catalog, user, type, r);
            var mapper = new SubsonicMapper(user);
            return Ok("albumList2", new { album = albums.Skip(offset).Take(size).Select(a => mapper.Album(a)).ToList() });
        });

        Add("getRandomSongs", async r =>
        {
            var catalog = await Catalog(r);
            var size = r.GetInt("size", 10, 0, MaxListSize);
            var genre = r.Get("genre");
            var fromYear = r.GetInt("fromYear");
            var toYear = r.GetInt("toYear");
            var songs = catalog.Songs.Where(s =>
                (string.IsNullOrEmpty(genre) || s.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase))
                && (fromYear == null || s.Year >= fromYear) && (toYear == null || s.Year <= toYear)).ToArray();
            Random.Shared.Shuffle(songs);
            var mapper = await Mapper(r);
            return Ok("randomSongs", new { song = songs.Take(size).Select(mapper.Song).ToList() });
        });

        Add("getSongsByGenre", async r =>
        {
            var catalog = await Catalog(r);
            var genre = r.Require("genre");
            var count = r.GetInt("count", 10, 0, MaxListSize);
            var offset = r.GetInt("offset", 0);
            var mapper = await Mapper(r);
            var songs = catalog.Songs.Where(s => s.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase)).Skip(offset).Take(count);
            return Ok("songsByGenre", new { song = songs.Select(mapper.Song).ToList() });
        });

        Add("search3", async r =>
        {
            var catalog = await Catalog(r);
            var query = SearchQuery(r.Get("query"));
            IEnumerable<CatalogArtist> artists;
            IEnumerable<CatalogAlbum> albums;
            IEnumerable<CatalogSong> songs;
            if (query.Length == 0)
            {
                // Empty query: everything (full sync), in the catalog's stable order.
                (artists, albums, songs) = (catalog.AlbumArtists, catalog.Albums, catalog.Songs);
            }
            else
            {
                (artists, albums, songs) = (catalog.SearchArtists(query), catalog.SearchAlbums(query), catalog.SearchSongs(query));
            }

            var mapper = await Mapper(r);
            return Ok("searchResult3", new
            {
                artist = Page(artists, r, "artist").Select(a => mapper.Artist(a)).ToList(),
                album = Page(albums, r, "album").Select(a => mapper.Album(a)).ToList(),
                song = Page(songs, r, "song").Select(mapper.Song).ToList(),
            });
        });
    }

    private static Task<CatalogSnapshot> Catalog(SubsonicRequest r) => r.Service<MediaCatalog>().GetAsync(r.Aborted);

    private static Task<UserDataSnapshot> UserData(SubsonicRequest r) => r.Service<UserDataStore>().GetAsync(r.Aborted);

    private static async Task<SubsonicMapper> Mapper(SubsonicRequest r) => new(await UserData(r));

    /// <summary>Query without the quotes Symfonium sends for an empty search (<c>query=""</c>).</summary>
    internal static string SearchQuery(string? query)
    {
        var q = (query ?? "").Trim();
        if (q.Length >= 2 && q[0] == '"' && q[^1] == '"') q = q[1..^1].Trim();
        return q;
    }

    private static IEnumerable<T> Page<T>(IEnumerable<T> items, SubsonicRequest r, string prefix) =>
        items.Skip(r.GetInt($"{prefix}Offset", 0)).Take(r.GetInt($"{prefix}Count", 20, 0, MaxSearchCount));

    /// <summary>Index group of an artist: first letter (without accents), "#" for everything else.</summary>
    private static string IndexName(string name)
    {
        var key = CatalogIds.NormalizeName(name);
        return key.Length > 0 && char.IsLetter(key[0]) ? char.ToUpperInvariant(key[0]).ToString() : "#";
    }

    private static IEnumerable<CatalogAlbum> AlbumList(CatalogSnapshot catalog, UserDataSnapshot user, string type, SubsonicRequest r)
    {
        var albums = catalog.Albums;
        switch (type)
        {
            case "newest":
                return albums.OrderByDescending(a => a.Created).ThenBy(a => a.Id, StringComparer.Ordinal);
            case "alphabeticalByName":
                return albums.OrderBy(a => CatalogIds.NormalizeName(a.Name), StringComparer.Ordinal).ThenBy(a => a.Id, StringComparer.Ordinal);
            case "alphabeticalByArtist":
                // The catalog order: album artist, then album name.
                return albums;
            case "random":
                var shuffled = albums.ToArray();
                Random.Shared.Shuffle(shuffled);
                return shuffled;
            case "byYear":
                var from = r.RequireInt("fromYear");
                var to = r.RequireInt("toYear");
                var inRange = albums.Where(a => a.Year >= Math.Min(from, to) && a.Year <= Math.Max(from, to));
                return from <= to
                    ? inRange.OrderBy(a => a.Year).ThenBy(a => a.Id, StringComparer.Ordinal)
                    : inRange.OrderByDescending(a => a.Year).ThenBy(a => a.Id, StringComparer.Ordinal);
            case "byGenre":
                var genre = r.Require("genre");
                return albums.Where(a => a.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase));
            case "frequent":
                // Album plays are derived from its songs.
                return albums.Select(a => (Album: a, Plays: user.Plays(a))).Where(x => x.Plays.Count > 0)
                    .OrderByDescending(x => x.Plays.Count).ThenByDescending(x => x.Plays.LastPlayed).ThenBy(x => x.Album.Id, StringComparer.Ordinal)
                    .Select(x => x.Album);
            case "recent":
                return albums.Select(a => (Album: a, user.Plays(a).LastPlayed)).Where(x => x.LastPlayed != null)
                    .OrderByDescending(x => x.LastPlayed).ThenBy(x => x.Album.Id, StringComparer.Ordinal)
                    .Select(x => x.Album);
            case "starred":
                return albums.Select(a => (Album: a, Starred: user.Starred(a.Id))).Where(x => x.Starred != null)
                    .OrderByDescending(x => x.Starred).ThenBy(x => x.Album.Id, StringComparer.Ordinal)
                    .Select(x => x.Album);
            case "highest":
                // OrderBy is stable: equal ratings stay in catalog order.
                return albums.Select(a => (Album: a, Rating: user.Rating(a.Id))).Where(x => x.Rating != null)
                    .OrderByDescending(x => x.Rating)
                    .Select(x => x.Album);
            default:
                throw new SubsonicException(SubsonicErrors.Generic, $"Unknown list type: {type}");
        }
    }
}
