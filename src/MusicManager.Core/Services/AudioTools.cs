using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MusicManager.Core.Configuration;

namespace MusicManager.Core.Services;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* ignored */ }
            throw;
        }

        return new ProcessResult(p.ExitCode, await stdout, await stderr);
    }
}

public sealed record AudioInfo(string? Codec, double? DurationSeconds, int? BitrateKbps, bool HasVideo);

public sealed record Fingerprint(double DurationSeconds, string Value);

/// <summary>Wrapper around ffprobe, ffmpeg and fpcalc.</summary>
public sealed class AudioTools(IOptions<MusicManagerOptions> options, ILogger<AudioTools> logger)
{
    private readonly MusicManagerOptions _o = options.Value;

    /// <summary>Detects codec and duration from the stream, not from the file extension.</summary>
    public async Task<AudioInfo> ProbeAsync(string path, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(_o.FfprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path], ct);
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe failed: {r.StdErr.Trim()}");

        using var doc = JsonDocument.Parse(r.StdOut);
        string? codec = null;
        int? bitrate = null;
        var hasVideo = false;
        if (doc.RootElement.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.GetProperty("codec_type").GetString();
                // Embedded cover art is reported as a video stream with attached_pic disposition.
                var isCover = s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                if (type == "audio" && codec == null)
                {
                    codec = s.GetProperty("codec_name").GetString();
                    if (s.TryGetProperty("bit_rate", out var br) && long.TryParse(br.GetString(), out var b))
                        bitrate = (int)(b / 1000);
                }
                else if (type == "video" && !isCover)
                {
                    hasVideo = true;
                }
            }
        }

        double? duration = null;
        if (doc.RootElement.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), CultureInfo.InvariantCulture, out var dur))
                duration = dur;
            if (bitrate == null && format.TryGetProperty("bit_rate", out var fbr) && long.TryParse(fbr.GetString(), out var fb))
                bitrate = (int)(fb / 1000);
        }

        return new AudioInfo(codec, duration, bitrate, hasVideo);
    }

    public async Task<Fingerprint?> FingerprintAsync(string path, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(_o.FpcalcPath, ["-json", path], ct);
        if (r.ExitCode != 0)
        {
            logger.LogWarning("fpcalc failed for {Path}: {Error}", path, r.StdErr.Trim());
            return null;
        }

        using var doc = JsonDocument.Parse(r.StdOut);
        return new Fingerprint(doc.RootElement.GetProperty("duration").GetDouble(), doc.RootElement.GetProperty("fingerprint").GetString()!);
    }

    /// <summary>Raw Chromaprint values (first 120 s) for local duplicate comparisons.</summary>
    public async Task<(double Duration, uint[] Values)?> RawFingerprintAsync(string path, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(_o.FpcalcPath, ["-raw", "-json", path], ct);
        if (r.ExitCode != 0)
        {
            logger.LogWarning("fpcalc -raw failed for {Path}: {Error}", path, r.StdErr.Trim());
            return null;
        }

        using var doc = JsonDocument.Parse(r.StdOut);
        var values = doc.RootElement.GetProperty("fingerprint").EnumerateArray().Select(v => unchecked((uint)v.GetInt64())).ToArray();
        return (doc.RootElement.GetProperty("duration").GetDouble(), values);
    }

    /// <summary>
    /// Starts encoding <paramref name="input"/> on the fly (see <see cref="PumpAsync"/>). Null when ffmpeg cannot be
    /// started at all, so the caller can still serve the original file.
    /// </summary>
    public Process? StartTranscode(string input, TranscodePlan plan, int offsetSeconds)
    {
        var psi = new ProcessStartInfo(_o.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in Transcoding.Arguments(input, plan, offsetSeconds)) psi.ArgumentList.Add(a);
        try
        {
            return Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            logger.LogWarning(ex, "ffmpeg could not be started for transcoding");
            return null;
        }
    }

    /// <summary>
    /// Copies the encoder's output to <paramref name="output"/> while it runs, so the first bytes go out within
    /// milliseconds. Stops ffmpeg when the client goes away (<paramref name="ct"/>). Returns whether ffmpeg finished
    /// without an error, i.e. the output is complete.
    /// </summary>
    public async Task<bool> PumpAsync(Process ffmpeg, string input, Stream output, CancellationToken ct)
    {
        using var p = ffmpeg;
        // Read stderr alongside, or a chatty ffmpeg blocks once the pipe is full.
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        long written = 0;
        try
        {
            var buffer = new byte[64 * 1024];
            var source = p.StandardOutput.BaseStream;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;
            }

            await p.WaitForExitAsync(ct);
        }
        catch (Exception) when (!p.HasExited)
        {
            // Client gone (skipped to the next song) or the write failed: no need to encode any further.
            try { p.Kill(true); } catch { /* ignored */ }
            throw;
        }

        if (p.ExitCode == 0) return true;
        logger.LogWarning("Transcoding {Input} failed after {Bytes} bytes: {Error}", input, written, (await stderr).Trim());
        return false;
    }

    /// <summary>
    /// Converts any input to M4A/AAC. AAC streams are only remuxed (no re-encoding);
    /// everything else is encoded with the native AAC encoder. The moov atom is moved to
    /// the front for streaming. Metadata and pictures are dropped, tags are written afterwards.
    /// </summary>
    public async Task<bool> ConvertToM4aAsync(string input, string output, string? codec, int bitrateKbps, double? sourceSeconds,
        CancellationToken ct)
    {
        var copy = string.Equals(codec, "aac", StringComparison.OrdinalIgnoreCase);
        var args = new List<string>
        {
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
            "-i", input,
            "-map", "0:a:0", "-map_metadata", "-1", "-map_chapters", "-1", "-vn", "-sn", "-dn",
        };
        if (copy)
            args.AddRange(["-c:a", "copy"]);
        else
            args.AddRange(["-c:a", "aac", "-b:a", $"{bitrateKbps}k"]);
        args.AddRange(["-movflags", "+faststart", "-f", "ipod", output]);

        var r = await ProcessRunner.RunAsync(_o.FfmpegPath, args, ct);
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed: {r.StdErr.Trim()}");

        // ffmpeg stops at a damaged spot in the source and still exits with 0; the result would be filed cut short
        // and the original moved to the trash.
        var converted = (await ProbeAsync(output, ct)).DurationSeconds;
        var detail = r.StdErr.Trim();
        if (IsTruncated(sourceSeconds, converted, detail))
        {
            throw new InvalidOperationException(
                $"Umwandlung unvollständig: {converted:0} s statt {sourceSeconds:0} s – die Quelldatei ist vermutlich beschädigt"
                + (detail.Length > 0 ? $" ({(detail.Length > 300 ? detail[..300] : detail)})" : ""));
        }

        return copy;
    }

    /// <summary>
    /// ffmpeg reported errors and the converted file is clearly shorter than the source claims to be (more than 1 s and
    /// 1 %). Both are needed: for raw ADTS AAC and VBR MP3s without a Xing/VBRI header ffprobe only estimates the source
    /// length from the bitrate, so an intact file can come out shorter, but then without decoding errors.
    /// </summary>
    internal static bool IsTruncated(double? sourceSeconds, double? convertedSeconds, string ffmpegErrors) =>
        !string.IsNullOrWhiteSpace(ffmpegErrors)
        && sourceSeconds is > 0 && convertedSeconds is { } c && sourceSeconds.Value - c > Math.Max(1.0, sourceSeconds.Value * 0.01);

    private static readonly HashSet<string> BrowserFormats = [".mp3", ".m4a", ".mp4", ".aac", ".flac", ".ogg", ".oga", ".opus", ".wav", ".webm"];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double?> _loudness = new();

    /// <summary>
    /// A file the browser can play: the file itself, or an AAC transcode cached in the work folder
    /// (so seeking with range requests works for it as well).
    /// </summary>
    public async Task<string> BrowserPlayableAsync(string path, CancellationToken ct)
    {
        if (BrowserFormats.Contains(Path.GetExtension(path).ToLowerInvariant())) return path;

        var info = new FileInfo(path);
        var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes($"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")));
        var dir = Path.GetFullPath(Path.Combine(_o.WorkPath, "preview"));
        Directory.CreateDirectory(dir);
        var cached = Path.Combine(dir, key + ".m4a");
        if (File.Exists(cached)) return cached;

        var tmp = cached + ".tmp";
        var r = await ProcessRunner.RunAsync(_o.FfmpegPath,
            ["-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", path, "-map", "0:a:0", "-vn", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-f", "ipod", tmp], ct);
        if (r.ExitCode != 0) throw new InvalidOperationException($"ffmpeg failed: {r.StdErr.Trim()}");
        File.Move(tmp, cached, true);
        return cached;
    }

    /// <summary>Integrated loudness (EBU R128, LUFS) – used to play both versions equally loud.</summary>
    public async Task<double?> LoudnessAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        var key = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        if (_loudness.TryGetValue(key, out var cached)) return cached;

        var r = await ProcessRunner.RunAsync(_o.FfmpegPath,
            ["-nostdin", "-hide_banner", "-nostats", "-i", path, "-map", "0:a:0", "-af", "ebur128=framelog=quiet", "-f", "null", "-"], ct);
        double? lufs = null;
        var m = System.Text.RegularExpressions.Regex.Match(r.StdErr, @"Integrated loudness:\s*I:\s*(-?[\d.]+) LUFS");
        if (m.Success && double.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var v)) lufs = v;
        _loudness[key] = lufs;
        return lufs;
    }

    /// <summary>
    /// Measures the loudness of a file (EBU R128 integrated loudness and true peak) and returns it as ReplayGain
    /// values (<see cref="Model.ReplayGain.Silence"/> for a silent file). Null when ffmpeg cannot measure it.
    /// </summary>
    public async Task<Model.ReplayGain?> MeasureReplayGainAsync(string path, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(_o.FfmpegPath,
            ["-nostdin", "-hide_banner", "-nostats", "-i", path, "-map", "0:a:0", "-af", "ebur128=peak=true:framelog=quiet", "-f", "null", "-"], ct);
        return r.ExitCode == 0 ? ParseReplayGain(r.StdErr) : null;
    }

    /// <summary>The summary ffmpeg's ebur128 filter prints at the end ("I: -9.8 LUFS", "Peak: -0.3 dBFS").</summary>
    internal static Model.ReplayGain? ParseReplayGain(string ffmpegOutput)
    {
        var summary = ffmpegOutput.LastIndexOf("Integrated loudness:", StringComparison.Ordinal);
        if (summary < 0) return null;
        var text = ffmpegOutput[summary..];
        var loudness = System.Text.RegularExpressions.Regex.Match(text, @"\bI:\s*(-?[\d.]+) LUFS");
        var peak = System.Text.RegularExpressions.Regex.Match(text, @"True peak:\s*Peak:\s*(-inf|-?[\d.]+) dBFS");
        if (!loudness.Success || !peak.Success || !double.TryParse(loudness.Groups[1].Value, CultureInfo.InvariantCulture, out var lufs))
            return null;
        // Silence (-70 LUFS is the filter's floor, the peak "-inf"): nothing to level, but measured. It gets the
        // neutral gain, so the song counts as normalized and is not measured again on every run.
        if (lufs <= -70 || !double.TryParse(peak.Groups[1].Value, CultureInfo.InvariantCulture, out var db))
            return Model.ReplayGain.Silence;
        return Model.ReplayGain.FromMeasurement(lufs, db);
    }

    /// <summary>
    /// Cuts an image to a centered square, scales it down to the given size and re-encodes it as JPEG. Every
    /// album and artist image goes through here or <see cref="SquareImageAsync"/>, so they are all square.
    /// </summary>
    public Task<byte[]?> ResizeImageAsync(byte[] image, int maxSize, CancellationToken ct) =>
        ConvertImageAsync(image, SquareFilter(maxSize), "jpg", ct);

    /// <summary>
    /// The image as a square (album and artist images always are): cut to the middle of its longer side, then
    /// scaled down to <paramref name="maxSize"/> if given. A square image within the size comes back unchanged,
    /// without encoding it again. Null when it cannot be decoded.
    /// </summary>
    public async Task<byte[]?> SquareImageAsync(byte[] image, int? maxSize, CancellationToken ct, string format = "jpg")
    {
        var size = await ImageSizeAsync(image, ct);
        if (size == null) return null;
        var (width, height) = size.Value;
        if (width == height && (maxSize == null || width <= maxSize)) return image;
        return await ConvertImageAsync(image, SquareFilter(maxSize), format, ct);
    }

    /// <summary>
    /// Makes an image file square in place (same format, by its extension, new timestamp). Returns true when it was changed;
    /// false when it was square already or could not be read. <paramref name="keepOriginal"/> is called with the
    /// path right before it is replaced, e.g. to move the original to the trash.
    /// </summary>
    public async Task<bool> SquareImageFileAsync(string path, Action<string>? keepOriginal, CancellationToken ct)
    {
        var image = await File.ReadAllBytesAsync(path, ct);
        var format = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpg";
        var square = await SquareImageAsync(image, null, ct, format);
        if (square == null || ReferenceEquals(square, image)) return false;

        // Written next to it and moved over it, so a reader never sees half a file. It gets a new timestamp, so
        // every cache keyed on it (Subsonic covers, browser) notices the change.
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tmp, square, ct);
            keepOriginal?.Invoke(path);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            File.Delete(tmp);
        }

        return true;
    }

    /// <summary>Width and height of an image, or null when ffprobe cannot read it.</summary>
    public async Task<(int Width, int Height)?> ImageSizeAsync(byte[] image, CancellationToken ct)
    {
        var input = Path.Combine(ImageWorkDir(), Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllBytesAsync(input, image, ct);
            var r = await ProcessRunner.RunAsync(_o.FfprobePath,
                ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0:s=x", input], ct);
            return r.ExitCode == 0 ? ParseSize(r.StdOut) : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No ffprobe at all (only outside the Docker image): the size is unknown, the image stays as it is.
            return null;
        }
        finally
        {
            File.Delete(input);
        }
    }

    /// <summary>ffprobe's "WIDTHxHEIGHT" (first line).</summary>
    internal static (int Width, int Height)? ParseSize(string output)
    {
        var parts = output.Trim().Split('\n')[0].Trim().Split('x');
        return parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w > 0 && h > 0
            ? (w, h)
            : null;
    }

    /// <summary>
    /// ffmpeg filter: the largest centered square (crop defaults to the middle), then scaled down to at most
    /// <paramref name="maxSize"/>; never scaled up.
    /// </summary>
    internal static string SquareFilter(int? maxSize) =>
        "crop='min(iw,ih)':'min(iw,ih)'" + (maxSize is { } m ? $",scale='min({m},iw)':'min({m},ih)'" : "");

    private string ImageWorkDir()
    {
        var dir = Path.Combine(_o.WorkPath, "img");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// <paramref name="image"/> as a square of <paramref name="size"/> pixels with <paramref name="badgePath"/> (a PNG with
    /// transparency) in the bottom right corner, e.g. to mark a playlist's origin. Null when ffmpeg cannot read it.
    /// </summary>
    public async Task<byte[]?> AddBadgeAsync(byte[] image, string badgePath, int size, CancellationToken ct)
    {
        var input = Path.Combine(ImageWorkDir(), Guid.NewGuid().ToString("N"));
        var output = $"{input}.jpg";
        var badge = size / 5;
        var margin = size / 25;
        try
        {
            await File.WriteAllBytesAsync(input, image, ct);
            var r = await ProcessRunner.RunAsync(_o.FfmpegPath,
            [
                "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", input, "-i", badgePath, "-filter_complex",
                $"[0:v]scale={size}:{size}:force_original_aspect_ratio=increase,crop={size}:{size}[c];[1:v]scale={badge}:{badge}[b];" +
                $"[c][b]overlay=W-w-{margin}:H-h-{margin}", "-frames:v", "1", "-q:v", "3", output,
            ], ct);
            return r.ExitCode == 0 ? await File.ReadAllBytesAsync(output, ct) : null;
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    private async Task<byte[]?> ConvertImageAsync(byte[] image, string filter, string format, CancellationToken ct)
    {
        var input = Path.Combine(ImageWorkDir(), Guid.NewGuid().ToString("N"));
        var output = $"{input}.{format}";
        try
        {
            await File.WriteAllBytesAsync(input, image, ct);
            List<string> args = ["-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", input, "-vf", filter, "-frames:v", "1"];
            if (format == "jpg") args.AddRange(["-q:v", "3"]);
            args.Add(output);
            var r = await ProcessRunner.RunAsync(_o.FfmpegPath, args, ct);
            return r.ExitCode == 0 ? await File.ReadAllBytesAsync(output, ct) : null;
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }
}
