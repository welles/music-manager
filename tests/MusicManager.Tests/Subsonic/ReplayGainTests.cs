using MusicManager.Core.Model;
using MusicManager.Core.Pipeline;
using MusicManager.Core.Services;

namespace MusicManager.Tests.Subsonic;

/// <summary>Loudness as ReplayGain tags; the audio stays untouched (issue #14).</summary>
public class ReplayGainTests
{
    private static TagSet Tags(string title) => new()
    {
        Title = title, Artist = "A", Artists = ["A"], Album = "B", AlbumArtist = "A", AlbumArtists = ["A"],
        TrackNumber = 1, TrackCount = 10, MbRecordingId = "rec", MbReleaseId = "rel",
    };

    private const string Summary = """
        [Parsed_ebur128_0 @ 0x1] Summary:

          Integrated loudness:
            I:          -9.8 LUFS
            Threshold: -20.1 LUFS

          Loudness range:
            LRA:         5.2 LU

          True peak:
            Peak:       -0.3 dBFS
        """;

    [Fact]
    public void MeasurementBecomesGainAndPeak()
    {
        var rg = AudioTools.ParseReplayGain(Summary)!;
        Assert.Equal(-8.2, rg.TrackGain); // -18 LUFS target
        Assert.Equal(0.966051, rg.TrackPeak);
        Assert.Equal("-8.20 dB", rg.GainText);
        Assert.Equal("0.966051", rg.PeakText);
    }

    [Theory]
    [InlineData("no summary at all")]
    [InlineData("Integrated loudness:\n I: -70.0 LUFS\n True peak:\n Peak: -inf dBFS")] // silence
    public void UnmeasurableIsNull(string output) => Assert.Null(AudioTools.ParseReplayGain(output));

    [Theory]
    [InlineData("-6.20 dB", "0.988553", -6.2, 0.988553)]
    [InlineData("+3.5 dB", null, 3.5, 1.0)]
    [InlineData("-7", "0.5", -7.0, 0.5)]
    public void TagValuesAreParsed(string gain, string? peak, double expectedGain, double expectedPeak) =>
        Assert.Equal(new ReplayGain(expectedGain, expectedPeak), ReplayGain.Parse(gain, peak));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("loud")]
    public void MissingGainIsNotNormalized(string? gain) => Assert.Null(ReplayGain.Parse(gain, "0.9"));

    [Fact]
    public async Task TagsAreIndexedAndDoNotCountAsAnOutsideEdit()
    {
        using var app = new SubsonicTestHost();
        var path = app.AddFile("A/B/1.m4a", Tags("One"));

        TagIo.WriteReplayGain(path, new ReplayGain(-6.2, 0.9));

        Assert.False(TagIo.CheckHashes(path)!.Edited);
        await app.ScanAsync();
        var entry = (await app.EntryAsync("A/B/1.m4a"))!;
        Assert.Equal(-6.2, entry.ReplayGainTrackGain);
        Assert.Equal(0.9, entry.ReplayGainTrackPeak);
        Assert.False(entry.TagsEditedOutside);
    }

    [Fact]
    public void ReTaggingKeepsTheMeasuredLoudness()
    {
        using var app = new SubsonicTestHost();
        var path = app.AddFile("A/B/1.m4a", Tags("One"));
        TagIo.WriteReplayGain(path, new ReplayGain(-6.2, 0.9));

        TagIo.WriteM4a(path, Tags("Renamed"), null, "musicbrainz");

        Assert.Equal(new ReplayGain(-6.2, 0.9), TagIo.ReadReplayGain(TagFields.Read(path)));
        Assert.Equal("Renamed", TagIo.Read(path).Title);
    }

    [Fact]
    public async Task TheApiDeliversReplayGain()
    {
        using var app = new SubsonicTestHost();
        TagIo.WriteReplayGain(app.AddFile("A/B/1.m4a", Tags("One")), new ReplayGain(-6.2, 0.9));
        app.AddFile("A/B/2.m4a", Tags("Two"));
        await app.ScanAsync();

        var songs = (await app.CallAsync("search3", "query=&songCount=10")).GetProperty("searchResult3").GetProperty("song")
            .EnumerateArray().ToDictionary(s => s.GetProperty("title").GetString()!);

        var rg = songs["One"].GetProperty("replayGain");
        Assert.Equal(-6.2, rg.GetProperty("trackGain").GetDouble());
        Assert.Equal(0.9, rg.GetProperty("trackPeak").GetDouble());
        Assert.False(songs["Two"].TryGetProperty("replayGain", out _));
    }

    [Fact]
    public async Task LibrarySongsWithoutTheTagAreMeasured()
    {
        if (!await FfmpegTestTools.AvailableAsync()) return;
        using var app = new SubsonicTestHost();
        var measured = app.AddFile("A/B/1.m4a", Tags("One"));
        TagIo.WriteReplayGain(measured, new ReplayGain(-1, 0.5));
        var missing = app.AddFile("A/B/2.m4a", Tags("Two"));
        // A tone instead of the silent fixture: silence cannot be measured.
        var tone = Path.Combine(app.Root, "tone.m4a");
        Assert.Equal(0, (await ProcessRunner.RunAsync("ffmpeg",
            ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "sine=f=440:d=3", "-c:a", "aac", tone], default)).ExitCode);
        File.Copy(tone, missing, overwrite: true);
        TagIo.WriteM4a(missing, Tags("Two"), null, "musicbrainz");
        await app.ScanAsync();
        var measuredBytes = await File.ReadAllBytesAsync(measured);
        var service = app.Get<ReplayGainService>();
        Assert.Equal(1, await service.CountMissingAsync());

        await service.RunAsync(default);

        Assert.Equal(new ReplayGainState(false, 1, 1, 1, 0, 0), service.State);
        Assert.Equal(0, await service.CountMissingAsync());
        Assert.Equal(measuredBytes, await File.ReadAllBytesAsync(measured)); // has the tag: untouched
        var entry = (await app.EntryAsync("A/B/2.m4a"))!;
        Assert.InRange(entry.ReplayGainTrackGain!.Value, 1, 8); // a quiet sine: turned up
        Assert.Equal(TagIo.ReadReplayGain(TagFields.Read(missing))!.TrackGain, entry.ReplayGainTrackGain);
        var info = new FileInfo(missing);
        Assert.Equal(info.Length, entry.FileSize);
        Assert.Equal(info.LastWriteTimeUtc, entry.FileModifiedUtc);
        Assert.False(TagIo.CheckHashes(missing)!.Edited);
    }

    [Fact]
    public async Task ASilentSongStaysWithoutTheTag()
    {
        if (!await FfmpegTestTools.AvailableAsync()) return;
        using var app = new SubsonicTestHost();
        app.AddFile("A/B/1.m4a", Tags("Silence"));
        await app.ScanAsync();
        var service = app.Get<ReplayGainService>();

        await service.RunAsync(default);

        Assert.Equal(new ReplayGainState(false, 1, 1, 0, 0, 1), service.State);
        Assert.Null((await app.EntryAsync("A/B/1.m4a"))!.ReplayGainTrackGain);
    }
}
