using MusicManager.Core.Services;

namespace MusicManager.Tests;

/// <summary>
/// Helpers for tests that need ffmpeg/ffprobe on the PATH. The Docker image has them, CI has not; locally e.g.
/// <c>nix shell nixpkgs#ffmpeg-headless -c dotnet test</c>. Tests using them return early without ffmpeg.
/// </summary>
public static class FfmpegTestTools
{
    private static readonly Lazy<Task<bool>> Present = new(async () =>
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
    });

    public static Task<bool> AvailableAsync() => Present.Value;

    /// <summary>A solid image of the given size; the format follows the extension of <paramref name="path"/>.</summary>
    public static async Task<byte[]> ImageAsync(string path, int width, int height)
    {
        var r = await ProcessRunner.RunAsync("ffmpeg",
            ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", $"color=c=red:s={width}x{height}", "-frames:v", "1", path],
            CancellationToken.None);
        Assert.Equal(0, r.ExitCode);
        return await File.ReadAllBytesAsync(path);
    }
}
