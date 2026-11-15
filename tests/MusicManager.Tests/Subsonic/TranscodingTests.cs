using System.Globalization;
using System.Net;
using System.Text.Json;
using MusicManager.Core.Data;
using MusicManager.Core.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MusicManager.Core.Services;

namespace MusicManager.Tests.Subsonic;

/// <summary>stream transcodes on request (maxBitRate, format, timeOffset) so skipping is fast over a slow uplink (issue #17).</summary>
public class TranscodingTests
{
    // ---- When and to what (no ffmpeg needed) ----

    [Fact]
    public void WithoutParametersTheOriginalIsServed() =>
        Assert.Null(Transcoding.Decide("m4a", 256, null, null));

    [Fact]
    public void ALimitAboveTheFileBitrateServesTheOriginal() =>
        Assert.Null(Transcoding.Decide("m4a", 256, null, 320));

    [Fact]
    public void ALimitBelowTheFileBitrateTranscodesToMp3ByDefault() =>
        Assert.Equal(new TranscodePlan(Transcoding.Mp3, 128), Transcoding.Decide("m4a", 256, null, 128));

    [Fact]
    public void ARequestedFormatIsUsedWithItsDefaultBitrate() =>
        Assert.Equal(new TranscodePlan(Transcoding.Opus, 128), Transcoding.Decide("m4a", 256, "opus", null));

    [Fact]
    public void TheBitrateNeverGoesAboveTheFile() =>
        Assert.Equal(new TranscodePlan(Transcoding.Mp3, 96), Transcoding.Decide("m4a", 96, "mp3", 320));

    [Fact]
    public void TheSameFormatWithinTheLimitServesTheOriginal()
    {
        Assert.Null(Transcoding.Decide("m4a", 256, "aac", 320));
        Assert.Null(Transcoding.Decide("mp3", 192, "mp3", 0));
    }

    [Fact]
    public void RawAlwaysServesTheOriginal() =>
        Assert.Null(Transcoding.Decide("m4a", 256, "raw", 64));

    [Fact]
    public void AnUnknownFormatIsIgnored()
    {
        Assert.Null(Transcoding.Decide("m4a", 256, "flv", null));
        Assert.Equal(new TranscodePlan(Transcoding.Mp3, 64), Transcoding.Decide("m4a", 256, "flv", 64));
    }

    [Fact]
    public void AnUnknownFileBitrateIsTreatedAsTooHigh() =>
        Assert.Equal(new TranscodePlan(Transcoding.Mp3, 128), Transcoding.Decide("m4a", null, null, 128));

    [Fact]
    public void TheBitrateStaysWithinSensibleBounds()
    {
        Assert.Equal(Transcoding.MinKbps, Transcoding.Decide("m4a", 256, null, 8)!.Kbps);
        Assert.Equal(Transcoding.MaxKbps, Transcoding.Decide("flac", 900, "mp3", 1000)!.Kbps);
    }

    [Fact]
    public void AnOffsetSeeksBeforeTheInput()
    {
        var args = Transcoding.Arguments("/music/a.m4a", new TranscodePlan(Transcoding.Opus, 96), 42);
        Assert.True(args.ToList().IndexOf("-ss") < args.ToList().IndexOf("-i"));
        Assert.Contains("42", args);
        Assert.Contains("96k", args);
        Assert.Equal("pipe:1", args[^1]);
    }

    // ---- Through the API with real audio (needs ffmpeg) ----

    private static TagSet Tags => new()
    {
        Title = "Tone", Artist = "A", Artists = ["A"], Album = "B", AlbumArtist = "A", AlbumArtists = ["A"],
        TrackNumber = 1, TrackCount = 1, MbRecordingId = "rec", MbReleaseId = "rel",
    };

    /// <summary>A library with one AAC song at 256 kbit/s (10 s by default); returns the host, the song ID and the file.</summary>
    private static async Task<(SubsonicTestHost App, string Id, string Path)> LibraryAsync(int seconds = 10)
    {
        var app = new SubsonicTestHost();
        var path = app.AddFile("A/B/1.m4a", Tags);
        var tone = System.IO.Path.Combine(app.Root, "tone.m4a");
        Assert.Equal(0, (await ProcessRunner.RunAsync("ffmpeg",
            ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", $"sine=f=440:d={seconds}", "-c:a", "aac", "-b:a", "256k", tone], default)).ExitCode);
        File.Copy(tone, path, overwrite: true);
        TagIo.WriteM4a(path, Tags, null, "musicbrainz");
        await app.ScanAsync();
        var songs = await app.CallAsync("getRandomSongs", "size=1");
        var id = songs.GetProperty("randomSongs").GetProperty("song")[0].GetProperty("id").GetString()!;
        return (app, id, path);
    }

    private static string Url(string id, string query) => $"/rest/stream?apiKey={SubsonicTestHost.Key}&id={id}&{query}";

    /// <summary>Codec name and duration of what came back, read with ffprobe.</summary>
    private static async Task<(string Codec, double Seconds)> ProbeAsync(SubsonicTestHost app, byte[] body, string suffix)
    {
        var file = System.IO.Path.Combine(app.Root, $"out-{Guid.NewGuid():N}.{suffix}");
        await File.WriteAllBytesAsync(file, body);
        var r = await ProcessRunner.RunAsync("ffprobe",
            ["-v", "error", "-show_entries", "stream=codec_name:format=duration", "-of", "json", file], default);
        Assert.Equal(0, r.ExitCode);
        using var doc = JsonDocument.Parse(r.StdOut);
        var codec = doc.RootElement.GetProperty("streams")[0].GetProperty("codec_name").GetString()!;
        var seconds = double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        return (codec, seconds);
    }

    [Fact]
    public async Task ALowerBitrateIsTranscodedWhileSent()
    {
        if (!await FfmpegTestTools.AvailableAsync()) return;
        var (app, id, path) = await LibraryAsync();
        using var _ = app;

        var response = await app.CreateClient().GetAsync(Url(id, "maxBitRate=64"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("none", response.Headers.AcceptRanges);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.True(body.Length < new FileInfo(path).Length / 2, $"{body.Length} bytes is not smaller");
        var (codec, seconds) = await ProbeAsync(app, body, "mp3");
        Assert.Equal("mp3", codec);
        Assert.InRange(seconds, 9.5, 10.5);
    }

    [Fact]
    public async Task OpusIsSentInOgg()
    {
        if (!await FfmpegTestTools.AvailableAsync()) return;
        var (app, id, _) = await LibraryAsync();
        using var _app = app;

        var response = await app.CreateClient().GetAsync(Url(id, "format=opus&maxBitRate=96"));

        Assert.Equal("audio/ogg", response.Content.Headers.ContentType?.MediaType);
        var (codec, seconds) = await ProbeAsync(app, await response.Content.ReadAsByteArrayAsync(), "opus");
        Assert.Equal("opus", codec);
        Assert.InRange(seconds, 9.5, 10.5);
    }

    [Fact]
    public async Task TimeOffsetStartsLaterInTheSong()
    {
        if (!await FfmpegTestTools.AvailableAsync()) return;
        var (app, id, _) = await LibraryAsync();
        using var _app = app;

        var response = await app.CreateClient().GetAsync(Url(id, "maxBitRate=64&timeOffset=6"));

        var (_, seconds) = await ProbeAsync(app, await response.Content.ReadAsByteArrayAsync(), "mp3");
        Assert.InRange(seconds, 3.5, 4.5);
        var extensions = (await app.CallAsync("getOpenSubsonicExtensions")).GetProperty("openSubsonicExtensions");
        Assert.Contains(extensions.EnumerateArray(), e => e.GetProperty("name").GetString() == "transcodeOffset");
    }

    [Fact]
    public async Task AnEstimatedLengthIsKeptExactly()
    {
        if (!await FfmpegTestTools.AvailableAsync()) return;
        var (app, id, _) = await LibraryAsync();
        using var _app = app;

        var response = await app.CreateClient().GetAsync(Url(id, "maxBitRate=64&estimateContentLength=true"));

        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(response.Content.Headers.ContentLength, body.Length);
        Assert.Equal(Transcoding.EstimateBytes(new TranscodePlan(Transcoding.Mp3, 64), 10, 0), body.Length);
        // ffprobe estimates an MP3's length from its size (padding included), so the audio is decoded instead.
        Assert.InRange(await DecodedSecondsAsync(app, body, "mp3"), 9.5, 10.5);
    }

    private static async Task<double> DecodedSecondsAsync(SubsonicTestHost app, byte[] body, string suffix)
    {
        var file = System.IO.Path.Combine(app.Root, $"out-{Guid.NewGuid():N}.{suffix}");
        await File.WriteAllBytesAsync(file, body);
        var r = await ProcessRunner.RunAsync("ffmpeg", ["-nostdin", "-hide_banner", "-i", file, "-f", "null", "-"], default);
        Assert.Equal(0, r.ExitCode);
        var time = System.Text.RegularExpressions.Regex.Matches(r.StdErr, @"time=(\d+):(\d+):(\d+\.\d+)")[^1].Groups;
        return int.Parse(time[1].Value) * 3600 + int.Parse(time[2].Value) * 60 + double.Parse(time[3].Value, CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task TheOriginalKeepsItsRangesAndDownloadIsNeverTranscoded()
    {
        if (!await FfmpegTestTools.AvailableAsync()) return;
        var (app, id, path) = await LibraryAsync();
        using var _app = app;
        var original = await File.ReadAllBytesAsync(path);
        var client = app.CreateClient();

        Assert.Equal(original, await client.GetByteArrayAsync(Url(id, "format=raw&maxBitRate=64")));
        Assert.Equal(original, await client.GetByteArrayAsync($"/rest/download?apiKey={SubsonicTestHost.Key}&id={id}&maxBitRate=64"));
        var request = new HttpRequestMessage(HttpMethod.Get, Url(id, ""));
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 99);
        var partial = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(original[..100], await partial.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task WithoutFfmpegTheOriginalIsServed()
    {
        using var app = new SubsonicTestHost();
        var path = app.AddFile("A/B/1.m4a", Tags);
        await app.ScanAsync();
        var id = (await app.EntryAsync("A/B/1.m4a"))!.SongId;
        using var noFfmpeg = app.WithWebHostBuilder(b => b.UseSetting("MusicManager:FfmpegPath", "/nonexistent/ffmpeg"));

        var response = await noFfmpeg.CreateClient().GetAsync(Url(id!, "maxBitRate=64&format=mp3"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/mp4", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(await File.ReadAllBytesAsync(path), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AacWithAnEstimatedLengthKeepsTheEnd()
    {
        if (!await FfmpegTestTools.AvailableAsync()) return;
        // At a low bitrate the ADTS frame headers are a noticeable share; a minute makes a cut-off end measurable.
        var (app, id, _) = await LibraryAsync(seconds: 60);
        using var _app = app;

        var response = await app.CreateClient().GetAsync(Url(id, "format=aac&maxBitRate=32&estimateContentLength=true"));

        Assert.Equal("audio/aac", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(response.Content.Headers.ContentLength, body.Length);
        Assert.InRange(await DecodedSecondsAsync(app, body, "aac"), 59.5, 60.5);
    }

    [Fact]
    public async Task HeadOnlyAnswersWithTheHeaders()
    {
        // No ffmpeg needed: HEAD must not start an encoder for a body nobody receives.
        using var app = new SubsonicTestHost();
        app.AddFile("A/B/1.m4a", Tags);
        await app.ScanAsync();
        var id = (await app.EntryAsync("A/B/1.m4a"))!.SongId!;

        var response = await app.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Head,
            Url(id, "format=mp3&estimateContentLength=true")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("none", response.Headers.AcceptRanges);
        Assert.NotNull(response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AnInvalidParameterIsRefusedBeforeEncoding()
    {
        using var app = new SubsonicTestHost();
        app.AddFile("A/B/1.m4a", Tags);
        await app.ScanAsync();
        var id = (await app.EntryAsync("A/B/1.m4a"))!.SongId!;

        var response = await app.CreateClient().GetAsync(Url(id, "format=mp3&estimateContentLength=yes&f=json"));

        Assert.Equal("failed", (await SubsonicTestHost.ReadAsync(response)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task AnUnknownDurationAnnouncesNoLengthAndSendsTheWholeSong()
    {
        if (!await FfmpegTestTools.AvailableAsync()) return;
        using var app = new SubsonicTestHost();
        var path = System.IO.Path.Combine(app.LibraryRoot, "A", "B", "1.m4a");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        Assert.Equal(0, (await ProcessRunner.RunAsync("ffmpeg",
            ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "sine=f=440:d=10", "-c:a", "aac", path], default)).ExitCode);
        // As for a file TagLib cannot read: no duration, no bitrate in the index.
        await app.SeedAsync([new LibraryEntry
        {
            RelativePath = "A/B/1.m4a", SongId = "tr-1", Title = "One", Artist = "A", Artists = ["A"], AlbumArtist = "A",
            AlbumArtists = ["A"], Album = "B", DurationSeconds = null, BitrateKbps = null, FileSize = new FileInfo(path).Length,
        }]);
        var client = app.CreateClient();

        // Headers as sent: a buffered HttpClient response fills in Content-Length itself.
        var response = await client.GetAsync(Url("tr-1", "maxBitRate=64&estimateContentLength=true"), HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Content.Headers.ContentLength);
        Assert.InRange(await DecodedSecondsAsync(app, await response.Content.ReadAsByteArrayAsync(), "mp3"), 9.5, 10.5);
        // Seeking still works without a known length.
        var later = await client.GetByteArrayAsync(Url("tr-1", "maxBitRate=64&timeOffset=6"));
        Assert.InRange(await DecodedSecondsAsync(app, later, "mp3"), 3.5, 4.5);
    }

    [Fact]
    public void AJumpIntoTheOriginalKeepsItsFormatAndBitrate()
    {
        Assert.Equal(new TranscodePlan(Transcoding.Aac, 256), Transcoding.ForOffset("m4a", 256));
        Assert.Equal(new TranscodePlan(Transcoding.Mp3, Transcoding.MaxKbps), Transcoding.ForOffset("flac", 900));
        Assert.Equal(new TranscodePlan(Transcoding.Opus, 128), Transcoding.ForOffset("opus", null));
    }

    [Fact]
    public async Task TimeOffsetAlsoWorksWithoutTranscoding()
    {
        if (!await FfmpegTestTools.AvailableAsync()) return;
        var (app, id, path) = await LibraryAsync();
        using var _app = app;
        var client = app.CreateClient();

        // A client that knows "transcodeOffset" seeks this way even in the original.
        var response = await client.GetAsync(Url(id, "timeOffset=6"));

        Assert.Equal("audio/aac", response.Content.Headers.ContentType?.MediaType);
        Assert.InRange(await DecodedSecondsAsync(app, await response.Content.ReadAsByteArrayAsync(), "aac"), 3.5, 4.5);
        // raw stays the untouched file, offset or not.
        Assert.Equal(await File.ReadAllBytesAsync(path), await client.GetByteArrayAsync(Url(id, "format=raw&timeOffset=6")));
    }
}
