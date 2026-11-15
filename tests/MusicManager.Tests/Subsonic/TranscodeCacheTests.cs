using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicManager.Core.Configuration;
using MusicManager.Core.Model;
using MusicManager.Core.Services;

namespace MusicManager.Tests.Subsonic;

/// <summary>Finished transcodes are kept and served again as files with length and ranges.</summary>
public class TranscodeCacheTests
{
    private static TagSet Tags => new()
    {
        Title = "Tone", Artist = "A", Artists = ["A"], Album = "B", AlbumArtist = "A", AlbumArtists = ["A"],
        TrackNumber = 1, TrackCount = 1, MbRecordingId = "rec", MbReleaseId = "rel",
    };

    private static async Task<(SubsonicTestHost App, string Id, string Path)> LibraryAsync()
    {
        var app = new SubsonicTestHost();
        var path = app.AddFile("A/B/1.m4a", Tags);
        var tone = System.IO.Path.Combine(app.Root, "tone.m4a");
        Assert.Equal(0, (await ProcessRunner.RunAsync("ffmpeg",
            ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "sine=f=440:d=10", "-c:a", "aac", "-b:a", "256k", tone], default)).ExitCode);
        File.Copy(tone, path, overwrite: true);
        TagIo.WriteM4a(path, Tags, null, "musicbrainz");
        await app.ScanAsync();
        return (app, (await app.EntryAsync("A/B/1.m4a"))!.SongId!, path);
    }

    private static string Url(string id, string query) => $"/rest/stream?apiKey={SubsonicTestHost.Key}&id={id}&{query}";

    private static string[] Cached(SubsonicTestHost app)
    {
        var dir = Path.Combine(app.DataRoot, "cache", "transcodes");
        return Directory.Exists(dir) ? Directory.GetFiles(dir) : [];
    }

    /// <summary>The entry is committed right after the last byte; give the server that moment.</summary>
    private static async Task<string[]> CachedSoonAsync(SubsonicTestHost app, int expected)
    {
        for (var i = 0; i < 50 && Cached(app).Length < expected; i++) await Task.Delay(20);
        return Cached(app);
    }

    [Fact]
    public async Task ASecondRequestComesFromTheCacheWithLengthAndRanges()
    {
        if (!await FfmpegTestTools.AvailableAsync()) return;
        var (app, id, _) = await LibraryAsync();
        using var _app = app;
        var client = app.CreateClient();

        var first = await client.GetAsync(Url(id, "format=opus&maxBitRate=96"), HttpCompletionOption.ResponseHeadersRead);
        Assert.Contains("none", first.Headers.AcceptRanges); // encoded while sent
        var encoded = await first.Content.ReadAsByteArrayAsync();
        var file = Assert.Single(await CachedSoonAsync(app, 1));
        Assert.EndsWith(".opus", file);
        Assert.Equal(encoded, await File.ReadAllBytesAsync(file));

        var second = await client.GetAsync(Url(id, "format=opus&maxBitRate=96"), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("audio/ogg", second.Content.Headers.ContentType?.MediaType);
        Assert.Equal(encoded.Length, second.Content.Headers.ContentLength);
        Assert.Contains("bytes", second.Headers.AcceptRanges);
        Assert.Equal(encoded, await second.Content.ReadAsByteArrayAsync());

        var request = new HttpRequestMessage(HttpMethod.Get, Url(id, "format=opus&maxBitRate=96"));
        request.Headers.Range = new RangeHeaderValue(100, 199);
        var partial = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(encoded[100..200], await partial.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task EachFormatAndBitrateHasItsOwnEntryAndAChangedFileIsEncodedAgain()
    {
        if (!await FfmpegTestTools.AvailableAsync()) return;
        var (app, id, path) = await LibraryAsync();
        using var _app = app;
        var client = app.CreateClient();

        await client.GetByteArrayAsync(Url(id, "maxBitRate=64"));
        await client.GetByteArrayAsync(Url(id, "maxBitRate=96"));
        Assert.Equal(2, (await CachedSoonAsync(app, 2)).Length);

        // A retag or replacement changes the file: the old entry must not be served for it.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        await client.GetByteArrayAsync(Url(id, "maxBitRate=64"));
        Assert.Equal(3, (await CachedSoonAsync(app, 3)).Length);
    }

    [Fact]
    public async Task AJumpIntoTheSongIsNeitherCachedNorServedFromTheCache()
    {
        if (!await FfmpegTestTools.AvailableAsync()) return;
        var (app, id, _) = await LibraryAsync();
        using var _app = app;
        var client = app.CreateClient();

        await client.GetByteArrayAsync(Url(id, "maxBitRate=64&timeOffset=6"));
        await Task.Delay(200);
        Assert.Empty(Cached(app));

        var whole = await client.GetByteArrayAsync(Url(id, "maxBitRate=64"));
        await CachedSoonAsync(app, 1);
        var later = await client.GetByteArrayAsync(Url(id, "maxBitRate=64&timeOffset=6"));
        Assert.True(later.Length < whole.Length / 2, "the offset must still apply with a cached song");
    }

    [Fact]
    public async Task AFailedTranscodeLeavesNothingInTheCache()
    {
        if (!await FfmpegTestTools.AvailableAsync()) return;
        var (app, id, path) = await LibraryAsync();
        using var _app = app;
        // Garbage behind a valid start: ffmpeg gives up with an error.
        var bytes = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, bytes[..2000].Concat(new byte[200_000]).ToArray());

        await app.CreateClient().GetByteArrayAsync(Url(id, "maxBitRate=64"));
        await Task.Delay(300);

        Assert.Empty(Cached(app));
    }

    [Fact]
    public async Task WithTheCacheOffNothingIsWritten()
    {
        if (!await FfmpegTestTools.AvailableAsync()) return;
        var (app, id, _) = await LibraryAsync();
        using var _app = app;
        using var off = app.WithWebHostBuilder(b => b.UseSetting("MusicManager:TranscodeCacheMb", "0"));

        var body = await off.CreateClient().GetByteArrayAsync(Url(id, "maxBitRate=64"));
        await Task.Delay(300);

        Assert.NotEmpty(body);
        Assert.Empty(Cached(app));
    }

    [Fact]
    public void TrimKeepsTheMostRecentlyUsedWithinTheLimit()
    {
        var data = Path.Combine(Path.GetTempPath(), $"mm-tc-{Guid.NewGuid():N}");
        try
        {
            var cache = new TranscodeCache(Options.Create(new MusicManagerOptions { DataPath = data, TranscodeCacheMb = 1 }),
                NullLogger<TranscodeCache>.Instance);
            Directory.CreateDirectory(cache.Root);
            var now = DateTime.UtcNow;
            string Add(string name, int kb, int minutesAgo)
            {
                var file = Path.Combine(cache.Root, name);
                File.WriteAllBytes(file, new byte[kb * 1024]);
                File.SetLastWriteTimeUtc(file, now.AddMinutes(-minutesAgo));
                return file;
            }

            var newest = Add("a.opus", 400, 1);
            var middle = Add("b.opus", 400, 5);
            var oldest = Add("c.opus", 400, 10);
            var staleTmp = Add("d.opus.x.tmp", 1, 120);
            var freshTmp = Add("e.opus.x.tmp", 1, 0);

            cache.Trim();

            Assert.True(File.Exists(newest));
            Assert.True(File.Exists(middle));
            Assert.False(File.Exists(oldest)); // 1200 KB > 1 MB: the one used longest ago goes
            Assert.False(File.Exists(staleTmp));
            Assert.True(File.Exists(freshTmp)); // may still be written
        }
        finally
        {
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }
}
