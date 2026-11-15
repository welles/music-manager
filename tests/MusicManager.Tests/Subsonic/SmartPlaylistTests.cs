using System.Text.Json;
using MusicManager.Core.Data;

namespace MusicManager.Tests.Subsonic;

/// <summary>Playlists Music Manager keeps by itself: "Alle", "Noch nie gespielt", "Favoriten" – read-only for clients.</summary>
public class SmartPlaylistTests
{
    private const string All = "pl-smart-alle";
    private const string NeverPlayed = "pl-smart-nie-gespielt";
    private const string Favorites = "pl-smart-favoriten";

    /// <summary>30 songs tr-01..tr-30, enough that two shuffles practically never come out the same.</summary>
    private static async Task<SubsonicTestHost> StartAsync()
    {
        var app = new SubsonicTestHost();
        await app.SeedAsync(Enumerable.Range(1, 30).Select(i => new LibraryEntry
        {
            RelativePath = $"Artist/Album/{i:00}.m4a", SongId = $"tr-{i:00}", Title = $"Song {i:00}", Artist = "Artist", Artists = ["Artist"],
            AlbumArtist = "Artist", AlbumArtists = ["Artist"], Album = "Album", TrackNumber = i, DurationSeconds = 100, FileSize = 1000,
            AddedAt = DateTimeOffset.UtcNow,
        }));
        return app;
    }

    private static async Task<JsonElement> PlaylistAsync(SubsonicTestHost app, string id) =>
        (await app.CallAsync("getPlaylist", $"id={id}")).GetProperty("playlist");

    private static List<string> SongIds(JsonElement playlist) =>
        playlist.TryGetProperty("entry", out var entries) ? entries.EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToList() : [];

    [Fact]
    public async Task TheSmartPlaylistsComeFirstReadOnlyAndBeforeTheClientsOwn()
    {
        using var app = await StartAsync();
        await app.CallAsync("createPlaylist", "name=Mine&songId=tr-01");

        var playlists = (await app.CallAsync("getPlaylists")).GetProperty("playlists").GetProperty("playlist").EnumerateArray().ToList();

        Assert.Equal(["Alle", "Noch nie gespielt", "Favoriten", "Mine"], playlists.Select(p => p.GetProperty("name").GetString()));
        Assert.All(playlists.Take(3), p => Assert.True(p.GetProperty("readonly").GetBoolean()));
        Assert.False(playlists[3].TryGetProperty("readonly", out _));
        Assert.Equal(30, playlists[0].GetProperty("songCount").GetInt32());
        Assert.False(playlists[0].TryGetProperty("entry", out _)); // the list carries no songs
    }

    [Fact]
    public async Task AlleHasEverySongInANewOrderEachTime()
    {
        using var app = await StartAsync();

        var first = await PlaylistAsync(app, All);
        var second = await PlaylistAsync(app, All);

        Assert.Equal(30, SongIds(first).Distinct().Count());
        Assert.Equal(SongIds(first).Order(), SongIds(second).Order());
        Assert.NotEqual(SongIds(first), SongIds(second));
        // Valid only for now: a client that honours it loads the playlist anew for the next start.
        Assert.True(first.TryGetProperty("validUntil", out _));
    }

    [Fact]
    public async Task NochNieGespieltLeavesOutOnlySongsThatWereReallyPlayed()
    {
        using var app = await StartAsync();
        await app.CallAsync("scrobble", "id=tr-01&submission=true");
        // Started and skipped: the client only said "now playing".
        await app.CallAsync("scrobble", "id=tr-02&submission=false");

        var ids = SongIds(await PlaylistAsync(app, NeverPlayed));

        Assert.Equal(29, ids.Count);
        Assert.DoesNotContain("tr-01", ids);
        Assert.Contains("tr-02", ids);
    }

    [Fact]
    public async Task FavoritenShowsTheLatestFavoriteFirst()
    {
        using var app = await StartAsync();
        foreach (var id in new[] { "tr-05", "tr-12", "tr-03" })
        {
            await app.CallAsync("star", $"id={id}");
            await Task.Delay(20); // distinct star times
        }

        // An album star does not belong in a song playlist.
        await app.CallAsync("star", "albumId=al-whatever");

        var favorites = await PlaylistAsync(app, Favorites);

        Assert.Equal(["tr-03", "tr-12", "tr-05"], SongIds(favorites));
        Assert.Equal(SongIds(favorites), SongIds(await PlaylistAsync(app, Favorites))); // not shuffled
        Assert.False(favorites.TryGetProperty("validUntil", out _));

        // Taking back an older favorite changes the list: "changed" must move so clients reload it.
        var before = favorites.GetProperty("changed").GetDateTimeOffset();
        await Task.Delay(20);
        await app.CallAsync("unstar", "id=tr-05");
        var after = await PlaylistAsync(app, Favorites);
        Assert.Equal(["tr-03", "tr-12"], SongIds(after));
        Assert.True(after.GetProperty("changed").GetDateTimeOffset() > before);
    }

    [Theory]
    [InlineData("updatePlaylist", "playlistId=pl-smart-alle&name=Mine")]
    [InlineData("updatePlaylist", "playlistId=pl-smart-favoriten&songIdToAdd=tr-01")]
    [InlineData("deletePlaylist", "id=pl-smart-nie-gespielt")]
    [InlineData("createPlaylist", "playlistId=pl-smart-alle&songId=tr-01")]
    public async Task ClientsCannotChangeThem(string endpoint, string query)
    {
        using var app = await StartAsync();

        var response = await SubsonicTestHost.ReadAsync(await app.CreateClient().GetAsync($"/rest/{endpoint}?apiKey={SubsonicTestHost.Key}&f=json&{query}"));

        Assert.Equal("failed", response.GetProperty("status").GetString());
        Assert.Equal(50, response.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(30, SongIds(await PlaylistAsync(app, All)).Count);
    }

    [Theory]
    [InlineData(All)]
    [InlineData(NeverPlayed)]
    [InlineData(Favorites)]
    public async Task EachHasItsOwnFixedCover(string id)
    {
        using var app = await StartAsync();

        var playlist = await PlaylistAsync(app, id);
        var cover = await app.CreateClient().GetAsync($"/rest/getCoverArt?apiKey={SubsonicTestHost.Key}&id={id}");

        Assert.Equal(id, playlist.GetProperty("coverArt").GetString());
        Assert.Equal(System.Net.HttpStatusCode.OK, cover.StatusCode);
        Assert.Equal("image/png", cover.Content.Headers.ContentType?.MediaType);
        var bytes = await cover.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
    }
}
