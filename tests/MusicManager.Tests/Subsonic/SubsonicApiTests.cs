using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MusicManager.Core.Data;
using MusicManager.Web.Subsonic;

namespace MusicManager.Tests.Subsonic;

public class SubsonicAuthTests(SubsonicAuthTests.Host host) : IClassFixture<SubsonicAuthTests.Host>
{
    public sealed class Host : IDisposable
    {
        public SubsonicTestHost App { get; } = new();
        public void Dispose() => App.Dispose();
    }

    private static int ErrorCode(JsonElement r)
    {
        Assert.Equal("failed", r.GetProperty("status").GetString());
        return r.GetProperty("error").GetProperty("code").GetInt32();
    }

    [Fact]
    public async Task PingWithKeyHasTheOpenSubsonicEnvelope()
    {
        var r = await host.App.CallAsync("ping");
        Assert.Equal("ok", r.GetProperty("status").GetString());
        Assert.Equal("1.16.1", r.GetProperty("version").GetString());
        Assert.Equal("music-manager", r.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(r.GetProperty("serverVersion").GetString()));
        Assert.True(r.GetProperty("openSubsonic").GetBoolean());
    }

    [Fact]
    public async Task MissingAuthIsMissingParameter() => Assert.Equal(10, ErrorCode(await host.App.CallAsync("ping", auth: false)));

    [Fact]
    public async Task WrongKeyIsInvalidApiKey() => Assert.Equal(44, ErrorCode(await host.App.CallAsync("ping", "apiKey=nope", auth: false)));

    [Fact]
    public async Task PasswordAuthIsNotSupported() =>
        Assert.Equal(42, ErrorCode(await host.App.CallAsync("ping", "u=owner&p=secret", auth: false)));

    [Fact]
    public async Task TokenAuthIsNotSupported() =>
        Assert.Equal(42, ErrorCode(await host.App.CallAsync("ping", "u=owner&t=abc&s=def", auth: false)));

    [Fact]
    public async Task KeyAndUserAreConflicting() => Assert.Equal(43, ErrorCode(await host.App.CallAsync("ping", "u=owner")));

    [Fact]
    public async Task ExtensionsWorkWithoutAuth()
    {
        var r = await host.App.CallAsync("getOpenSubsonicExtensions", auth: false);
        Assert.Equal("ok", r.GetProperty("status").GetString());
        var names = r.GetProperty("openSubsonicExtensions").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.Contains("apiKeyAuthentication", names);
        Assert.Contains("formPost", names);
    }

    [Fact]
    public async Task UnknownEndpointIsAFailedEnvelope()
    {
        var r = await host.App.CallAsync("doesNotExist");
        Assert.Equal(0, ErrorCode(r));
        Assert.Equal("1.16.1", r.GetProperty("version").GetString());
    }

    [Fact]
    public async Task ViewSuffixWorks() => Assert.Equal("ok", (await host.App.CallAsync("ping.view")).GetProperty("status").GetString());

    [Fact]
    public async Task FormPostWorks()
    {
        var response = await host.App.CreateClient().PostAsync("/rest/ping.view",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["apiKey"] = SubsonicTestHost.Key, ["f"] = "json" }));
        Assert.Equal("ok", (await SubsonicTestHost.ReadAsync(response)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task FormPostWithWrongKeyFails()
    {
        var response = await host.App.CreateClient().PostAsync("/rest/ping",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["apiKey"] = "wrong" }));
        Assert.Equal(44, ErrorCode(await SubsonicTestHost.ReadAsync(response)));
    }

    [Fact]
    public async Task NotFoundIsSubsonicErrorNotHtml() =>
        Assert.Equal(70, ErrorCode(await host.App.CallAsync("getAlbum", "id=al-missing")));

    [Fact]
    public async Task MissingParameterIsError10() => Assert.Equal(10, ErrorCode(await host.App.CallAsync("getAlbum")));

    [Fact]
    public void EmptyQuoteQueryIsEmpty()
    {
        Assert.Equal("", SubsonicEndpoints.SearchQuery("\"\""));
        Assert.Equal("", SubsonicEndpoints.SearchQuery(null));
        Assert.Equal("abc", SubsonicEndpoints.SearchQuery("\"abc\""));
    }

    [Fact]
    public async Task DataGoesToTheTempFolder()
    {
        await host.App.CallAsync("ping");
        Assert.True(File.Exists(Path.Combine(host.App.DataRoot, "musicmanager.db")));
    }
}

public class SubsonicWithoutKeyTests
{
    [Fact]
    public async Task WithoutConfiguredKeyEveryCallFails()
    {
        using var app = new SubsonicTestHost(apiKey: null);
        var r = await app.CallAsync("ping");
        Assert.Equal(44, r.GetProperty("error").GetProperty("code").GetInt32());
        var ext = await app.CallAsync("getOpenSubsonicExtensions", auth: false);
        Assert.Equal("ok", ext.GetProperty("status").GetString());
    }
}

public class SubsonicBrowsingTests(SubsonicBrowsingTests.Host host) : IClassFixture<SubsonicBrowsingTests.Host>
{
    public sealed class Host : IAsyncLifetime
    {
        public SubsonicTestHost App { get; } = new();
        public int Songs { get; private set; }

        public async Task InitializeAsync()
        {
            var entries = new List<LibraryEntry>();
            var n = 0;
            for (var a = 0; a < 7; a++)
            {
                var artist = $"Artist {a}";
                var artistMbId = a % 2 == 0 ? Guid.NewGuid().ToString() : null;
                for (var b = 0; b < 3; b++)
                {
                    var releaseId = b == 0 ? Guid.NewGuid().ToString() : null;
                    for (var t = 1; t <= 4; t++)
                    {
                        n++;
                        entries.Add(new LibraryEntry
                        {
                            RelativePath = $"{artist}/Album {b}/{t:00}.m4a", SongId = $"tr-{n:0000}", Title = $"Song {t}",
                            // Same titles in every album: ordering must still be total.
                            Artist = artist, Artists = [artist, .. t == 2 ? new[] { "Guest" } : []], AlbumArtist = artist, AlbumArtists = [artist],
                            MbArtistIds = artistMbId != null && t != 3 ? [artistMbId] : [], MbAlbumArtistIds = artistMbId != null ? [artistMbId] : [],
                            Album = $"Album {b}", MbReleaseId = releaseId, TrackNumber = t, DiscNumber = 1, Year = 2000 + b,
                            Genres = [b == 1 ? "Rock" : "Pop"], DurationSeconds = 60, FileSize = 1000, Codec = "aac", BitrateKbps = 256,
                            AddedAt = DateTimeOffset.UtcNow.AddMinutes(-n), ReleaseTypes = b == 2 ? ["album", "compilation"] : ["album"],
                            OriginalDate = "1999-05-01",
                        });
                    }
                }
            }

            Songs = n;
            await App.SeedAsync(entries);
        }

        public Task DisposeAsync()
        {
            App.Dispose();
            return Task.CompletedTask;
        }
    }

    private static List<string> Ids(JsonElement result, string key) =>
        result.TryGetProperty(key, out var list) ? list.EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToList() : [];

    private async Task<(List<string> Artists, List<string> Albums, List<string> Songs)> PageThroughAsync(int pageSize)
    {
        var (artists, albums, songs) = (new List<string>(), new List<string>(), new List<string>());
        for (var offset = 0; ; offset += pageSize)
        {
            var r = await host.App.CallAsync("search3",
                $"query=%22%22&artistCount={pageSize}&artistOffset={offset}&albumCount={pageSize}&albumOffset={offset}&songCount={pageSize}&songOffset={offset}");
            var result = r.GetProperty("searchResult3");
            var page = (Ids(result, "artist"), Ids(result, "album"), Ids(result, "song"));
            if (page.Item1.Count + page.Item2.Count + page.Item3.Count == 0) break;
            artists.AddRange(page.Item1);
            albums.AddRange(page.Item2);
            songs.AddRange(page.Item3);
        }

        return (artists, albums, songs);
    }

    [Fact]
    public async Task Search3PagesThroughEverythingWithoutGapsOrRepeats()
    {
        var first = await PageThroughAsync(7);
        Assert.Equal(host.Songs, first.Songs.Count);
        Assert.Equal(first.Songs.Count, first.Songs.Distinct().Count());
        Assert.Equal(7 * 3, first.Albums.Count);
        Assert.Equal(first.Albums.Count, first.Albums.Distinct().Count());
        // Album artists only ("Guest" is just a track artist).
        Assert.Equal(7, first.Artists.Count);

        var second = await PageThroughAsync(5);
        Assert.Equal(first.Songs, second.Songs);
        Assert.Equal(first.Albums, second.Albums);
        Assert.Equal(first.Artists, second.Artists);
    }

    [Fact]
    public async Task Search3WithQueryMatchesTitles()
    {
        // Every word must occur somewhere in title, artists or album; case-insensitive.
        var r = (await host.App.CallAsync("search3", "query=GUEST%20song&songCount=500")).GetProperty("searchResult3");
        var songs = r.GetProperty("song").EnumerateArray().ToList();
        Assert.Equal(21, songs.Count);
        Assert.All(songs, s => Assert.Equal("Song 2", s.GetProperty("title").GetString()));
        Assert.Empty(r.GetProperty("album").EnumerateArray());

        var albums = (await host.App.CallAsync("search3", "query=album%201&albumCount=500")).GetProperty("searchResult3").GetProperty("album");
        // "Album 1" of every artist plus the other two albums of "Artist 1".
        Assert.Equal(9, albums.GetArrayLength());
    }

    [Fact]
    public async Task GetArtistsListsAlbumArtistsOnly()
    {
        var r = await host.App.CallAsync("getArtists");
        var names = r.GetProperty("artists").GetProperty("index").EnumerateArray()
            .SelectMany(i => i.GetProperty("artist").EnumerateArray()).Select(a => a.GetProperty("name").GetString()).ToList();
        Assert.Equal(7, names.Count);
        Assert.DoesNotContain("Guest", names);
    }

    [Fact]
    public async Task ArtistWithAndWithoutMusicBrainzIdIsOneArtist()
    {
        // Artist 0 has an MB ID, but track 3 of each album has no artist ID.
        var r = (await host.App.CallAsync("search3", "query=Artist%200&songCount=500&albumCount=0&artistCount=0")).GetProperty("searchResult3");
        var songs = r.GetProperty("song").EnumerateArray().Where(s => s.GetProperty("artist").GetString() == "Artist 0").ToList();
        Assert.Equal(12, songs.Count);
        var artistIds = songs.Select(s => s.GetProperty("artists")[0].GetProperty("id").GetString()).Distinct().ToList();
        Assert.Single(artistIds);
    }

    [Fact]
    public async Task GetAlbumReturnsSongsAndOpenSubsonicFields()
    {
        var albums = (await host.App.CallAsync("getAlbumList2", "type=alphabeticalByArtist&size=500")).GetProperty("albumList2").GetProperty("album");
        var compilation = albums.EnumerateArray().First(a => a.GetProperty("isCompilation").GetBoolean());
        var r = (await host.App.CallAsync("getAlbum", $"id={compilation.GetProperty("id").GetString()}")).GetProperty("album");
        Assert.Equal(4, r.GetProperty("songCount").GetInt32());
        Assert.Equal(240, r.GetProperty("duration").GetInt32());
        Assert.Equal(["album", "compilation"], r.GetProperty("releaseTypes").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(1999, r.GetProperty("originalReleaseDate").GetProperty("year").GetInt32());
        Assert.StartsWith("al-", r.GetProperty("id").GetString());
        var songs = r.GetProperty("song").EnumerateArray().ToList();
        Assert.Equal([1, 2, 3, 4], songs.Select(s => s.GetProperty("track").GetInt32()));
        var song = songs[1];
        Assert.Equal(r.GetProperty("id").GetString(), song.GetProperty("albumId").GetString());
        Assert.Equal(r.GetProperty("id").GetString(), song.GetProperty("parent").GetString());
        Assert.False(song.GetProperty("isDir").GetBoolean());
        Assert.Equal("audio/mp4", song.GetProperty("contentType").GetString());
        Assert.Equal("m4a", song.GetProperty("suffix").GetString());
        Assert.Equal(2, song.GetProperty("artists").GetArrayLength());
        Assert.Equal("Guest", song.GetProperty("artists")[1].GetProperty("name").GetString());
        Assert.Equal(1, song.GetProperty("albumArtists").GetArrayLength());
    }

    [Fact]
    public async Task AlbumListTypes()
    {
        async Task<List<JsonElement>> List(string q) =>
            (await host.App.CallAsync("getAlbumList2", q)).GetProperty("albumList2").GetProperty("album").EnumerateArray().ToList();

        Assert.Equal(7, (await List("type=byGenre&genre=rock&size=500")).Count);
        Assert.All(await List("type=byYear&fromYear=2001&toYear=2001&size=500"), a => Assert.Equal(2001, a.GetProperty("year").GetInt32()));
        var reversed = await List("type=byYear&fromYear=2002&toYear=2000&size=500");
        Assert.Equal(2002, reversed[0].GetProperty("year").GetInt32());
        Assert.Equal(10, (await List("type=newest")).Count);
        Assert.Empty(await List("type=starred"));
        Assert.Equal(5, (await List("type=random&size=5")).Count);
    }

    [Fact]
    public async Task GenresAndRandomSongs()
    {
        var genres = (await host.App.CallAsync("getGenres")).GetProperty("genres").GetProperty("genre").EnumerateArray().ToList();
        var rock = genres.Single(g => g.GetProperty("value").GetString() == "Rock");
        Assert.Equal(28, rock.GetProperty("songCount").GetInt32());
        Assert.Equal(7, rock.GetProperty("albumCount").GetInt32());

        var random = (await host.App.CallAsync("getRandomSongs", "size=3&genre=Rock")).GetProperty("randomSongs").GetProperty("song");
        Assert.Equal(3, random.GetArrayLength());
        var byGenre = (await host.App.CallAsync("getSongsByGenre", "genre=Rock&count=500")).GetProperty("songsByGenre").GetProperty("song");
        Assert.Equal(28, byGenre.GetArrayLength());
    }

    [Fact]
    public async Task CoverArtWithoutImageIsNotFound()
    {
        var r = await host.App.CallAsync("getCoverArt", "id=tr-0001&size=100");
        Assert.Equal(70, r.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task SystemEndpoints()
    {
        Assert.Equal(1, (await host.App.CallAsync("getMusicFolders")).GetProperty("musicFolders").GetProperty("musicFolder").GetArrayLength());
        Assert.True((await host.App.CallAsync("getUser", "username=x")).GetProperty("user").GetProperty("streamRole").GetBoolean());
        Assert.True((await host.App.CallAsync("getLicense")).GetProperty("license").GetProperty("valid").GetBoolean());
        Assert.Equal(host.Songs, (await host.App.CallAsync("getScanStatus")).GetProperty("scanStatus").GetProperty("count").GetInt32());
    }
}

public class SubsonicStreamTests
{
    [Fact]
    public async Task StreamSupportsRangeRequests()
    {
        using var app = new SubsonicTestHost();
        var path = app.AddFile("A/B/A - Song.m4a", new() { Title = "Song", Artist = "A", Album = "B", AlbumArtist = "A" });
        await app.ScanAsync();
        var entry = await app.EntryAsync("A/B/A - Song.m4a");
        Assert.NotNull(entry?.SongId);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/rest/stream.view?apiKey={SubsonicTestHost.Key}&id={entry.SongId}&maxBitRate=128&format=mp3");
        request.Headers.Range = new RangeHeaderValue(10, 109);
        var response = await app.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("audio/mp4", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(100, (await response.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal(new FileInfo(path).Length, response.Content.Headers.ContentRange?.Length);

        // An unsatisfiable range is answered directly, not re-executed as the HTML error page.
        var bad = new HttpRequestMessage(HttpMethod.Get, $"/rest/stream?apiKey={SubsonicTestHost.Key}&id={entry.SongId}");
        bad.Headers.Range = new RangeHeaderValue(10_000_000, null);
        var badResponse = await app.CreateClient().SendAsync(bad);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, badResponse.StatusCode);
        Assert.NotEqual("text/html", badResponse.Content.Headers.ContentType?.MediaType);

        var full = await app.CreateClient().GetAsync($"/rest/download?apiKey={SubsonicTestHost.Key}&id={entry.SongId}");
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal(new FileInfo(path).Length, (await full.Content.ReadAsByteArrayAsync()).Length);
    }
}
