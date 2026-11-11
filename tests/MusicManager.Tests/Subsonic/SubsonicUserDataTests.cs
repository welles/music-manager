using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicManager.Core.Catalog;
using MusicManager.Core.Data;
using MusicManager.Core.Model;
using MusicManager.Core.Pipeline;

namespace MusicManager.Tests.Subsonic;

/// <summary>Stars, ratings, scrobbles and album lists; every test gets its own app and database.</summary>
public class SubsonicAnnotationTests
{
    private const string ReleaseA = "rel-a";
    private static readonly string AlbumA = CatalogIds.Album(ReleaseA, null, null);
    private static readonly string AlbumB = CatalogIds.Album(null, "Artist", "Album B");
    private static readonly string Artist = CatalogIds.Artist(null, "Artist");

    /// <summary>Album A (tr-a1..a3, with release ID) and album B (tr-b1..b3, without).</summary>
    private static async Task<SubsonicTestHost> StartAsync()
    {
        var app = new SubsonicTestHost();
        var entries = new List<LibraryEntry>();
        foreach (var (album, prefix, release) in new[] { ("Album A", "a", ReleaseA), ("Album B", "b", null) })
        {
            for (var t = 1; t <= 3; t++)
            {
                entries.Add(new LibraryEntry
                {
                    RelativePath = $"Artist/{album}/{t}.m4a", SongId = $"tr-{prefix}{t}", Title = $"{album} {t}", Artist = "Artist", Artists = ["Artist"],
                    AlbumArtist = "Artist", AlbumArtists = ["Artist"], Album = album, MbReleaseId = release, TrackNumber = t, DurationSeconds = 100,
                    FileSize = 1000, AddedAt = DateTimeOffset.UtcNow,
                });
            }
        }

        await app.SeedAsync(entries);
        return app;
    }

    private static async Task Ok(SubsonicTestHost app, string endpoint, string query)
    {
        var r = await app.CallAsync(endpoint, query);
        Assert.True(r.GetProperty("status").GetString() == "ok", r.ToString());
    }

    private static int ErrorCode(JsonElement r) => r.GetProperty("error").GetProperty("code").GetInt32();

    private static async Task<JsonElement> SongAsync(SubsonicTestHost app, string id) =>
        (await app.CallAsync("getSong", $"id={id}")).GetProperty("song");

    private static async Task<JsonElement> AlbumAsync(SubsonicTestHost app, string id) =>
        (await app.CallAsync("getAlbum", $"id={id}")).GetProperty("album");

    private static async Task<List<string>> AlbumListAsync(SubsonicTestHost app, string type) =>
        (await app.CallAsync("getAlbumList2", $"type={type}&size=500")).GetProperty("albumList2").GetProperty("album").EnumerateArray()
        .Select(a => a.GetProperty("id").GetString()!).ToList();

    private static List<string> Ids(JsonElement parent, string key) =>
        parent.GetProperty(key).EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToList();

    [Fact]
    public async Task StarAndUnstarRoundTrip()
    {
        using var app = await StartAsync();
        // Album ID in "id" is recognized by its prefix.
        await Ok(app, "star", $"id=tr-a1&id=tr-b2&id={AlbumB}&albumId={AlbumA}&artistId={Artist}");

        var starred = (await app.CallAsync("getStarred2")).GetProperty("starred2");
        Assert.Equal(["tr-a1", "tr-b2"], Ids(starred, "song").Order());
        Assert.Equal(new[] { AlbumA, AlbumB }.Order(), Ids(starred, "album").Order());
        Assert.Equal([Artist], Ids(starred, "artist"));
        Assert.All(starred.GetProperty("song").EnumerateArray(), s => Assert.True(s.TryGetProperty("starred", out _)));

        var song = await SongAsync(app, "tr-a1");
        Assert.True(DateTimeOffset.TryParse(song.GetProperty("starred").GetString(), out var at));
        Assert.True(at > DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.False((await SongAsync(app, "tr-a2")).TryGetProperty("starred", out _));

        var search = (await app.CallAsync("search3", "query=%22%22&songCount=500&albumCount=500&artistCount=500")).GetProperty("searchResult3");
        var starredSongs = search.GetProperty("song").EnumerateArray().Where(s => s.TryGetProperty("starred", out _)).Select(s => s.GetProperty("id").GetString());
        Assert.Equal(["tr-a1", "tr-b2"], starredSongs.Order());
        Assert.All(search.GetProperty("album").EnumerateArray(), a => Assert.True(a.TryGetProperty("starred", out _)));
        Assert.True(search.GetProperty("artist")[0].TryGetProperty("starred", out _));

        await Ok(app, "unstar", $"id=tr-a1&albumId={AlbumA}&artistId={Artist}");
        starred = (await app.CallAsync("getStarred2")).GetProperty("starred2");
        Assert.Equal(["tr-b2"], Ids(starred, "song"));
        Assert.Equal([AlbumB], Ids(starred, "album"));
        Assert.Empty(Ids(starred, "artist"));
        Assert.False((await SongAsync(app, "tr-a1")).TryGetProperty("starred", out _));
        Assert.False((await AlbumAsync(app, AlbumA)).TryGetProperty("starred", out _));
    }

    [Fact]
    public async Task StarNeedsAnIdOfAKnownKind()
    {
        using var app = await StartAsync();
        Assert.Equal(10, ErrorCode(await app.CallAsync("star")));
        Assert.Equal(70, ErrorCode(await app.CallAsync("star", "id=pl-1")));
    }

    [Fact]
    public async Task RatingIsSetAndRemoved()
    {
        using var app = await StartAsync();
        await Ok(app, "setRating", "id=tr-a1&rating=4");
        await Ok(app, "setRating", $"id={AlbumB}&rating=5");
        await Ok(app, "setRating", $"id={AlbumA}&rating=2");
        await Ok(app, "setRating", $"id={Artist}&rating=3");
        Assert.Equal(4, (await SongAsync(app, "tr-a1")).GetProperty("userRating").GetInt32());
        Assert.Equal(5, (await AlbumAsync(app, AlbumB)).GetProperty("userRating").GetInt32());
        Assert.Equal(3, (await app.CallAsync("getArtist", $"id={Artist}")).GetProperty("artist").GetProperty("userRating").GetInt32());
        Assert.Equal([AlbumB, AlbumA], await AlbumListAsync(app, "highest"));

        await Ok(app, "setRating", "id=tr-a1&rating=0");
        Assert.False((await SongAsync(app, "tr-a1")).TryGetProperty("userRating", out _));
        Assert.Equal(0, ErrorCode(await app.CallAsync("setRating", "id=tr-a1&rating=6")));
        Assert.Equal(10, ErrorCode(await app.CallAsync("setRating", "id=tr-a1")));
    }

    [Fact]
    public async Task ScrobbleCountsSubmissionsOnly()
    {
        using var app = await StartAsync();
        Assert.Equal(0, (await SongAsync(app, "tr-a1")).GetProperty("playCount").GetInt64());

        var time = new DateTimeOffset(2026, 5, 4, 3, 2, 1, TimeSpan.Zero);
        await Ok(app, "scrobble", $"id=tr-a1&time={time.ToUnixTimeMilliseconds()}");
        var song = await SongAsync(app, "tr-a1");
        Assert.Equal(1, song.GetProperty("playCount").GetInt64());
        Assert.Equal("2026-05-04T03:02:01.000Z", song.GetProperty("played").GetString());

        // "Now playing" changes nothing; submission defaults to true.
        await Ok(app, "scrobble", "id=tr-a1&submission=false");
        Assert.Equal(1, (await SongAsync(app, "tr-a1")).GetProperty("playCount").GetInt64());
        await Ok(app, "scrobble", "id=tr-a1&id=tr-a2");
        song = await SongAsync(app, "tr-a1");
        Assert.Equal(2, song.GetProperty("playCount").GetInt64());
        Assert.True(DateTimeOffset.Parse(song.GetProperty("played").GetString()!) > time);

        // An older, late scrobble counts but does not move the last play back.
        await Ok(app, "scrobble", $"id=tr-a1&time={time.ToUnixTimeMilliseconds()}");
        song = await SongAsync(app, "tr-a1");
        Assert.Equal(3, song.GetProperty("playCount").GetInt64());
        Assert.NotEqual("2026-05-04T03:02:01.000Z", song.GetProperty("played").GetString());

        var album = await AlbumAsync(app, AlbumA);
        Assert.Equal(4, album.GetProperty("playCount").GetInt64());
        Assert.Equal(song.GetProperty("played").GetString(), album.GetProperty("played").GetString());
        Assert.False((await SongAsync(app, "tr-b1")).TryGetProperty("played", out _));

        Assert.Equal(10, ErrorCode(await app.CallAsync("scrobble")));
    }

    [Fact]
    public async Task AlbumListsFromUserData()
    {
        using var app = await StartAsync();
        Assert.Empty(await AlbumListAsync(app, "frequent"));
        Assert.Empty(await AlbumListAsync(app, "recent"));
        Assert.Empty(await AlbumListAsync(app, "starred"));

        var older = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
        // Album A: three plays, yesterday. Album B: one play, now.
        await Ok(app, "scrobble", $"id=tr-a1&id=tr-a2&id=tr-a3&time={older}");
        await Ok(app, "scrobble", "id=tr-b1");
        Assert.Equal([AlbumA, AlbumB], await AlbumListAsync(app, "frequent"));
        Assert.Equal([AlbumB, AlbumA], await AlbumListAsync(app, "recent"));

        await Ok(app, "star", $"albumId={AlbumB}");
        Assert.Equal([AlbumB], await AlbumListAsync(app, "starred"));
        var listed = (await app.CallAsync("getAlbumList2", "type=starred")).GetProperty("albumList2").GetProperty("album")[0];
        Assert.True(listed.TryGetProperty("starred", out _));
        Assert.Equal(1, listed.GetProperty("playCount").GetInt64());
    }
}

public class SubsonicPlaylistTests
{
    private static async Task<SubsonicTestHost> StartAsync()
    {
        var app = new SubsonicTestHost();
        await app.SeedAsync(Enumerable.Range(1, 4).Select(i => new LibraryEntry
        {
            RelativePath = $"A/B/{i}.m4a", SongId = $"tr-{i}", Title = $"Song {i}", Artist = "A", Album = "B", AlbumArtist = "A", TrackNumber = i,
            DurationSeconds = 60, AddedAt = DateTimeOffset.UtcNow,
        }));
        return app;
    }

    private static List<string> Entries(JsonElement playlist) =>
        playlist.TryGetProperty("entry", out var e) ? e.EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToList() : [];

    private static async Task<JsonElement> GetAsync(SubsonicTestHost app, string id)
    {
        var r = await app.CallAsync("getPlaylist", $"id={id}");
        Assert.True(r.GetProperty("status").GetString() == "ok", r.ToString());
        return r.GetProperty("playlist");
    }

    private static async Task Ok(SubsonicTestHost app, string endpoint, string query)
    {
        var r = await app.CallAsync(endpoint, query);
        Assert.True(r.GetProperty("status").GetString() == "ok", r.ToString());
    }

    [Fact]
    public async Task PlaylistLifecycle()
    {
        using var app = await StartAsync();
        var created = (await app.CallAsync("createPlaylist", "name=Mix&songId=tr-1&songId=tr-2")).GetProperty("playlist");
        var id = created.GetProperty("id").GetString()!;
        Assert.StartsWith("pl-", id);
        Assert.Equal("Mix", created.GetProperty("name").GetString());
        Assert.Equal(["tr-1", "tr-2"], Entries(created));
        Assert.Equal(2, created.GetProperty("songCount").GetInt32());
        Assert.Equal(120, created.GetProperty("duration").GetInt32());
        Assert.Equal("owner", created.GetProperty("owner").GetString());
        Assert.False(created.GetProperty("public").GetBoolean());

        var list = (await app.CallAsync("getPlaylists")).GetProperty("playlists").GetProperty("playlist");
        Assert.Equal(id, list.EnumerateArray().Single().GetProperty("id").GetString());
        Assert.False(list[0].TryGetProperty("entry", out _));

        // Add, remove by index (before adding), rename, comment, public.
        await Ok(app, "updatePlaylist", $"playlistId={id}&songIdToAdd=tr-3&songIdToAdd=tr-4&songIndexToRemove=0");
        Assert.Equal(["tr-2", "tr-3", "tr-4"], Entries(await GetAsync(app, id)));
        await Ok(app, "updatePlaylist", $"playlistId={id}&songIndexToRemove=0&songIndexToRemove=2");
        Assert.Equal(["tr-3"], Entries(await GetAsync(app, id)));
        await Ok(app, "updatePlaylist", $"playlistId={id}&name=Renamed&comment=Hello&public=true");
        var renamed = await GetAsync(app, id);
        Assert.Equal("Renamed", renamed.GetProperty("name").GetString());
        Assert.Equal("Hello", renamed.GetProperty("comment").GetString());
        Assert.True(renamed.GetProperty("public").GetBoolean());

        // createPlaylist with playlistId replaces the songs.
        var replaced = (await app.CallAsync("createPlaylist", $"playlistId={id}&songId=tr-4&songId=tr-1&songId=tr-4")).GetProperty("playlist");
        Assert.Equal(id, replaced.GetProperty("id").GetString());
        Assert.Equal("Renamed", replaced.GetProperty("name").GetString());
        Assert.Equal(["tr-4", "tr-1", "tr-4"], Entries(replaced));

        await Ok(app, "deletePlaylist", $"id={id}");
        Assert.Equal(0, (await app.CallAsync("getPlaylists")).GetProperty("playlists").GetProperty("playlist").GetArrayLength());
        Assert.Equal(70, (await app.CallAsync("getPlaylist", $"id={id}")).GetProperty("error").GetProperty("code").GetInt32());
    }

    [Theory]
    [InlineData("getPlaylist", "id=pl-999")]
    [InlineData("getPlaylist", "id=al-1")]
    [InlineData("updatePlaylist", "playlistId=pl-999&name=x")]
    [InlineData("deletePlaylist", "id=pl-999")]
    [InlineData("createPlaylist", "playlistId=pl-999&songId=tr-1")]
    public async Task UnknownPlaylistIsError70(string endpoint, string query)
    {
        using var app = await StartAsync();
        Assert.Equal(70, (await app.CallAsync(endpoint, query)).GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task CreateWithoutNameIsMissingParameter()
    {
        using var app = await StartAsync();
        Assert.Equal(10, (await app.CallAsync("createPlaylist", "songId=tr-1")).GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task EntriesOutsideTheLibraryAreHiddenButKept()
    {
        using var app = await StartAsync();
        var id = (await app.CallAsync("createPlaylist", "name=X&songId=tr-1&songId=tr-gone&songId=tr-2")).GetProperty("playlist")
            .GetProperty("id").GetString()!;
        var playlist = await GetAsync(app, id);
        Assert.Equal(["tr-1", "tr-2"], Entries(playlist));
        Assert.Equal(2, playlist.GetProperty("songCount").GetInt32());

        // Index 1 is the second visible entry (tr-2), not the hidden one.
        await Ok(app, "updatePlaylist", $"playlistId={id}&songIndexToRemove=1");
        Assert.Equal(["tr-1"], Entries(await GetAsync(app, id)));
        await Ok(app, "createPlaylist", $"playlistId={id}&songId=tr-3");

        var store = await app.Get<PlaylistStore>().GetAsync(CatalogIds.ParsePlaylist(id)!.Value);
        Assert.Equal(["tr-3", "tr-gone"], store!.Entries.Select(e => e.SongId));
    }
}

public class SubsonicUserDataStabilityTests
{
    private static TagSet Tags(string title, string trackId) => new()
    {
        Title = title, Artist = "A", Artists = ["A"], Album = "B", AlbumArtist = "A", AlbumArtists = ["A"], MbTrackId = trackId,
    };

    [Fact]
    public async Task AnnotationsFollowTheSongIdToTheReplacingFile()
    {
        using var app = new SubsonicTestHost();
        app.AddFile("A/B/old.m4a", Tags("Old", Guid.NewGuid().ToString()));
        await app.ScanAsync();
        var songId = (await app.EntryAsync("A/B/old.m4a"))!.SongId!;

        await app.CallAsync("star", $"id={songId}");
        await app.CallAsync("setRating", $"id={songId}&rating=5");
        await app.CallAsync("scrobble", $"id={songId}");
        var playlistId = (await app.CallAsync("createPlaylist", $"name=P&songId={songId}")).GetProperty("playlist").GetProperty("id").GetString();

        // "Keep new" with another release track ID: the new file takes over the song ID.
        File.Delete(Path.Combine(app.LibraryRoot, "A/B/old.m4a"));
        app.AddFile("A/B/new.m4a", Tags("New", Guid.NewGuid().ToString()));
        await app.Get<LibraryIndexer>().UpsertAsync("A/B/new.m4a", null, CancellationToken.None, replaces: "A/B/old.m4a");

        var song = (await app.CallAsync("getSong", $"id={songId}")).GetProperty("song");
        Assert.Equal("New", song.GetProperty("title").GetString());
        Assert.True(song.TryGetProperty("starred", out _));
        Assert.Equal(5, song.GetProperty("userRating").GetInt32());
        Assert.Equal(1, song.GetProperty("playCount").GetInt64());
        var playlist = (await app.CallAsync("getPlaylist", $"id={playlistId}")).GetProperty("playlist");
        Assert.Equal(songId, playlist.GetProperty("entry")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task AnnotationsComeBackWithAMissingFile()
    {
        using var app = new SubsonicTestHost();
        var trackId = Guid.NewGuid().ToString();
        var path = app.AddFile("A/B/1.m4a", Tags("One", trackId));
        await app.ScanAsync();
        var songId = (await app.EntryAsync("A/B/1.m4a"))!.SongId!;
        await app.CallAsync("star", $"id={songId}");
        await app.CallAsync("scrobble", $"id={songId}");
        var playlistId = (await app.CallAsync("createPlaylist", $"name=P&songId={songId}")).GetProperty("playlist").GetProperty("id").GetString();

        // The file is gone for a while: nothing is shown, nothing is deleted.
        var backup = Path.Combine(app.Root, "backup.m4a");
        File.Move(path, backup);
        await app.ScanAsync();
        Assert.Empty((await app.CallAsync("getStarred2")).GetProperty("starred2").GetProperty("song").EnumerateArray());
        Assert.Equal(0, (await app.CallAsync("getPlaylist", $"id={playlistId}")).GetProperty("playlist").GetProperty("songCount").GetInt32());
        await using (var db = await app.Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
        {
            Assert.True(await db.Stars.AnyAsync(s => s.ItemId == songId));
            Assert.True(await db.PlayStats.AnyAsync(s => s.SongId == songId));
        }

        // Back (same release track ID, so the same song ID).
        File.Move(backup, path);
        await app.ScanAsync();
        var song = (await app.CallAsync("getSong", $"id={songId}")).GetProperty("song");
        Assert.True(song.TryGetProperty("starred", out _));
        Assert.Equal(1, song.GetProperty("playCount").GetInt64());
        Assert.Equal(1, (await app.CallAsync("getPlaylist", $"id={playlistId}")).GetProperty("playlist").GetProperty("songCount").GetInt32());
    }
}
