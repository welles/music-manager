using MusicManager.Core.Configuration;
using MusicManager.Core.Data;
using MusicManager.Core.Model;
using MusicManager.Core.Rules;

namespace MusicManager.Tests;

public class TagBuilderTests
{
    private static readonly ArtistRuleSet Rules = ArtistRuleSet.FromRules(
    [
        new ArtistRule { Kind = ArtistRuleKind.ForceReplace, Pattern = "Bob Seger & the Silver Bullet Band", Replacement = "Bob Seger" },
        new ArtistRule { Kind = ArtistRuleKind.ForceSplit, Pattern = "Audio88 & Yassin" },
    ]);

    private static ReleaseDetail Release(List<CreditPart> trackCredits, List<CreditPart>? releaseCredits = null) => new()
    {
        ReleaseId = "rel", Title = "Album", ReleaseGroupId = "rg", PrimaryType = "Album", Status = "Official", Date = "2010-05-01",
        Credits = releaseCredits ?? trackCredits,
        Genres = new(StringComparer.OrdinalIgnoreCase) { ["hip hop"] = 10, ["german hip hop"] = 6, ["seen live"] = 20, ["rap"] = 2 },
        Media =
        [
            new MediumDetail
            {
                Position = 1, Format = "Digital Media",
                Tracks =
                [
                    new TrackDetail { TrackId = "t1", RecordingId = "other", Position = 1, Title = "Intro" },
                    new TrackDetail { TrackId = "t2", RecordingId = "rec", Position = 2, Title = "Song", Credits = trackCredits },
                ],
            },
        ],
    };

    [Fact]
    public void BuildsDisplayStringAndMultiArtistListFromCredits()
    {
        var credits = new List<CreditPart>
        {
            new("a1", "Artist A", "A", " feat. "),
            new("b1", "Artist B", "B", ""),
        };

        var t = TagBuilder.Build(Release(credits, [credits[0] with { JoinPhrase = "" }]), "rec", new Dictionary<string, ArtistInfo>(),
            new AppSettings(), Rules);

        Assert.Equal("Artist A feat. Artist B", t.Artist);
        Assert.Equal(["Artist A", "Artist B"], t.Artists);
        Assert.Equal("Artist A", t.AlbumArtist);
        Assert.Equal(["Artist A"], t.AlbumArtists);
        Assert.Equal(2, t.TrackNumber);
        Assert.Equal(2, t.TrackCount);
        Assert.Equal("t2", t.MbTrackId);
    }

    [Fact]
    public void ReleaseWithoutDateFallsBackToOriginalDate()
    {
        var credits = new List<CreditPart> { new("a1", "Air", "Air", "") };
        var release = Release(credits);
        release = new ReleaseDetail
        {
            ReleaseId = release.ReleaseId, Title = release.Title, ReleaseGroupId = release.ReleaseGroupId, PrimaryType = release.PrimaryType,
            Status = release.Status, Date = null, OriginalDate = "1998-01-16", Credits = release.Credits, Media = release.Media,
        };
        var t = TagBuilder.Build(release, "rec", new Dictionary<string, ArtistInfo>(), new AppSettings(), Rules);
        Assert.Equal("1998-01-16", t.Date);
    }

    [Fact]
    public void TranslatesArtistNamesWithLocaleAlias()
    {
        var credits = new List<CreditPart> { new("x", "Сплин", "Сплин", "") };
        var artists = new Dictionary<string, ArtistInfo>
        {
            ["x"] = new() { ArtistId = "x", Name = "Сплин", LocaleAliases = new(StringComparer.OrdinalIgnoreCase) { ["en"] = "Splean" } },
        };

        var t = TagBuilder.Build(Release(credits), "rec", artists, new AppSettings(), Rules);

        Assert.Equal("Splean", t.Artist);
    }

    [Fact]
    public void AppliesReplacementPerCreditAndForcedSplit()
    {
        var credits = new List<CreditPart>
        {
            new("s", "Bob Seger & the Silver Bullet Band", "Bob Seger & the Silver Bullet Band", " & "),
            new("y", "Audio88 & Yassin", "Audio88 & Yassin", ""),
        };

        var t = TagBuilder.Build(Release(credits), "rec", new Dictionary<string, ArtistInfo>(), new AppSettings(), Rules);

        Assert.Equal("Bob Seger & Audio88 & Yassin", t.Artist);
        Assert.Equal(["Bob Seger", "Audio88", "Yassin"], t.Artists);
    }

    [Fact]
    public void RemixerRelationshipAddsArtist()
    {
        var release = Release([new CreditPart("a", "A", "A", "")]);
        release.Media[0].Tracks[1].Remixers.Add(new CreditPart("r", "DJ R", "DJ R", ""));

        var t = TagBuilder.Build(release, "rec", new Dictionary<string, ArtistInfo>(), new AppSettings(), Rules);

        Assert.Equal("DJ R", t.Remixer);
        Assert.Equal(["A", "DJ R"], t.Artists);
    }

    [Fact]
    public void GenresAreFilteredByUsageAndBlacklist()
    {
        var t = TagBuilder.Build(Release([new CreditPart("a", "A", "A", "")]), "rec", new Dictionary<string, ArtistInfo>(), new AppSettings(), Rules);

        Assert.Equal(["Hip Hop", "German Hip Hop"], t.Genres);
    }
}
