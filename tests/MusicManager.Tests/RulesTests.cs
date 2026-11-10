using MusicManager.Core.Data;
using MusicManager.Core.Model;
using MusicManager.Core.Rules;

namespace MusicManager.Tests;

public class RulesTests
{
    private static readonly ArtistRuleSet Rules = ArtistRuleSet.FromRules(
    [
        new ArtistRule { Kind = ArtistRuleKind.ForceSplit, Pattern = "Audio88 & Yassin" },
        new ArtistRule { Kind = ArtistRuleKind.ForceSplit, Pattern = "Ben Caplan & The Casual Smokers" },
        new ArtistRule { Kind = ArtistRuleKind.ForceReplace, Pattern = "Bob Seger & the Silver Bullet Band", Replacement = "Bob Seger" },
        new ArtistRule { Kind = ArtistRuleKind.NoSplit, Pattern = "Bruce Hornsby & the Range" },
        new ArtistRule { Kind = ArtistRuleKind.NoSplit, Pattern = "Simon & Garfunkel" },
    ]);

    [Theory]
    [InlineData("A feat. B & C", new[] { "A", "B", "C" })]
    [InlineData("A ft. B, C × D", new[] { "A", "B", "C", "D" })]
    [InlineData("A with B special appearance by C", new[] { "A", "B", "C" })]
    [InlineData("Simon & Garfunkel feat. X", new[] { "Simon & Garfunkel", "X" })]
    [InlineData("Bruce Hornsby & the Range", new[] { "Bruce Hornsby & the Range" })]
    public void Split_UsesSplittersAndExceptions(string input, string[] expected) =>
        Assert.Equal(expected, Rules.Split(input));

    [Fact]
    public void ApplyToList_ForceSplitsAndReplacesWholeNames()
    {
        var result = Rules.ApplyToList(["Audio88 & Yassin", "Bob Seger & the Silver Bullet Band", "Other"]);
        Assert.Equal(["Audio88", "Yassin", "Bob Seger", "Other"], result);
    }

    [Fact]
    public void Apply_RemovesVariantMarkersFromTitle()
    {
        var t = TaggingRules.Apply(new TagSet { Title = "Song (Explicit) [Album Version]", Artist = "A" }, Rules);
        Assert.Equal("Song", t.Title);
    }

    [Fact]
    public void Apply_MovesFeaturesFromTitleToArtist()
    {
        var t = TaggingRules.Apply(new TagSet { Title = "Song (feat. B & C)", Artist = "A" }, Rules);
        Assert.Equal("Song", t.Title);
        Assert.Equal("A feat. B & C", t.Artist);
        Assert.Equal(["A", "B", "C"], t.Artists);
    }

    [Fact]
    public void Apply_ReadsRemixerFromTitleAndAddsArtist()
    {
        var t = TaggingRules.Apply(new TagSet { Title = "Song (Some DJ remix)", Artist = "A" }, Rules);
        Assert.Equal("Some DJ", t.Remixer);
        Assert.Equal(["A", "Some DJ"], t.Artists);
        Assert.Equal("Song (Some DJ remix)", t.Title);
    }

    [Fact]
    public void Apply_AlbumArtistEqualToArtistReusesArtistList()
    {
        var t = TaggingRules.Apply(new TagSet { Title = "x", Artist = "A & B", AlbumArtist = "B & A" }, Rules);
        Assert.Equal(["A", "B"], t.AlbumArtists);
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        var input = new TagSet { Title = "Song (feat. B) (Remixer X Remix) (explicit)", Artist = "Bob Seger & the Silver Bullet Band", AlbumArtist = "Audio88 & Yassin" };
        var once = TaggingRules.Apply(input, Rules);
        var twice = TaggingRules.Apply(once, Rules);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(once), System.Text.Json.JsonSerializer.Serialize(twice));
        Assert.Equal("Bob Seger feat. B", once.Artist);
        Assert.Equal(["Audio88", "Yassin"], once.AlbumArtists);
    }

    [Fact]
    public void Apply_ConvertsTypographicPunctuation()
    {
        var t = TaggingRules.Apply(new TagSet { Title = "Don’t Stop – “Live”…", Artist = "A" }, Rules);
        Assert.Equal("Don't Stop - \"Live\"...", t.Title);
    }

    [Fact]
    public void PathBuilder_FollowsRenamingScript()
    {
        var path = PathBuilder.Build(new TagSet { AlbumArtist = "Sigur Rós", Album = "Ágætis byrjun", Title = "Svefn-g-englar: AC/DC?", Artists = ["x"] });
        Assert.Equal(Path.Combine("Sigur Ros", "Agaetis byrjun", "Sigur Ros - Svefn-g-englar_ AC_DC_.m4a"), path);
    }

    [Fact]
    public void PathBuilder_FallsBackToFirstArtistThenArtistString()
    {
        Assert.StartsWith("B" + Path.DirectorySeparatorChar, PathBuilder.Build(new TagSet { Artists = ["B", "C"], Artist = "B feat. C", Title = "t", Album = "a" }));
        Assert.StartsWith("B feat. C" + Path.DirectorySeparatorChar, PathBuilder.Build(new TagSet { Artist = "B feat. C", Title = "t", Album = "a" }));
    }

    [Fact]
    public void PreCleaner_RemovesVideoNoiseAndParsesFileName()
    {
        var r = PreCleaner.Clean(new TagSet(), "07 - Some Artist - Great Song (Official Video) [HD].mp3", null);
        Assert.Equal("Some Artist", r.Tags.Artist);
        Assert.Equal("Great Song", r.Tags.Title);
        Assert.True(r.LooksMessy);
    }

    [Fact]
    public void PreCleaner_MusicFabProfile()
    {
        var profile = new SourceProfile
        {
            Name = "MusicFab", FolderName = "musicfab", SplitCommaWithoutSpace = true, TrimTrackTotal = true,
            DeleteTags = ["show", "discnumber"],
        };
        var tags = new TagSet { Artist = "A,B", Title = "t", DiscNumber = 1, TrackNumber = 3, TrackCount = 12 };
        tags.Extra["show"] = "Some Show";
        var r = PreCleaner.Clean(tags, "x.mp3", profile);
        Assert.Equal(["A", "B"], r.Tags.Artists);
        Assert.Null(r.Tags.DiscNumber);
        Assert.False(r.Tags.Extra.ContainsKey("show"));
        Assert.Equal(3, r.Tags.TrackNumber);
        Assert.Null(r.Tags.TrackCount);
    }

    [Theory]
    [InlineData("The Beatles", "Beatles, The", 0.5)]
    [InlineData("Abbey Road (Remastered 2009)", "Abbey Road", 0.8)]
    [InlineData("Completely different", "Abbey Road", 0)]
    public void Similarity_IsTolerant(string a, string b, double min) =>
        Assert.True(TextUtil.Similarity(a, b) >= min);
}
