using MusicManager.Core.Data;
using MusicManager.Core.Pipeline;
using MusicManager.Core.Rules;

namespace MusicManager.Tests;

/// <summary>Same audio, only encoded differently – or does it sound different somewhere (e.g. a commentary track)?</summary>
public class SameAudioTests
{
    private static uint[] RandomFingerprint(int seed, int length)
    {
        var rnd = new Random(seed);
        return Enumerable.Range(0, length).Select(_ => (uint)rnd.NextInt64(0, uint.MaxValue)).ToArray();
    }

    [Fact]
    public void TheWeakestSectionIsFoundWhereTheAudioDiffers()
    {
        var a = RandomFingerprint(1, 1600); // ~3:18
        var rnd = new Random(2);
        // A voice from 1:39 for ~20 s: those values become unrelated; the rest is a slightly noisy re-encode.
        var b = a.Select((v, i) => i is >= 800 and < 960 ? (uint)rnd.NextInt64(0, uint.MaxValue)
            : rnd.NextDouble() < 0.2 ? v ^ (1u << rnd.Next(32)) : v).Skip(5).ToArray();

        var c = FingerprintMatcher.Compare(a, b);

        Assert.InRange(c.Overall, 0.9, 0.97);
        Assert.InRange(c.Weakest, 0.4, 0.6);
        Assert.InRange(c.WeakestAt.TotalSeconds, 95, 125);
    }

    [Fact]
    public void AReEncodeIsTheSameAudio()
    {
        var a = RandomFingerprint(3, 1600);
        var rnd = new Random(4);
        var b = a.Select(v => rnd.NextDouble() < 0.3 ? v ^ (1u << rnd.Next(32)) : v).ToArray();

        var (same, text) = SameAudioChecker.Judge(FingerprintMatcher.Compare(a, b), 198.2, 198.9);

        Assert.True(same);
        Assert.StartsWith("Gleiche Aufnahme", text);
    }

    // Values measured with a real song: re-encodes to MP3/Opus vs. a voice mixed over the music.
    [Theory]
    [InlineData(0.991, 0.985, true)] // MP3 128k
    [InlineData(0.986, 0.978, true)] // Opus 96k
    [InlineData(0.955, 0.872, false)] // quiet voice: the average alone would pass
    [InlineData(0.862, 0.621, false)] // louder voice
    [InlineData(0.655, 0.509, false)] // music ducked under the voice
    [InlineData(0.966, 0.960, false)] // evenly a little off: another master, worth a listen
    public void MeasuredReEncodesPassAndCommentaryDoesNot(double overall, double weakest, bool expected) =>
        Assert.Equal(expected, SameAudioChecker.Judge(new AudioComparison(overall, weakest, TimeSpan.FromSeconds(80)), 190, 190).Same);

    [Fact]
    public void AnotherLengthIsAnotherEdit()
    {
        var (same, text) = SameAudioChecker.Judge(new AudioComparison(0.99, 0.98, TimeSpan.Zero), 190, 196);

        Assert.False(same);
        Assert.Contains("Länge", text);
    }

    [Fact]
    public void ACommentaryIsExplainedWithWhereItDiffers()
    {
        var (_, text) = SameAudioChecker.Judge(new AudioComparison(0.955, 0.872, TimeSpan.FromSeconds(83)), 190, 190);

        Assert.Contains("ab 1:23 nur 87", text);
    }

    private static TrackFile File(params DuplicateMatch[] matches) => new()
    {
        RelativePath = "x.flac", Status = FileStatus.Duplicate, Duplicates = [.. matches],
    };

    private static DuplicateMatch Match(DuplicateKind kind, bool? same) => new()
    {
        RelativePath = "a.m4a", Reason = "", Kind = kind, SameAudio = same, AudioCheck = same == null ? null : "",
    };

    [Fact]
    public void OnlyFilesWhoseDuplicatesAllSoundTheSameAreSafeToDiscard()
    {
        Assert.True(SameAudioChecker.IsSafeToDiscard(File(Match(DuplicateKind.Duplicate, true), Match(DuplicateKind.OtherVersion, null))));
        Assert.False(SameAudioChecker.IsSafeToDiscard(File(Match(DuplicateKind.Duplicate, true), Match(DuplicateKind.Duplicate, false))));
        Assert.False(SameAudioChecker.IsSafeToDiscard(File(Match(DuplicateKind.Duplicate, null))));
        Assert.False(SameAudioChecker.IsSafeToDiscard(File(Match(DuplicateKind.OtherVersion, true))));
    }
}
