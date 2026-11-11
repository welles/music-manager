using MusicManager.Core.Data;
using MusicManager.Core.Model;
using MusicManager.Core.Pipeline;
using MusicManager.Core.Rules;

namespace MusicManager.Tests;

/// <summary>Uncertain MusicBrainz candidates also get the Spotify/Deezer search.</summary>
public class UncertainMatchTests
{
    private static ScoreResult Result(bool certain) => new() { Candidates = [], IsCertain = certain, Reason = "" };

    private static TrackFile File(int externalMatches = 0) => new()
    {
        RelativePath = "A/1.flac",
        ExternalMatches = Enumerable.Range(0, externalMatches)
            .Select(i => new ExternalMatch { Source = "spotify", Id = $"{i}", Title = "T" }).ToList(),
    };

    private static RecordingMatch Rec(string id) => new() { RecordingId = id, Title = id };

    [Fact]
    public void UncertainFileIsSearchedExternally() =>
        Assert.True(MatchingPipeline.ShouldSearchExternally(assignedByTags: false, Result(certain: false), File()));

    [Fact]
    public void CertainFileIsNotSearched() =>
        Assert.False(MatchingPipeline.ShouldSearchExternally(assignedByTags: false, Result(certain: true), File()));

    [Fact]
    public void FileAssignedByItsTagsIsNotSearched() =>
        Assert.False(MatchingPipeline.ShouldSearchExternally(assignedByTags: true, Result(certain: false), File()));

    [Fact]
    public void FileWithMatchesFromItsLinksIsNotSearchedAgain() =>
        Assert.False(MatchingPipeline.ShouldSearchExternally(assignedByTags: false, Result(certain: false), File(externalMatches: 1)));

    [Fact]
    public void OnlyNewRecordingsAreAddedOnce()
    {
        var known = new List<RecordingMatch> { Rec("crush-remix") };
        var found = new List<RecordingMatch> { Rec("CRUSH-REMIX"), Rec("ligma"), Rec("ligma") };

        var added = MatchingPipeline.NewRecordings(found, known);

        Assert.Equal(["ligma"], added.Select(r => r.RecordingId));
    }
}
