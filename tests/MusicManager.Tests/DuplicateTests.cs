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

public class TagFieldTests
{
    [Fact]
    public void ArtistIsWrittenAsMultiValueWithoutArtistsTag()
    {
        var fields = MusicManager.Core.Services.TagFields.ForFinalTags(new TagSet
        {
            Title = "Baddadan", Artist = "Chase & Status and Bou feat. IRAH", Artists = ["Chase & Status", "Bou", "IRAH"],
            AlbumArtist = "Chase & Status and Bou", AlbumArtists = ["Chase & Status", "Bou"],
        }, null, "musicbrainz");

        Assert.Equal(["Chase & Status", "Bou", "IRAH"], fields.Single(f => f.Name == "ARTIST").Values);
        Assert.Equal(["Chase & Status", "Bou"], fields.Single(f => f.Name == "ALBUMARTIST").Values);
        Assert.DoesNotContain(fields, f => f.Name is "ARTISTS" or "ALBUMARTISTS");
    }
}

public class NoRelationshipTagsTests
{
    /// <summary>No relationship or instrument credits are ever written (explicit user wish).</summary>
    [Fact]
    public void FinalTagsNeverContainRelationshipOrPerformerFields()
    {
        var t = new TagSet { Title = "x", Artist = "A", Artists = ["A"], Album = "B", Date = "2020", Genres = ["Rock"], Label = "L" };
        t.Extra["PERFORMER"] = "Someone (guitar)";
        t.Extra["COMPOSER"] = "Someone";
        var names = MusicManager.Core.Services.TagFields.ForFinalTags(t, "Front-Cover", "musicbrainz", "1-abc").Select(f => f.Name).ToList();
        foreach (var forbidden in new[] { "PERFORMER", "COMPOSER", "LYRICIST", "WRITER", "PRODUCER", "MIXER", "ENGINEER", "ARRANGER", "WORK" })
            Assert.DoesNotContain(forbidden, names);
        Assert.Contains("MUSICMANAGER_RULES", names);
    }
}
