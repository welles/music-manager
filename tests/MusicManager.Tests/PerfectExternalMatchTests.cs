using MusicManager.Core.Data;
using MusicManager.Core.Model;
using MusicManager.Core.Pipeline;

namespace MusicManager.Tests;

/// <summary>Which files the "100-%-Treffer von Deezer/Spotify übernehmen" buttons take over, and with which match.</summary>
public class PerfectExternalMatchTests
{
    private static ExternalMatch Match(string source, double score, string id = "1", bool fromTagLink = false) =>
        new() { Source = source, Id = id, Title = "T", Score = score, FromTagLink = fromTagLink };

    private static TrackFile File(FileStatus status = FileStatus.NotInMusicBrainz, bool weakCandidate = false, params ExternalMatch[] matches) => new()
    {
        RelativePath = "A/1.flac",
        Status = status,
        ExternalMatches = matches.ToList(),
        Candidates = weakCandidate
            ?
            [
                new ReleaseCandidate
                {
                    RecordingId = "r", RecordingTitle = "T", RecordingArtist = "A",
                    Release = new ReleaseAppearance { ReleaseId = "rel", Title = "Album" },
                },
            ]
            : [],
    };

    [Fact]
    public void TakesThePerfectMatchOfTheChosenSource()
    {
        var file = File(matches: [Match("spotify", 1, "s"), Match("deezer", 1, "d")]);
        Assert.Equal("deezer:d", ReviewService.PerfectMatch(file, "deezer")?.Key);
        Assert.Equal("spotify:s", ReviewService.PerfectMatch(file, "spotify")?.Key);
    }

    [Theory]
    [InlineData(0.999)] // shown as 100 %, but not a perfect match
    [InlineData(0.95)]
    public void LowerScoresAreNotTaken(double score) =>
        Assert.Null(ReviewService.PerfectMatch(File(matches: [Match("deezer", score)]), "deezer"));

    [Fact]
    public void AWeakMusicBrainzCandidateKeepsTheFileInTheQueue() =>
        Assert.Null(ReviewService.PerfectMatch(File(weakCandidate: true, matches: [Match("deezer", 1)]), "deezer"));

    [Theory]
    [InlineData(FileStatus.NeedsReview)]
    [InlineData(FileStatus.Duplicate)]
    [InlineData(FileStatus.Error)]
    [InlineData(FileStatus.Done)]
    public void OnlyFilesNotInMusicBrainz(FileStatus status) =>
        Assert.Null(ReviewService.PerfectMatch(File(status, matches: [Match("deezer", 1)]), "deezer"));

    [Fact]
    public void NoMatchFromThatSource() =>
        Assert.Null(ReviewService.PerfectMatch(File(matches: [Match("spotify", 1)]), "deezer"));

    [Fact]
    public void ALinkFromTheTagsWinsOverASearchHit()
    {
        var file = File(matches: [Match("deezer", 1, "search"), Match("deezer", 1, "link", fromTagLink: true)]);
        Assert.Equal("deezer:link", ReviewService.PerfectMatch(file, "deezer")?.Key);
    }
}
