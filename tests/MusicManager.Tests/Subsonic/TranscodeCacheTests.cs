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
        // Finished entries only: a transcode still being written is a .tmp file.
        return Directory.Exists(dir) ? Directory.GetFiles(dir).Where(f => !f.EndsWith(".tmp", StringComparison.Ordinal)).ToArray() : [];
    }

    /// <summary>The entry is committed right after the last byte; give the server that moment.</summary>
    private static async Task<string[]> CachedSoonAsync(SubsonicTestHost app, int expected, int seconds = 1)
    {
        for (var i = 0; i < seconds * 50 && Cached(app).Length < expected; i++) await Task.Delay(20);
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
    public async Task EachFormatAndBitrateHasItsOwnEntryAndAChangedFileReplacesThemAll()
    {
        if (!await FfmpegTestTools.AvailableAsync()) return;
        var (app, id, path) = await LibraryAsync();
        using var _app = app;
        var client = app.CreateClient();

        await client.GetByteArrayAsync(Url(id, "maxBitRate=64"));
        await client.GetByteArrayAsync(Url(id, "maxBitRate=96"));
        var before = await CachedSoonAsync(app, 2);
        Assert.Equal(2, before.Length);

        // Changed but not yet indexed: the old entries are not served, and writing the new one removes them.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        var response = await client.GetAsync(Url(id, "maxBitRate=64"), HttpCompletionOption.ResponseHeadersRead);
        Assert.Contains("none", response.Headers.AcceptRanges); // encoded again, not from the cache
        await response.Content.ReadAsByteArrayAsync();
        await Task.Delay(300);
        var after = Assert.Single(Cached(app));
        Assert.DoesNotContain(after, before);
    }

    [Fact]
    public async Task TheIndexForgetsTheEntriesOfAChangedOrDeletedOriginal()
    {
        if (!await FfmpegTestTools.AvailableAsync()) return;
        var (app, id, path) = await LibraryAsync();
        using var _app = app;
        var otherTags = Tags;
        otherTags.Title = "Other";
        otherTags.TrackNumber = 2;
        var other = app.AddFile("A/B/2.m4a", otherTags);
        await app.ScanAsync();
        var otherId = (await app.EntryAsync("A/B/2.m4a"))!.SongId!;
        var client = app.CreateClient();
        await client.GetByteArrayAsync(Url(id, "maxBitRate=64"));
        await client.GetByteArrayAsync(Url(id, "format=opus"));
        await client.GetByteArrayAsync(Url(otherId, "format=mp3"));
        Assert.Equal(3, (await CachedSoonAsync(app, 3)).Length);

        // Changed outside Music Manager and picked up by the scan: both entries of that song go, the other stays.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        await app.ScanAsync();
        var left = Assert.Single(Cached(app));
        Assert.EndsWith(".mp3", left);

        // Deleted: its entry goes with it.
        File.Delete(other);
        await app.ScanAsync();
        Assert.Empty(Cached(app));
    }

    [Fact]
    public async Task FilingAFileAnewForgetsItsEntries()
    {
        if (!await FfmpegTestTools.AvailableAsync()) return;
        var (app, id, _) = await LibraryAsync();
        using var _app = app;
        await app.CreateClient().GetByteArrayAsync(Url(id, "maxBitRate=64"));
        Assert.Single(await CachedSoonAsync(app, 1));

        // What the pipeline does after writing a file (retag, replacement by a duplicate "keep new").
        await app.Get<MusicManager.Core.Pipeline.LibraryIndexer>().UpsertAsync("A/B/1.m4a", null, default);

        Assert.Empty(Cached(app));
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

    [Fact]
    public void ForgetRemovesOnlyTheEntriesOfThatOriginal()
    {
        var data = Path.Combine(Path.GetTempPath(), $"mm-tc-{Guid.NewGuid():N}");
        try
        {
            var cache = new TranscodeCache(Options.Create(new MusicManagerOptions { DataPath = data }), NullLogger<TranscodeCache>.Instance);
            var music = Path.Combine(data, "music");
            Directory.CreateDirectory(music);
            var a = Path.Combine(music, "a.m4a");
            var b = Path.Combine(music, "b.m4a");
            File.WriteAllBytes(a, [1]);
            File.WriteAllBytes(b, [2]);
            async Task Write(string source, TranscodePlan plan)
            {
                await using var entry = cache.Begin(source, plan)!;
                await entry.Stream.WriteAsync(new byte[] { 9 });
                await entry.CommitAsync();
            }

            Write(a, new TranscodePlan(Transcoding.Mp3, 128)).GetAwaiter().GetResult();
            Write(a, new TranscodePlan(Transcoding.Opus, 96)).GetAwaiter().GetResult();
            Write(b, new TranscodePlan(Transcoding.Mp3, 128)).GetAwaiter().GetResult();
            Assert.Equal(3, Directory.GetFiles(cache.Root).Length);

            Assert.Equal(2, cache.Forget(a));

            Assert.Null(cache.Find(a, new TranscodePlan(Transcoding.Mp3, 128)));
            Assert.NotNull(cache.Find(b, new TranscodePlan(Transcoding.Mp3, 128)));
        }
        finally
        {
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }

    [Fact]
    public async Task ASongIsCachedCompletelyEvenWhenTheClientGoesAwayEarly()
    {
        if (!await FfmpegTestTools.AvailableAsync()) return;
        using var app = new SubsonicTestHost();
        var path = app.AddFile("A/B/1.m4a", Tags);
        // Two minutes of noise: still encoding when the client has gone after the first bytes.
        var noise = System.IO.Path.Combine(app.Root, "noise.m4a");
        Assert.Equal(0, (await ProcessRunner.RunAsync("ffmpeg",
            ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "anoisesrc=d=120:a=0.2", "-c:a", "aac", "-b:a", "256k", noise], default)).ExitCode);
        File.Copy(noise, path, overwrite: true);
        TagIo.WriteM4a(path, Tags, null, "musicbrainz");
        await app.ScanAsync();
        var id = (await app.EntryAsync("A/B/1.m4a"))!.SongId!;
        var client = app.CreateClient();

        // Read a little, then hang up (a skip in Symfonium).
        using (var response = await client.GetAsync(Url(id, "format=opus&maxBitRate=128"), HttpCompletionOption.ResponseHeadersRead))
        {
            await using var body = await response.Content.ReadAsStreamAsync();
            var start = new byte[4096];
            await body.ReadAtLeastAsync(start, start.Length);
        }

        var file = Assert.Single(await CachedSoonAsync(app, 1, seconds: 15));

        var seconds = await ProcessRunner.RunAsync("ffmpeg", ["-nostdin", "-hide_banner", "-i", file, "-f", "null", "-"], default);
        var time = System.Text.RegularExpressions.Regex.Matches(seconds.StdErr, @"time=00:(\d+):(\d+\.\d+)")[^1].Groups;
        Assert.InRange(int.Parse(time[1].Value) * 60 + double.Parse(time[2].Value, System.Globalization.CultureInfo.InvariantCulture), 119.5, 120.5);
        // And the next request is served from it.
        var next = await client.GetAsync(Url(id, "format=opus&maxBitRate=128"), HttpCompletionOption.ResponseHeadersRead);
        Assert.Contains("bytes", next.Headers.AcceptRanges);
    }
}
