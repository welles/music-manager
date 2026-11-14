using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicManager.Core.Configuration;
using MusicManager.Core.Services;

namespace MusicManager.Tests;

/// <summary>
/// Converts a real damaged FLAC with ffmpeg. Needs ffmpeg/ffprobe on the PATH (the Docker image has them; locally
/// e.g. <c>nix shell nixpkgs#ffmpeg-headless -c dotnet test</c>). Without them these tests return early, as CI has no ffmpeg.
/// </summary>
public sealed class TruncatedConversionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mm-truncated-").FullName;
    private readonly AudioTools _audio = new(Options.Create(new MusicManagerOptions()), NullLogger<AudioTools>.Instance);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static async Task<bool> HasFfmpegAsync()
    {
        try
        {
            return (await ProcessRunner.RunAsync("ffmpeg", ["-version"], CancellationToken.None)).ExitCode == 0
                   && (await ProcessRunner.RunAsync("ffprobe", ["-version"], CancellationToken.None)).ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>20 s of noise as FLAC (noise does not compress, so cutting the file removes most of the audio).</summary>
    private async Task<string> IntactFlacAsync()
    {
        var path = Path.Combine(_dir, "intact.flac");
        var r = await ProcessRunner.RunAsync("ffmpeg",
            ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "anoisesrc=d=20:c=pink:r=44100", "-c:a", "flac", path], CancellationToken.None);
        Assert.Equal(0, r.ExitCode);
        return path;
    }

    [Fact]
    public async Task DamagedFlacIsNotConvertedCutShort()
    {
        if (!await HasFfmpegAsync()) return;

        // Keep the header (which still claims 20 s) and the first 30 % of the frames, like a broken download.
        var intact = await File.ReadAllBytesAsync(await IntactFlacAsync());
        var damaged = Path.Combine(_dir, "damaged.flac");
        await File.WriteAllBytesAsync(damaged, intact[..(intact.Length * 3 / 10)]);

        var info = await _audio.ProbeAsync(damaged, CancellationToken.None);
        Assert.InRange(info.DurationSeconds ?? 0, 19.5, 20.5);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _audio.ConvertToM4aAsync(damaged, Path.Combine(_dir, "out.m4a"), info.Codec, 256, info.DurationSeconds, CancellationToken.None));
        Assert.Contains("Umwandlung unvollständig", ex.Message);
    }

    [Fact]
    public async Task IntactFlacIsConverted()
    {
        if (!await HasFfmpegAsync()) return;

        var flac = await IntactFlacAsync();
        var info = await _audio.ProbeAsync(flac, CancellationToken.None);
        var output = Path.Combine(_dir, "out.m4a");

        await _audio.ConvertToM4aAsync(flac, output, info.Codec, 256, info.DurationSeconds, CancellationToken.None);

        Assert.InRange((await _audio.ProbeAsync(output, CancellationToken.None)).DurationSeconds ?? 0, 19.5, 20.5);
    }
}
