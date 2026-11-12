using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MusicManager.Core.Catalog;
using MusicManager.Core.Data;
using MusicManager.Core.Pipeline;
using MusicManager.Core.Services;

namespace MusicManager.Tests.Subsonic;

public class AlbumCoverTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];

    private static LibraryEntry Entry(string path, string songId, string album, string? releaseId = null) => new()
    {
        RelativePath = path, SongId = songId, Title = songId, Artist = "A", Artists = ["A"], AlbumArtist = "A", AlbumArtists = ["A"],
        Album = album, MbReleaseId = releaseId, DurationSeconds = 60, FileSize = 1000,
    };

    private static string WriteCover(SubsonicTestHost app, string relativePath, byte[] data)
    {
        var path = Path.Combine(app.LibraryRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static Task<HttpResponseMessage> CoverAsync(SubsonicTestHost app, string id) =>
        app.CreateClient().GetAsync($"/rest/getCoverArt?apiKey={SubsonicTestHost.Key}&f=json&id={id}");

    [Fact]
    public void AnAlbumHasAFolderOnlyWhenItIsItsOwn()
    {
        var catalog = CatalogSnapshot.Build(
        [
            Entry(Path.Combine("A", "One", "1.m4a"), "tr-1", "One"),
            Entry(Path.Combine("A", "One", "2.m4a"), "tr-2", "One"),
            // Two albums in one folder.
            Entry(Path.Combine("A", "Mix", "1.m4a"), "tr-3", "Mix 1"),
            Entry(Path.Combine("A", "Mix", "2.m4a"), "tr-4", "Mix 2"),
            // One album in two folders.
            Entry(Path.Combine("A", "CD1", "1.m4a"), "tr-5", "Split"),
            Entry(Path.Combine("A", "CD2", "1.m4a"), "tr-6", "Split"),
            // In the library root.
            Entry("loose.m4a", "tr-7", "Loose"),
        ], 1);

        string? Folder(string album) => catalog.Albums.Single(a => a.Name == album).Folder;
        Assert.Equal(Path.Combine("A", "One"), Folder("One"));
        Assert.Null(Folder("Mix 1"));
        Assert.Null(Folder("Mix 2"));
        Assert.Null(Folder("Split"));
        Assert.Null(Folder("Loose"));
    }

    [Theory]
    [InlineData("cover.jpg", true)]
    [InlineData("Folder.JPG", true)]
    [InlineData("front.png", true)]
    [InlineData("albumart.jpeg", true)]
    [InlineData("cover (2).jpg", false)]
    [InlineData("artist.jpg", false)]
    [InlineData("cover.gif", false)]
    public void CoverFileNames(string name, bool expected) => Assert.Equal(expected, AlbumCoverService.IsCoverFile(name));

    [Fact]
    public async Task CoverArtComesFromTheAlbumFolder()
    {
        using var app = new SubsonicTestHost();
        var release = Guid.NewGuid().ToString();
        await app.SeedAsync(
        [
            Entry(Path.Combine("A", "One", "1.m4a"), "tr-1", "One", release),
            Entry(Path.Combine("A", "Mix", "1.m4a"), "tr-2", "Mix 1"),
            Entry(Path.Combine("A", "Mix", "2.m4a"), "tr-3", "Mix 2"),
        ]);
        // "cover" goes before "folder".
        WriteCover(app, Path.Combine("A", "One", "folder.png"), [0x89, 0x50, 0x4E, 0x47, 9]);
        WriteCover(app, Path.Combine("A", "One", "cover.jpg"), Jpeg);
        WriteCover(app, Path.Combine("A", "Mix", "cover.jpg"), Jpeg);

        // No song has an embedded cover, the album still announces one.
        var album = (await app.CallAsync("getAlbum", $"id=al-{release}")).GetProperty("album");
        Assert.Equal($"al-{release}", album.GetProperty("coverArt").GetString());

        foreach (var id in new[] { $"al-{release}", "tr-1" })
        {
            var response = await CoverAsync(app, id);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(Jpeg, await response.Content.ReadAsByteArrayAsync());
        }

        // A folder shared by two albums has no album cover.
        var shared = await SubsonicTestHost.ReadAsync(await CoverAsync(app, "tr-2"));
        Assert.Equal(70, shared.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task MissingFolderCoversAreLoadedFromTheArchive()
    {
        using var app = new SubsonicTestHost();
        string release1 = Guid.NewGuid().ToString(), group1 = Guid.NewGuid().ToString(), release2 = Guid.NewGuid().ToString(),
            release4 = Guid.NewGuid().ToString();
        app.AddFile("A/One/A - S.m4a", new() { Title = "S", Artist = "A", AlbumArtist = "A", Album = "One", MbReleaseId = release1, MbReleaseGroupId = group1 });
        app.AddFile("A/Two/A - S.m4a", new() { Title = "S", Artist = "A", AlbumArtist = "A", Album = "Two", MbReleaseId = release2 });
        app.AddFile("A/Three/A - S.m4a", new() { Title = "S", Artist = "A", AlbumArtist = "A", Album = "Three" });
        app.AddFile("A/Four/A - S.m4a", new() { Title = "S", Artist = "A", AlbumArtist = "A", Album = "Four", MbReleaseId = release4 });
        var own = WriteCover(app, "A/Two/folder.png", [0x89, 0x50, 0x4E, 0x47, 9]);
        await app.ScanAsync();

        // The archive only has a cover for the release group of the first album. Deezer knows the song of
        // every album, but on the album "Three" (and "Other", which is no album of the library).
        byte[] deezerJpeg = [0xFF, 0xD8, 5];
        var requests = new List<string>();
        var handler = new StubHandler(request =>
        {
            var uri = request.RequestUri!;
            requests.Add(uri.AbsolutePath);
            if (uri.Host == "api.deezer.com")
            {
                const string hit = """{"id":%ID%,"title":"S","artist":{"name":"A"},"album":{"title":"%ALBUM%","cover_xl":"https://cdn.example/%ALBUM%.jpg"},"duration":1}""";
                var data = string.Join(",", new[] { "Other", "Three" }.Select((a, i) => hit.Replace("%ID%", $"{i + 1}").Replace("%ALBUM%", a)));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"data":[{{data}}]}""") };
            }

            return uri.AbsolutePath == $"/release-group/{group1}/front-1200" ? Image(Jpeg)
                : uri.AbsolutePath == "/Three.jpg" ? Image(deezerJpeg)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var http = new HttpClient(handler);
        var service = new AlbumCoverService(app.Get<MediaCatalog>(), new CoverArtService(http, NullLogger<CoverArtService>.Instance),
            new DeezerClient(http, NullLogger<DeezerClient>.Instance), app.Get<SpotifyClient>(),
            app.Get<AudioTools>(), app.Get<Storage>(), app.Get<SettingsService>(), app.Get<PipelineNotifier>(), NullLogger<AlbumCoverService>.Instance);

        await service.BackfillAsync(TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(new AlbumCoverState(false, 3, 3, 2, 1), service.State);
        Assert.Equal(Jpeg, File.ReadAllBytes(Path.Combine(app.LibraryRoot, "A", "One", "cover.jpg")));
        // No MusicBrainz release: the cover of the album with the same name from Deezer.
        Assert.Equal(deezerJpeg, File.ReadAllBytes(Path.Combine(app.LibraryRoot, "A", "Three", "cover.jpg")));
        Assert.Equal(["A - S.m4a", Path.GetFileName(own)], Directory.GetFiles(Path.Combine(app.LibraryRoot, "A", "Two")).Select(Path.GetFileName).Order());
        Assert.False(File.Exists(Path.Combine(app.LibraryRoot, "A", "Four", "cover.jpg")));
        Assert.DoesNotContain(requests, r => r.Contains(release2));

        // A second run only asks for the albums that still have none.
        requests.Clear();
        await service.BackfillAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.Equal([$"/release/{release4}/front-1200", "/search"], requests);

        // A newly filed song gets the cover of its release, an existing file is kept.
        handler.Respond = _ => Image([0xFF, 0xD8, 7]);
        await service.EnsureForSongAsync(Path.Combine("A", "Four", "A - S.m4a"), null, release4, null, CancellationToken.None);
        await service.EnsureForSongAsync(Path.Combine("A", "One", "A - S.m4a"), null, release1, group1, CancellationToken.None);
        Assert.Equal([0xFF, 0xD8, 7], File.ReadAllBytes(Path.Combine(app.LibraryRoot, "A", "Four", "cover.jpg")));
        Assert.Equal(Jpeg, File.ReadAllBytes(Path.Combine(app.LibraryRoot, "A", "One", "cover.jpg")));
    }

    [Fact]
    public void TheFolderCoverFollowsTheLastSong()
    {
        using var app = new SubsonicTestHost();
        var service = app.Get<AlbumCoverService>();
        string Lib(params string[] parts) => Path.Combine([app.LibraryRoot, .. parts]);
        void Move(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Lib(to))!);
            File.Move(Lib(from), Lib(to));
            service.FollowSong(from, to);
        }

        foreach (var song in new[] { "A/Old/1.m4a", "A/Old/2.m4a", "A/Other/1.m4a" }) WriteCover(app, song, [1]);
        WriteCover(app, "A/Old/cover.jpg", Jpeg);
        WriteCover(app, "A/Other/folder.png", [2]);

        // Songs are left: the cover stays.
        Move("A/Old/1.m4a", "A/New/1.m4a");
        Assert.True(File.Exists(Lib("A", "Old", "cover.jpg")));
        Assert.False(File.Exists(Lib("A", "New", "cover.jpg")));

        // The last song takes it along.
        Move("A/Old/2.m4a", "A/New/2.m4a");
        Assert.Empty(Directory.GetFileSystemEntries(Lib("A", "Old")));
        Assert.Equal(Jpeg, File.ReadAllBytes(Lib("A", "New", "cover.jpg")));

        // The new folder has a cover: the old one goes to the trash, the existing one stays.
        Move("A/Other/1.m4a", "A/New/3.m4a");
        Assert.Empty(Directory.GetFileSystemEntries(Lib("A", "Other")));
        Assert.Equal(["1.m4a", "2.m4a", "3.m4a", "cover.jpg"], Directory.GetFiles(Lib("A", "New")).Select(Path.GetFileName).Order());
        Assert.Single(Directory.GetFiles(Path.Combine(app.Root, "trash"), "folder.png", SearchOption.AllDirectories));
    }

    private static HttpResponseMessage Image(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Respond(request));
    }
}
