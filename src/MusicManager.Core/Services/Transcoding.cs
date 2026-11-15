using System.Globalization;

namespace MusicManager.Core.Services;

/// <summary>An output format for streaming: encoder, container and what the client is told.</summary>
public sealed record TranscodeFormat(string Name, string Encoder, string Muxer, string ContentType, string Suffix, int DefaultKbps);

/// <summary>What a stream is turned into: format and bitrate in kbit/s.</summary>
public sealed record TranscodePlan(TranscodeFormat Format, int Kbps);

/// <summary>
/// Transcoding for the Subsonic <c>stream</c> endpoint. The library files stay as they are; a client that asks for a
/// lower bitrate (<c>maxBitRate</c>, e.g. Symfonium on mobile data) or another format gets the audio encoded on the fly,
/// which over a slow uplink starts and skips much faster than the original file.
/// </summary>
public static class Transcoding
{
    public static readonly TranscodeFormat Mp3 = new("mp3", "libmp3lame", "mp3", "audio/mpeg", "mp3", 192);
    public static readonly TranscodeFormat Opus = new("opus", "libopus", "ogg", "audio/ogg", "opus", 128);

    // ADTS instead of M4A: an MP4 container cannot be written to a pipe from the start.
    public static readonly TranscodeFormat Aac = new("aac", "aac", "adts", "audio/aac", "aac", 192);

    public const int MinKbps = 32;
    public const int MaxKbps = 320;

    /// <summary>
    /// Whether and how to transcode. Null means the original file is served: the client asked for "raw", or the file
    /// already fits the requested format and bitrate. Without a format the Subsonic default MP3 is used. The bitrate never
    /// goes above the file's own (encoding again at a higher rate only makes the stream bigger).
    /// </summary>
    public static TranscodePlan? Decide(string sourceSuffix, int? sourceKbps, string? format, int? maxBitRate)
    {
        var name = format?.Trim().ToLowerInvariant();
        if (name == "raw") return null;
        // An unknown format is ignored rather than refused: the client still gets audio.
        var requested = name switch
        {
            "mp3" => Mp3,
            "opus" or "ogg" => Opus,
            "aac" or "m4a" => Aac,
            _ => null,
        };

        int? limit = maxBitRate is > 0 ? maxBitRate : null;
        // An unknown bitrate is treated as too high: the limit is all we know.
        var tooHigh = limit != null && (sourceKbps ?? int.MaxValue) > limit;
        var otherFormat = requested != null && !IsSame(requested, sourceSuffix);
        if (!tooHigh && !otherFormat) return null;

        var target = requested ?? Mp3;
        var kbps = Math.Clamp(limit ?? target.DefaultKbps, MinKbps, MaxKbps);
        if (sourceKbps is { } source) kbps = Math.Min(kbps, Math.Max(source, MinKbps));
        return new TranscodePlan(target, kbps);
    }

    private static bool IsSame(TranscodeFormat format, string suffix)
    {
        suffix = suffix.TrimStart('.').ToLowerInvariant();
        return format == Mp3 ? suffix == "mp3"
            : format == Opus ? suffix == "opus"
            : suffix is "m4a" or "aac" or "mp4";
    }

    /// <summary>ffmpeg arguments that write the stream to stdout, starting <paramref name="offsetSeconds"/> into the song.</summary>
    public static IReadOnlyList<string> Arguments(string input, TranscodePlan plan, int offsetSeconds)
    {
        var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error" };
        // Before -i: a fast seek in the input, so a jump into the song starts right away.
        if (offsetSeconds > 0) args.AddRange(["-ss", offsetSeconds.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", input, "-map", "0:a:0", "-vn", "-map_metadata", "-1", "-c:a", plan.Format.Encoder,
            "-b:a", $"{plan.Kbps}k", "-f", plan.Format.Muxer, "pipe:1"]);
        return args;
    }

    /// <summary>
    /// Expected size of the stream for <c>estimateContentLength</c>: bitrate × remaining length, plus a little for
    /// container overhead (the stream is padded or cut to exactly this size).
    /// </summary>
    public static long EstimateBytes(TranscodePlan plan, double durationSeconds, int offsetSeconds)
    {
        var seconds = Math.Max(1, durationSeconds - offsetSeconds);
        return (long)Math.Ceiling(seconds * plan.Kbps * 125 * 1.02) + 4096;
    }
}
