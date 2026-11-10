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
    /// Converts any input to M4A/AAC. AAC streams are only remuxed (no re-encoding);
    /// everything else is encoded with the native AAC encoder. The moov atom is moved to
    /// the front for streaming. Metadata and pictures are dropped, tags are written afterwards.
    /// </summary>
    public async Task<bool> ConvertToM4aAsync(string input, string output, string? codec, int bitrateKbps, CancellationToken ct)
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
        return copy;
    }

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
        var dir = Path.Combine(_o.WorkPath, "preview");
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

    /// <summary>Scales an image down to the given size and re-encodes it as JPEG.</summary>
    public async Task<byte[]?> ResizeImageAsync(byte[] image, int maxSize, CancellationToken ct)
    {
        var dir = Path.Combine(_o.WorkPath, "img");
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, Guid.NewGuid().ToString("N"));
        var output = input + ".jpg";
        try
        {
            await File.WriteAllBytesAsync(input, image, ct);
            var r = await ProcessRunner.RunAsync(_o.FfmpegPath,
            [
                "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", input,
                "-vf", $"scale='min({maxSize},iw)':'min({maxSize},ih)':force_original_aspect_ratio=decrease",
                "-frames:v", "1", "-q:v", "3", output,
            ], ct);
            return r.ExitCode == 0 ? await File.ReadAllBytesAsync(output, ct) : null;
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }
}
