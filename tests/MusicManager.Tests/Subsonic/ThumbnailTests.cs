using Microsoft.EntityFrameworkCore;
using MusicManager.Core.Data;
using MusicManager.Core.Model;
using MusicManager.Core.Pipeline;
using MusicManager.Core.Services;

namespace MusicManager.Tests.Subsonic;

/// <summary>Scaled-down covers for the pages: made once, kept in the cache, renewed when the source changes.</summary>
public class ThumbnailTests
{
    private static TagSet Tags(string title, int track = 1) => new()
    {
        Title = title, Artist = "A", Artists = ["A"], Album = "B", AlbumArtist = "A", AlbumArtists = ["A"],
        TrackNumber = track, TrackCount = 10, MbRecordingId = "rec", MbReleaseId = "rel",
    };

    private static string[] Cached(SubsonicTestHost app)
    {
        var dir = Path.Combine(app.DataRoot, "cache", "thumbnails");
        return Directory.Exists(dir) ? Directory.GetFiles(dir).Where(f => !f.EndsWith(".tmp", StringComparison.Ordinal)).ToArray() : [];
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(-5, null)]
    [InlineData(480, 480)]
    [InlineData(99999, ThumbnailCache.MaxSize)]
    public void SizesAreClamped(int? requested, int? expected) => Assert.Equal(expected, ThumbnailCache.Normalize(requested));

    [Fact]
    public async Task LibraryCoverIsScaledOnceAndRenewedWhenTheSongChanges()
    {
        if (!await FfmpegTestTools.AvailableAsync()) return;
        using var app = new SubsonicTestHost();
        var audio = app.Get<AudioTools>();
        var big = await FfmpegTestTools.ImageAsync(Path.Combine(app.Root, "big.jpg"), 600, 600);
        var song = app.AddFile("A/B/1.m4a", Tags("One"));
        TagIo.WriteM4a(song, Tags("One"), big, "musicbrainz");
        await app.ScanAsync();
        var id = (await app.EntryAsync("A/B/1.m4a"))!.Id;
        var client = app.CreateClient();

        var scaled = await client.GetByteArrayAsync($"/api/library/{id}/cover?size=50");
        Assert.Equal((50, 50), await audio.ImageSizeAsync(scaled, default));
        var entry = Assert.Single(Cached(app));
        Assert.EndsWith("-50.jpg", entry);

        // Served from the cache the second time; without size, the original.
        Assert.Equal(scaled, await client.GetByteArrayAsync($"/api/library/{id}/cover?size=50"));
        Assert.Single(Cached(app));
        Assert.Equal((600, 600), await audio.ImageSizeAsync(await client.GetByteArrayAsync($"/api/library/{id}/cover"), default));

        // A new cover: the scan forgets the old copies, the next request makes a new one.
        var other = await FfmpegTestTools.ImageAsync(Path.Combine(app.Root, "other.png"), 300, 300);
        TagIo.WriteM4a(song, Tags("One"), other, "musicbrainz");
        File.SetLastWriteTimeUtc(song, DateTime.UtcNow.AddMinutes(1));
        await app.ScanAsync();
        Assert.Empty(Cached(app));
        Assert.Equal((50, 50), await audio.ImageSizeAsync(await client.GetByteArrayAsync($"/api/library/{id}/cover?size=50"), default));
        Assert.NotEqual(entry, Assert.Single(Cached(app)));
    }

    [Fact]
    public async Task NewVersionOfAnImageReplacesTheOldCopies()
    {
        if (!await FfmpegTestTools.AvailableAsync()) return;
        using var app = new SubsonicTestHost();
        var cache = app.Get<ThumbnailCache>();
        var image = Path.Combine(app.Root, "a.jpg");
        await FfmpegTestTools.ImageAsync(image, 200, 200);

        var first = await cache.GetAsync(image, 32, default);
        Assert.NotNull(first);
        Assert.NotNull(await cache.GetAsync(image, 64, default));
        Assert.Equal(2, Cached(app).Length);

        await FfmpegTestTools.ImageAsync(image, 300, 300);
        File.SetLastWriteTimeUtc(image, DateTime.UtcNow.AddMinutes(1));
        var second = await cache.GetAsync(image, 32, default);

        Assert.NotEqual(first, second);
        Assert.Equal(second, Assert.Single(Cached(app)));
    }

    [Fact]
    public async Task ParallelRequestsShareOneCopy()
    {
        if (!await FfmpegTestTools.AvailableAsync()) return;
        using var app = new SubsonicTestHost();
        var cache = app.Get<ThumbnailCache>();
        var image = Path.Combine(app.Root, "a.jpg");
        await FfmpegTestTools.ImageAsync(image, 200, 200);

        var paths = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => cache.GetAsync(image, 32, default)));

        Assert.All(paths, p => Assert.Equal(paths[0], p));
        Assert.Single(Cached(app));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(paths[0]!)!, "*.tmp"));
    }

    [Fact]
    public async Task UnreadableSourceServesNoCopy()
    {
        using var app = new SubsonicTestHost();
        var cache = app.Get<ThumbnailCache>();
        var notAnImage = Path.Combine(app.Root, "a.jpg");
        await File.WriteAllTextAsync(notAnImage, "no picture");

        Assert.Null(await cache.GetAsync(notAnImage, 32, default));
        Assert.Null(await cache.GetAsync(Path.Combine(app.Root, "missing.jpg"), 32, default));
        Assert.Empty(Cached(app));
    }

    [Fact]
    public async Task TheRunMakesTheCopiesThePagesUseAndRemovesTheOldCache()
    {
        if (!await FfmpegTestTools.AvailableAsync()) return;
        using var app = new SubsonicTestHost();
        var square = await FfmpegTestTools.ImageAsync(Path.Combine(app.Root, "square.jpg"), 600, 600);
        var first = app.AddFile("A/B/1.m4a", Tags("One"));
        TagIo.WriteM4a(first, Tags("One"), square, "musicbrainz");
        var second = app.AddFile("A/B/2.m4a", Tags("Two", 2));
        TagIo.WriteM4a(second, Tags("Two", 2), square, "musicbrainz");
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "artists"));
        await FfmpegTestTools.ImageAsync(Path.Combine(app.DataRoot, "artists", "a.jpg"), 600, 600);
        await using (var db = await app.Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
        {
            db.ArtistImages.Add(new ArtistImage { Key = "a", Name = "A", Source = "deezer", FilePath = Path.Combine("artists", "a.jpg") });
            await db.SaveChangesAsync();
        }

        var legacy = Path.Combine(app.DataRoot, "cache", "covers");
        Directory.CreateDirectory(legacy);
        await File.WriteAllTextAsync(Path.Combine(legacy, "old.jpg"), "x");
        await app.ScanAsync();

        var service = app.Get<ImageSquareService>();
        await service.RunAsync(default);

        // Two song covers, preview and tile of the album (its first song) and of the artist.
        Assert.Equal(6, service.State.ThumbnailTotal);
        Assert.Equal(6, service.State.ThumbnailProgress);
        var names = Cached(app).Select(Path.GetFileName).ToList();
        Assert.Equal(2, names.Count(n => n!.EndsWith($"-{ThumbnailCache.SongSize}.jpg")));
        Assert.Equal(2, names.Count(n => n!.EndsWith($"-{ThumbnailCache.PreviewSize}.jpg")));
        Assert.Equal(2, names.Count(n => n!.EndsWith($"-{ThumbnailCache.TileSize}.jpg")));
        Assert.False(Directory.Exists(legacy));

        // The pages get exactly these copies; a second run only checks them.
        var id = LibraryCatalog.Albums(await app.Get<LibraryCatalog>().GetTracksAsync()).Single().CoverTrackId;
        var tile = await app.CreateClient().GetByteArrayAsync($"/api/library/{id}/cover?size={ThumbnailCache.TileSize}");
        Assert.Equal((ThumbnailCache.TileSize, ThumbnailCache.TileSize), await app.Get<AudioTools>().ImageSizeAsync(tile, default));
        await service.RunAsync(default);
        Assert.Equal(6, Cached(app).Length);
    }
}
