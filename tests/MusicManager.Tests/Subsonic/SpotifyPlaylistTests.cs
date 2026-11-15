using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MusicManager.Core.Data;
using MusicManager.Core.Pipeline;
using MusicManager.Core.Services;

namespace MusicManager.Tests.Subsonic;

/// <summary>Spotify playlists kept in step and offered read-only to clients (issues #19/#21), against a stand-in Spotify.</summary>
public sealed class SpotifyPlaylistTests : IDisposable
{
    private readonly SubsonicTestHost _base = new();
    private readonly WebApplicationFactory<Program> _host;
    private readonly FakeSpotify _spotify = new();

    public SpotifyPlaylistTests()
    {
        _host = _base.WithWebHostBuilder(b =>
        {
            b.UseSetting("MusicManager:SpotifyClientId", "client-id");
            b.UseSetting("MusicManager:SpotifyClientSecret", "client-secret");
            b.ConfigureTestServices(s =>
            {
                s.AddHttpClient(nameof(SpotifyUserClient)).ConfigurePrimaryHttpMessageHandler(() => _spotify);
                s.AddHttpClient<CoverArtService>().ConfigurePrimaryHttpMessageHandler(() => _spotify);
            });
        });
    }

    public void Dispose()
    {
        _host.Dispose();
        _base.Dispose();
    }

    private T Get<T>() where T : notnull => _host.Services.GetRequiredService<T>();

    /// <summary>
    /// Spotify as the app sees it: "mine" is the owner's playlist (tracks over two pages, new and old field names, plus a
    /// local file and a podcast episode), "theirs" one of someone else the owner follows (tracks refused with 403).
    /// </summary>
    private sealed class FakeSpotify : HttpMessageHandler
    {
        public int ItemRequests;
        public string Snapshot = "snap-1";
        public string Scope = SpotifyUserClient.Scopes;

        // The playlist of missing tracks as Spotify would hold it.
        public int Created;
        public int Writes;
        public List<string>? Missing;

        private static object Track(string field, string id, string title, string artist, int ms, string? isrc) => new Dictionary<string, object>
        {
            [field] = new
            {
                type = "track", id, name = title, duration_ms = ms, is_local = false, artists = new[] { new { name = artist } },
                album = new { name = "Album", images = new[] { new { url = $"https://i.scdn.co/{id}.jpg" } } },
                external_ids = isrc == null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["isrc"] = isrc },
            },
        };

        private static string Json(object o) => JsonSerializer.Serialize(o);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!;
            var path = url.AbsolutePath;
            string? json = (url.Host, path) switch
            {
                ("accounts.spotify.com", "/api/token") => Json(new { access_token = "access", token_type = "Bearer", expires_in = 3600, refresh_token = "refresh-1", scope = Scope }),
                (_, "/v1/me") => """{"id":"me","display_name":"Nico"}""",
                (_, "/v1/me/playlists") =>
                    """{"items":[{"id":"mine","name":"Der Abspül-Blues","owner":{"id":"me","display_name":"Nico"},"collaborative":false,"images":[],"items":{"total":3}},{"id":"theirs","name":"Top Hits","owner":{"id":"spotify","display_name":"Spotify"},"collaborative":false,"images":[],"tracks":{"total":50}}],"next":null}""",
                (_, "/v1/playlists/mine") => Json(new
                {
                    id = "mine", name = "Der Abspül-Blues", description = "Zum &amp; Spülen", owner = new { id = "me", display_name = "Nico" },
                    collaborative = false, snapshot_id = Snapshot, images = new[] { new { url = "https://image.test/cover.jpg" } }, items = new { total = 3 },
                }),
                (_, "/v1/playlists/theirs") =>
                    """{"id":"theirs","name":"Top Hits","owner":{"id":"spotify","display_name":"Spotify"},"collaborative":false,"snapshot_id":"x","images":[]}""",
                (_, "/v1/playlists/mine/items") when url.Query.Contains("page=2") => Page2(),
                (_, "/v1/playlists/mine/items") => Page1(),
                ("image.test", _) => null,
                _ => null,
            };

            if (path == "/v1/me/playlists" && request.Method == HttpMethod.Post)
            {
                Created++;
                Missing = [];
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"id":"missing"}""") });
            }

            if (path == "/v1/playlists/missing" && Missing != null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("""{"id":"missing","name":"Fehlt im Music Manager","owner":{"id":"me"},"snapshot_id":"m"}""") });

            if (path == "/v1/playlists/missing/items" && Missing != null)
            {
                if (request.Method == HttpMethod.Get)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        { Content = new StringContent(Json(new { items = Missing.Select(id => Track("item", id, id, "x", 1000, null)).ToArray(), next = (string?)null })) });
                var uris = JsonDocument.Parse(request.Content!.ReadAsStringAsync(ct).Result).RootElement.GetProperty("uris").EnumerateArray()
                    .Select(u => u.GetString()!["spotify:track:".Length..]).ToList();
                Writes++;
                if (request.Method == HttpMethod.Put) Missing = uris;
                else Missing.AddRange(uris);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"snapshot_id":"m2"}""") });
            }

            if (path == "/v1/playlists/theirs/items")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                    { Content = new StringContent("""{"error":{"status":403,"message":"Forbidden"}}""") });
            if (url.Host == "image.test")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 1, 2, 3]) });
            return Task.FromResult(json == null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":{"status":404,"message":"Not found"}}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

            string Page1()
            {
                Interlocked.Increment(ref ItemRequests);
                return Json(new
                {
                    items = new[] { Track("item", "s1", "Haus am See", "Peter Fox", 220000, "dea810800130"), Track("item", "s2", "Never Owned", "Nobody", 180000, null) },
                    next = "https://api.spotify.com/v1/playlists/mine/items?page=2",
                });
            }

            string Page2() => Json(new
            {
                items = new object[]
                {
                    Track("track", "s3", "Dickes B (feat. Black Kappa)", "Seeed", 245000, null), // the old field name
                    new { item = new { type = "track", id = (string?)null, name = "My Demo", is_local = true, artists = Array.Empty<object>() } },
                    new { item = new { type = "episode", id = "e1", name = "Podcast" } },
                },
                next = (string?)null,
            });
        }
    }

    private async Task ConnectAsync()
    {
        var client = _host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var connect = await client.GetAsync("/spotify/connect");
        var login = connect.Headers.Location!.ToString();
        Assert.StartsWith("https://accounts.spotify.com/authorize?", login);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(login).Query);
        Assert.Equal("client-id", query["client_id"]);
        Assert.Equal(SpotifyUserClient.Scopes, query["scope"]);
        Assert.EndsWith("/spotify/callback", query["redirect_uri"]);
        var state = query["state"]!;

        var back = await client.GetAsync($"/spotify/callback?code=the-code&state={state}");
        Assert.Equal("/playlists?spotify=connected", back.Headers.Location!.ToString());
    }

    private async Task SeedLibraryAsync()
    {
        // Two of the three Spotify tracks are in the library: one by ISRC, one by title and artist.
        await _base.SeedAsync(
        [
            new LibraryEntry { RelativePath = "Peter Fox/Stadtaffe/01.m4a", SongId = "tr-fox", Title = "Haus am See", Artist = "Peter Fox", Artists = ["Peter Fox"],
                Album = "Stadtaffe", AlbumArtist = "Peter Fox", AlbumArtists = ["Peter Fox"], Isrc = "DEA810800130", DurationSeconds = 220, FileSize = 1 },
            new LibraryEntry { RelativePath = "Seeed/Next/01.m4a", SongId = "tr-seeed", Title = "Dickes B", Artist = "Seeed", Artists = ["Seeed"],
                Album = "New Dubby Conquerors", AlbumArtist = "Seeed", AlbumArtists = ["Seeed"], DurationSeconds = 246, FileSize = 1 },
        ]);
    }

    [Fact]
    public async Task ALoginWithAForeignStateIsRefused()
    {
        var client = _host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var back = await client.GetAsync("/spotify/callback?code=the-code&state=made-up");

        Assert.Contains("abgelaufen", Uri.UnescapeDataString(back.Headers.Location!.ToString()));
        Assert.Null(await Get<SpotifyUserClient>().AccountAsync());
    }

    [Fact]
    public async Task OwnPlaylistsAreReadableFollowedOnesAreNot()
    {
        await ConnectAsync();

        var account = (await Get<SpotifyUserClient>().AccountAsync())!;
        var playlists = await Get<SpotifyUserClient>().MyPlaylistsAsync();

        Assert.Equal(("me", "Nico", "refresh-1"), (account.UserId, account.DisplayName, account.RefreshToken));
        Assert.Equal(["mine", "theirs"], playlists.Select(p => p.Id));
        Assert.Equal([3, 50], playlists.Select(p => p.TrackCount)); // "items" and the old "tracks"
        Assert.True(SpotifyPlaylistSync.Readable(playlists[0], "me"));
        Assert.False(SpotifyPlaylistSync.Readable(playlists[1], "me"));
    }

    [Fact]
    public async Task APlaylistIsReadAndOfferedReadOnlyWithOnlyTheSongsInTheLibrary()
    {
        await ConnectAsync();
        await SeedLibraryAsync();
        var sync = Get<SpotifyPlaylistSync>();
        var mine = (await Get<SpotifyUserClient>().MyPlaylistsAsync())[0];

        var playlist = await sync.AddAsync(mine);

        Assert.Null(playlist.SyncError);
        await using (var db = await Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
        {
            var tracks = await db.SpotifyPlaylistTracks.Where(t => t.PlaylistId == playlist.Id).OrderBy(t => t.Position).ToListAsync();
            Assert.Equal(["Haus am See", "Never Owned", "Dickes B (feat. Black Kappa)"], tracks.Select(t => t.Title)); // no local file, no episode
            Assert.Equal("DEA810800130", tracks[0].Isrc);
            Assert.Equal("Zum & Spülen", (await db.SpotifyPlaylists.FirstAsync()).Description);
        }

        Assert.True(File.Exists(sync.CoverPath(playlist.Id)));

        var id = $"pl-spotify-{playlist.Id}";
        var listed = (await _base.CallAsync("getPlaylists")).GetProperty("playlists").GetProperty("playlist").EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == id);
        Assert.Equal("Der Abspül-Blues", listed.GetProperty("name").GetString());
        Assert.Equal("Spotify", listed.GetProperty("owner").GetString());
        Assert.True(listed.GetProperty("readonly").GetBoolean());
        Assert.Equal("Spotify-Playlist · von Nico · 2 von 3 Titeln vorhanden", listed.GetProperty("comment").GetString());
        Assert.Equal(id, listed.GetProperty("coverArt").GetString());

        var entries = (await _base.CallAsync("getPlaylist", $"id={id}")).GetProperty("playlist").GetProperty("entry").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString()).ToList();
        Assert.Equal(["tr-fox", "tr-seeed"], entries); // Spotify's order, the missing one left out

        var refused = await SubsonicTestHost.ReadAsync(await _host.CreateClient().GetAsync($"/rest/deletePlaylist?apiKey={SubsonicTestHost.Key}&f=json&id={id}"));
        Assert.Equal(50, refused.GetProperty("error").GetProperty("code").GetInt32());

        var cover = await _host.CreateClient().GetAsync($"/rest/getCoverArt?apiKey={SubsonicTestHost.Key}&id={id}");
        Assert.Equal(HttpStatusCode.OK, cover.StatusCode);
    }

    [Fact]
    public async Task AnUnchangedPlaylistIsNotReadAgain()
    {
        await ConnectAsync();
        var sync = Get<SpotifyPlaylistSync>();
        await sync.AddAsync((await Get<SpotifyUserClient>().MyPlaylistsAsync())[0]);
        Assert.Equal(1, _spotify.ItemRequests);

        await sync.RunAsync(default);
        Assert.Equal(1, _spotify.ItemRequests); // same snapshot

        _spotify.Snapshot = "snap-2";
        await sync.RunAsync(default);
        Assert.Equal(2, _spotify.ItemRequests);
    }

    [Fact]
    public async Task AFollowedPlaylistOfSomeoneElseExplainsTheSoundiizWay()
    {
        await ConnectAsync();
        var theirs = (await Get<SpotifyUserClient>().MyPlaylistsAsync())[1];

        var playlist = await Get<SpotifyPlaylistSync>().AddAsync(theirs);

        Assert.Contains("Soundiiz", playlist.SyncError);
    }

    [Fact]
    public async Task RemovingAPlaylistTakesItsTracksAndCoversAlong()
    {
        await ConnectAsync();
        var sync = Get<SpotifyPlaylistSync>();
        var playlist = await sync.AddAsync((await Get<SpotifyUserClient>().MyPlaylistsAsync())[0]);
        await File.WriteAllBytesAsync($"{sync.CoverPath(playlist.Id)}.badge.jpg", [1]);

        await sync.RemoveAsync(playlist.Id);

        Assert.False(File.Exists(sync.CoverPath(playlist.Id)));
        Assert.False(File.Exists($"{sync.CoverPath(playlist.Id)}.badge.jpg"));
        await using var db = await Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        Assert.Empty(await db.SpotifyPlaylistTracks.ToListAsync());
        var gone = await _base.CallAsync("getPlaylist", $"id=pl-spotify-{playlist.Id}");
        Assert.Equal(70, gone.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ThePlaylistOfMissingTracksFollowsTheLibrary()
    {
        await ConnectAsync();
        await SeedLibraryAsync();
        var client = Get<SpotifyUserClient>();
        var sync = Get<SpotifyPlaylistSync>();
        await sync.AddAsync((await client.MyPlaylistsAsync())[0]);
        Assert.Equal(0, _spotify.Created); // off until turned on

        await client.SetMissingPlaylistAsync(true);
        await sync.RunAsync(default);

        Assert.Equal(1, _spotify.Created);
        Assert.Equal(["s2"], _spotify.Missing); // "Never Owned"; the other two are in the library
        Assert.Equal("missing", (await client.AccountAsync())!.MissingPlaylistId);
        Assert.Equal(1, sync.MissingState?.Count);

        var writes = _spotify.Writes;
        await sync.RunAsync(default);
        Assert.Equal(writes, _spotify.Writes); // unchanged: nothing written
        Assert.Equal(1, _spotify.Created); // and not created again

        // The song arrives in the library: it leaves the playlist.
        // Into this app's index (_base is another instance on the same database, its catalog would not hear of it).
        await using (var db = await Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
        {
            db.Library.Add(new LibraryEntry { RelativePath = "Nobody/X/01.m4a", SongId = "tr-never", Title = "Never Owned", Artist = "Nobody",
                Artists = ["Nobody"], Album = "X", AlbumArtist = "Nobody", AlbumArtists = ["Nobody"], DurationSeconds = 180, FileSize = 1 });
            await db.SaveChangesAsync();
        }

        Get<LibraryIndexer>().OnChanged();
        // The catalog is rebuilt in the background after an index change.
        for (var i = 0; i < 100 && (await Get<MusicManager.Core.Catalog.MediaCatalog>().GetAsync()).Song("tr-never") == null; i++) await Task.Delay(20);
        await sync.RunAsync(default);
        Assert.Empty(_spotify.Missing!);
        Assert.Equal(0, sync.MissingState?.Count);
    }

    [Fact]
    public async Task WithoutTheRightToWriteNothingIsCreated()
    {
        _spotify.Scope = "playlist-read-private playlist-read-collaborative"; // a login from before
        await ConnectAsync();
        var client = Get<SpotifyUserClient>();
        await client.SetMissingPlaylistAsync(true);

        await Get<SpotifyPlaylistSync>().RunAsync(default);

        Assert.Equal(0, _spotify.Created);
        Assert.Contains("neu mit Spotify verbinden", Get<SpotifyPlaylistSync>().MissingState?.Error);

        // Connecting again grants it and keeps the setting.
        _spotify.Scope = SpotifyUserClient.Scopes;
        await ConnectAsync();
        var account = (await client.AccountAsync())!;
        Assert.True(account.MissingPlaylistEnabled);
        Assert.True(account.CanWritePlaylists);
    }
}
