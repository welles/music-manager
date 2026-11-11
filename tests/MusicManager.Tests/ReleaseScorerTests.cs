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
    public void OriginalReleaseFromTheTagsBeatsLaterWorldwideDeluxe()
    {
        // D Smoke – Gaspar Yanga: the tags name the 2020 US original, MusicBrainz also has a 2021 XW deluxe.
        var recs = new[]
        {
            Rec(Rel("deluxe", "Black Habits (Deluxe)", "rg", date: "2021-02-05", country: "XW", track: 4),
                Rel("original", "Black Habits", "rg", date: "2020-02-07", country: "US", track: 4)),
        };

        var result = ReleaseScorer.Score(new TagSet { Title = "Song", Artist = "Artist", Album = "Black Habits", Date = "2020-02-07", TrackNumber = 4 },
            200, recs, GroupContext.Empty, new AppSettings());

        Assert.Equal("original", result.Best!.Release.ReleaseId);
    }

    [Fact]
    public void CompletelyDifferentArtistIsNotFiledAutomatically()
    {
        // Nobodys Face – Chemicals: the fingerprint hit Ollie Crowe – Chemicals.
        var recs = new[] { Rec(Rel("album", "Song", "rg", type: "Single", date: "2022-02-04")) };
        var result = ReleaseScorer.Score(new TagSet { Title = "Song", Artist = "Nobodys Face", Album = "Song" }, 205, recs,
            GroupContext.Empty, new AppSettings());
        Assert.False(result.IsCertain);
        Assert.Contains("Künstler-Tag", result.Reason);
    }

    [Fact]
    public void DifferentArtistGoesToReviewEvenWithAnExactFingerprintMatch()
    {
        // Spotify credits the members "Petra Magoni, Ferruccio Spinetti", MusicBrainz the duo "Musica Nuda" – but an
        // exact fingerprint can also be linked to the wrong recording, so this is not filed automatically.
        var recs = new[] { Rec(Rel("album", "Song", "rg", type: "Single", date: "2022-02-04")) };
        var result = ReleaseScorer.Score(new TagSet { Title = "Song", Artist = "Petra Magoni, Ferruccio Spinetti", Album = "Song" }, 201, recs,
            GroupContext.Empty, new AppSettings());
        Assert.False(result.IsCertain);
    }

    [Fact]
    public void BestCandidateWithWrongLengthIsNotFiledWhenAnotherRecordingFits()
    {
        // "The Pokémon Beatles – Beatles for Sale" won by the album name, but only the Beatles recording has the right length.
        var cover = new RecordingMatch
        {
            RecordingId = "cover", Title = "Song", Credits = [new CreditPart("c", "The Cover Artist", "The Cover Artist", "")],
            LengthSeconds = 208, MatchScore = 0.99, Releases = [Rel("cover-album", "The Album", "rg-cover", date: "2025-02-26")],
        };
        var recs = new[] { cover, Rec(Rel("comp", "Greatest Hits", "rg-comp", secondary: "Compilation", date: "1993")) };
        var result = ReleaseScorer.Score(new TagSet { Title = "Song", Artist = "The Artist", Album = "The Album" }, 200.5, recs,
            GroupContext.Empty, new AppSettings());
        Assert.Equal("cover-album", result.Best!.Release.ReleaseId);
        Assert.False(result.IsCertain);
        Assert.Contains("Länge", result.Reason);
    }

    [Theory]
    [InlineData("2020-02-07", "2020-02-07", 1.0)]
    [InlineData("2020", "2020-02-07", 1.0)]
    [InlineData("2020-02-07", "2020-08-01", 0.5)]
    [InlineData("2020-02-07", "2020", 0.5)]
    [InlineData("2020-02-07", "2021-02-07", 0.0)]
    public void DateTagMatch(string tag, string release, double expected) =>
        Assert.Equal(expected, ReleaseScorer.DateMatch(tag, release));

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
    public void AlbumTagNotMatchingBestCandidateIsUncertain()
    {
        var recs = new[] { Rec(Rel("comp", "Vintage FG", "rg-comp", secondary: "Compilation")) };

        var result = ReleaseScorer.Score(new TagSet { Title = "Song", Artist = "Artist", Album = "Homework" }, 200, recs,
            GroupContext.Empty, new AppSettings());

        Assert.False(result.IsCertain);
        Assert.Contains("Homework", result.Reason);
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
