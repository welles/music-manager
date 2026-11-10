using MusicManager.Core.Configuration;
using MusicManager.Core.Model;
using MusicManager.Core.Rules;

namespace MusicManager.Tests;

public class ReleaseScorerTests
{
    private static ReleaseAppearance Rel(string id, string title, string rg, string type = "Album", string? secondary = null,
        string date = "2000-01-01", string country = "XW", string status = "Official", int? track = null) => new()
    {
        ReleaseId = id, Title = title, ReleaseGroupId = rg, PrimaryType = type,
        SecondaryTypes = secondary == null ? [] : [secondary], Date = date, Country = country, Status = status,
        Formats = ["Digital Media"], ArtistString = "Artist", TrackPosition = track,
    };

    private static RecordingMatch Rec(params ReleaseAppearance[] releases) => new()
    {
        RecordingId = "rec", Title = "Song", Credits = [new CreditPart("a", "Artist", "Artist", "")],
        LengthSeconds = 200, MatchScore = 0.95, Releases = [.. releases],
    };

    [Fact]
    public void PrefersStudioAlbumOverCompilationAndLive()
    {
        var recs = new[]
        {
            Rec(Rel("comp", "Greatest Hits", "rg-comp", secondary: "Compilation", date: "1995"),
                Rel("live", "Live in Berlin", "rg-live", secondary: "Live", date: "1992"),
                Rel("album", "The Album", "rg-album", date: "1990")),
        };

        var result = ReleaseScorer.Score(new TagSet { Title = "Song", Artist = "Artist" }, 201, recs, GroupContext.Empty, new AppSettings());

        Assert.Equal("album", result.Best!.Release.ReleaseId);
        Assert.True(result.IsCertain, result.Reason);
    }

    [Fact]
    public void OldAlbumTagWinsAgainstEarlierRelease()
    {
        var recs = new[]
        {
            Rec(Rel("single", "Song", "rg-single", type: "Single", date: "1989"),
                Rel("album", "The Album", "rg-album", date: "1990")),
        };

        var result = ReleaseScorer.Score(new TagSet { Title = "Song", Artist = "Artist", Album = "The Album (Deluxe)" }, 200, recs,
            GroupContext.Empty, new AppSettings());

        Assert.Equal("album", result.Best!.Release.ReleaseId);
    }

    [Fact]
    public void GroupContextBreaksTies()
    {
        var recs = new[] { Rec(Rel("a", "Album A", "rg-a"), Rel("b", "Album B", "rg-b")) };
        var group = new GroupContext([(["b"], ["rg-b"]), (["b"], ["rg-b"])]);

        var result = ReleaseScorer.Score(new TagSet { Title = "Song", Artist = "Artist" }, 200, recs, group, new AppSettings());

        Assert.Equal("b", result.Best!.Release.ReleaseId);
        Assert.True(result.IsCertain, result.Reason);
    }

    [Fact]
    public void CloseCandidatesOfDifferentReleaseGroupsAreUncertain()
    {
        var recs = new[] { Rec(Rel("a", "Album A", "rg-a"), Rel("b", "Album B", "rg-b")) };

        var result = ReleaseScorer.Score(new TagSet { Title = "Song", Artist = "Artist" }, 200, recs, GroupContext.Empty, new AppSettings());

        Assert.False(result.IsCertain);
    }

    [Fact]
    public void ReleasesOfTheSameReleaseGroupDoNotCauseUncertainty()
    {
        var recs = new[] { Rec(Rel("cd", "The Album", "rg", country: "DE"), Rel("digital", "The Album", "rg")) };

        var result = ReleaseScorer.Score(new TagSet { Title = "Song", Artist = "Artist" }, 200, recs, GroupContext.Empty, new AppSettings());

        Assert.True(result.IsCertain, result.Reason);
        Assert.Equal("digital", result.Best!.Release.ReleaseId);
    }
}
