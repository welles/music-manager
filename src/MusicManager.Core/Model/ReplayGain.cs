using System.Globalization;

namespace MusicManager.Core.Model;

/// <summary>
/// Loudness of a song as ReplayGain 2.0 values: the audio stays as it is, the player turns the volume by
/// <see cref="TrackGain"/> dB so every song plays at <see cref="TargetLufs"/>. Written as the tags
/// <c>replaygain_track_gain</c> / <c>replaygain_track_peak</c>; a file that has them counts as normalized.
/// </summary>
/// <param name="TrackGain">Gain in dB to reach the target loudness (negative = the song is louder).</param>
/// <param name="TrackPeak">Highest sample peak as a linear value (1.0 = full scale); for players that prevent clipping.</param>
public sealed record ReplayGain(double TrackGain, double TrackPeak)
{
    /// <summary>Reference loudness of ReplayGain 2.0.</summary>
    public const double TargetLufs = -18;

    // Lower case is the usual spelling in MP4 files (foobar2000, rsgain); readers compare without case.
    public const string GainTag = "replaygain_track_gain";
    public const string PeakTag = "replaygain_track_peak";

    public static readonly string[] TagNames = [GainTag, PeakTag];

    /// <summary>From a measurement: integrated loudness in LUFS and true peak in dBFS.</summary>
    public static ReplayGain FromMeasurement(double integratedLufs, double truePeakDb) =>
        new(Math.Round(TargetLufs - integratedLufs, 2), Math.Round(Math.Pow(10, truePeakDb / 20), 6));

    public string GainText => string.Create(CultureInfo.InvariantCulture, $"{TrackGain:0.00} dB");
    public string PeakText => TrackPeak.ToString("0.000000", CultureInfo.InvariantCulture);

    /// <summary>Tag values, e.g. "-6.20 dB" and "0.988553"; null when the gain is missing or unreadable.</summary>
    public static ReplayGain? Parse(string? gain, string? peak)
    {
        if (string.IsNullOrWhiteSpace(gain)) return null;
        var number = gain.Trim();
        if (number.EndsWith("dB", StringComparison.OrdinalIgnoreCase)) number = number[..^2].Trim();
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var g)) return null;
        // Without a readable peak, full scale is the careful assumption.
        var p = double.TryParse(peak?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : 1.0;
        return new ReplayGain(g, p);
    }
}
