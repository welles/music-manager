using MusicManager.Core.Data;
using MusicManager.Core.Model;
using MusicManager.Core.Pipeline;

namespace MusicManager.Tests;

public class DuplicateTests
{
    [Fact]
    public void SameRecordingWithoutIdsIsDuplicate()
    {
        var (kind, _) = DuplicateDetector.Classify(new TagSet { Title = "Song" }, new LibraryEntry { RelativePath = "x", Title = "Song" });
        Assert.Equal(DuplicateKind.Duplicate, kind);
    }

    [Fact]
    public void DifferentMusicBrainzRecordingsAreOtherVersions()
    {
        var (kind, _) = DuplicateDetector.Classify(new TagSet { Title = "Song", MbRecordingId = "a" },
            new LibraryEntry { RelativePath = "x", Title = "Song", MbRecordingId = "b" });
        Assert.Equal(DuplicateKind.OtherVersion, kind);
    }

    [Theory]
    [InlineData("Song (DJ X Remix)", "Song")]
    [InlineData("Song - Live at Wembley", "Song")]
    [InlineData("Song (2011 Remastered)", "Song")]
    [InlineData("Song (Radio Edit)", "Song (Extended Mix)")]
    public void VersionMarkersMakeOtherVersions(string a, string b)
    {
        var (kind, _) = DuplicateDetector.Classify(new TagSet { Title = a }, new LibraryEntry { RelativePath = "x", Title = b });
        Assert.Equal(DuplicateKind.OtherVersion, kind);
    }

    [Fact]
    public void RemasterSpellingsAreTheSameMarker() =>
        Assert.Equal(DuplicateDetector.VersionMarkers("Song (Remastered 2009)"), DuplicateDetector.VersionMarkers("Song - 2009 Remaster"));
}

public class DuplicateFingerprintTests
{
    [Fact]
    public void MatchingFingerprintMakesDifferentRecordingsADuplicate()
    {
        var (kind, _) = DuplicateDetector.Classify(new TagSet { Title = "Shutdown", MbRecordingId = "a" },
            new LibraryEntry { RelativePath = "x", Title = "Shutdown", MbRecordingId = "b" }, fingerprintMatches: true);
        Assert.Equal(DuplicateKind.Duplicate, kind);
    }

    [Fact]
    public void VersionMarkersWinOverMatchingFingerprint()
    {
        var (kind, _) = DuplicateDetector.Classify(new TagSet { Title = "Song (2011 Remaster)" },
            new LibraryEntry { RelativePath = "x", Title = "Song" }, fingerprintMatches: true);
        Assert.Equal(DuplicateKind.OtherVersion, kind);
    }
}
