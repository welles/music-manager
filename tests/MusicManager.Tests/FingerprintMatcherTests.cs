using MusicManager.Core.Rules;

namespace MusicManager.Tests;

public class FingerprintMatcherTests
{
    private static uint[] RandomFingerprint(int seed, int length = 900)
    {
        var rnd = new Random(seed);
        return Enumerable.Range(0, length).Select(_ => (uint)rnd.NextInt64(0, uint.MaxValue)).ToArray();
    }

    [Fact]
    public void IdenticalFingerprintsMatchFully()
    {
        var fp = RandomFingerprint(1);
        Assert.Equal(1.0, FingerprintMatcher.Similarity(fp, fp));
    }

    [Fact]
    public void ShiftedFingerprintIsFoundByAlignment()
    {
        var fp = RandomFingerprint(2);
        var shifted = fp.Skip(17).ToArray(); // ~2 s later start, e.g. a different intro cut
        Assert.Equal(1.0, FingerprintMatcher.Similarity(fp, shifted));
    }

    [Fact]
    public void ReEncodingNoiseStaysAboveThreshold()
    {
        var fp = RandomFingerprint(3);
        var rnd = new Random(4);
        var noisy = fp.Select(v => rnd.NextDouble() < 0.3 ? v ^ (1u << rnd.Next(32)) ^ (1u << rnd.Next(32)) : v).ToArray();
        Assert.True(FingerprintMatcher.Similarity(fp, noisy) > 0.95);
    }

    [Fact]
    public void DifferentSongsAreDissimilar() =>
        Assert.True(FingerprintMatcher.Similarity(RandomFingerprint(5), RandomFingerprint(6)) < 0.6);

    [Fact]
    public void EncodeDecodeRoundTrip()
    {
        var fp = RandomFingerprint(7, 50);
        Assert.Equal(fp, FingerprintMatcher.Decode(FingerprintMatcher.Encode(fp)));
    }
}
