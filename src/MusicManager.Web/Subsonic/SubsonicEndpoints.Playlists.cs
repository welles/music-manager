using MusicManager.Core.Catalog;
using MusicManager.Core.Data;

namespace MusicManager.Web.Subsonic;

// Playlists: getPlaylists, getPlaylist, createPlaylist, updatePlaylist, deletePlaylist. Smart playlists are read-only.
public static partial class SubsonicEndpoints
{
    private static void MapPlaylists()
    {
        // The playlists Music Manager keeps (smart) come first, then the ones made in the clients.
        Add("getPlaylists", async r =>
        {
            var catalog = await Catalog(r);
            var user = await UserData(r);
            var mapper = new SubsonicMapper(user);
            var now = DateTimeOffset.UtcNow;
            var playlists = await r.Service<PlaylistStore>().ListAsync(r.Aborted);
            var smart = SmartPlaylists.Every.Select(p => mapper.SmartPlaylist(p, SmartPlaylists.Songs(p, catalog, user), now, withEntries: false));
            return Ok("playlists", new { playlist = smart.Concat(playlists.Select(p => mapper.Playlist(p, catalog, withEntries: false))).ToList() });
        });

        Add("getPlaylist", async r =>
        {
            var id = r.Require("id");
            if (SmartPlaylists.Find(id) is { } smart)
            {
                var catalog = await Catalog(r);
                var user = await UserData(r);
                var songs = SmartPlaylists.Songs(smart, catalog, user);
                // Shows whether a client loads the playlist anew for each start (then a shuffled one is new each time).
                r.Service<ILoggerFactory>().CreateLogger("MusicManager.Web.Subsonic.Playlists")
                    .LogInformation("Smart playlist {Name} ({Count} songs) loaded by {Client}", smart.Name, songs.Count, r.Get("c") ?? "?");
                return Ok("playlist", new SubsonicMapper(user).SmartPlaylist(smart, songs, DateTimeOffset.UtcNow, withEntries: true));
            }

            var playlist = await r.Service<PlaylistStore>().GetAsync(PlaylistKey(id), r.Aborted) ?? throw PlaylistNotFound();
            return await PlaylistResultAsync(r, playlist);
        });

        // With playlistId the songs are replaced (and the name, if given); otherwise a new playlist.
        Add("createPlaylist", async r =>
        {
            var store = r.Service<PlaylistStore>();
            var songIds = SongIds(r, "songId");
            Playlist playlist;
            if (r.Get("playlistId") is { Length: > 0 } playlistId)
            {
                RefuseSmart(playlistId);
                var catalog = await Catalog(r);
                playlist = await store.ReplaceSongsAsync(PlaylistKey(playlistId), r.Get("name"), songIds, id => catalog.Song(id) != null, r.Aborted)
                           ?? throw PlaylistNotFound();
            }
            else
            {
                playlist = await store.CreateAsync(r.Require("name"), songIds, r.Aborted);
            }

            return await PlaylistResultAsync(r, playlist);
        });

        Add("updatePlaylist", async r =>
        {
            var key = PlaylistKey(RefuseSmart(r.Require("playlistId")));
            var catalog = await Catalog(r);
            var changes = new PlaylistChanges(r.Get("name"), r.Get("comment"), r.GetBool("public"), SongIds(r, "songIdToAdd"),
                r.GetInts("songIndexToRemove"));
            _ = await r.Service<PlaylistStore>().UpdateAsync(key, changes, id => catalog.Song(id) != null, r.Aborted) ?? throw PlaylistNotFound();
            return Ok();
        });

        Add("deletePlaylist", async r =>
        {
            if (!await r.Service<PlaylistStore>().DeleteAsync(PlaylistKey(RefuseSmart(r.Require("id"))), r.Aborted)) throw PlaylistNotFound();
            return Ok();
        });
    }

    private static async Task<IResult> PlaylistResultAsync(SubsonicRequest r, Playlist playlist)
    {
        var catalog = await Catalog(r);
        return Ok("playlist", (await Mapper(r)).Playlist(playlist, catalog, withEntries: true));
    }

    private static int PlaylistKey(string id) => CatalogIds.ParsePlaylist(id) ?? throw PlaylistNotFound();

    private static SubsonicException PlaylistNotFound() => SubsonicException.NotFound("Playlist");

    /// <summary>Smart playlists are kept by Music Manager; a client may read but not change or delete them.</summary>
    private static string RefuseSmart(string id) => SmartPlaylists.Find(id) is null
        ? id
        : throw new SubsonicException(SubsonicErrors.NotAuthorized, "This playlist is kept by Music Manager and cannot be changed");

    /// <summary>Song IDs of a repeated parameter. Songs that are not in the library are accepted (and hidden).</summary>
    private static List<string> SongIds(SubsonicRequest r, string name)
    {
        var ids = r.GetAll(name).Where(i => i.Length > 0).ToList();
        return ids.All(CatalogIds.IsSong) ? ids : throw SubsonicException.NotFound("Song");
    }
}
