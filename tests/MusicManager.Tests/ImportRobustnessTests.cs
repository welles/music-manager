using MusicManager.Core.Rules;
using MusicManager.Core.Services;

namespace MusicManager.Tests;

/// <summary>Two failures seen while importing a large old collection.</summary>
public class ImportRobustnessTests
{
    // A MusicBrainz release title with an emoji made ToAscii normalize half a surrogate pair and fail the file.
    [Theory]
    [InlineData("Black Parade 🖤", "Black Parade _")]
    [InlineData("𝔅lack Parade", "Black Parade")] // compatibility form of a Fraktur letter
    [InlineData("Café 🎸 Ünïcode", "Cafe _ Unicode")]
    public void ToAsciiHandlesCharactersOutsideTheBmp(string input, string expected) =>
        Assert.Equal(expected, TextUtil.ToAscii(input));

    // Built in code: xUnit cannot pass a lone surrogate through InlineData unchanged.
    [Fact]
    public void ToAsciiHandlesBrokenUtf16()
    {
        Assert.Equal("A_B", TextUtil.ToAscii("A" + '\uD83D' + "B")); // high surrogate without its partner
        Assert.Equal("A_", TextUtil.ToAscii("A" + '\uDC00'));
    }

    [Fact]
    public void NormalizeComparesTitlesWithEmojis() =>
        Assert.Equal("welcome to the black parade", TextUtil.Normalize("Welcome to the Black Parade 🖤"));

    // A damaged FLAC: ffmpeg stopped at the damaged spot, exited with 0, and the song was filed cut short.
    [Theory]
    [InlineData(345.6, 27.3, true)]
    [InlineData(278.9, 131.2, true)]
    [InlineData(240.0, 239.95, false)] // AAC padding/rounding
    [InlineData(600.0, 595.0, false)] // within 1 %
    [InlineData(600.0, 590.0, true)]
    [InlineData(0.5, 0.1, false)] // within 1 s
    [InlineData(null, 10.0, false)] // source length unknown
    [InlineData(200.0, 205.0, false)] // longer is not truncated
    public void TruncatedConversionIsDetected(double? source, double? converted, bool truncated) =>
        Assert.Equal(truncated, AudioTools.IsTruncated(source, converted));
}
