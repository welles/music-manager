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
        var path = PathBuilder.Build(new TagSet { AlbumArtist = "Sigur Rós", Album = "Ágætis byrjun", Title = "Svefn-g-englar: AC/DC?", Artists = ["Sigur Rós"] });
        Assert.Equal(Path.Combine("Sigur Rós", "Ágætis byrjun", "Sigur Rós - Svefn-g-englar: AC∕DC?.m4a"), path);
    }

    [Theory]
    [InlineData("Fred again..", "leavemealone", "Fred again..", "Fred again.. - leavemealone.m4a")]
    [InlineData("Bruce Springsteen", "Born in the U.S.A.", "Bruce Springsteen", "Bruce Springsteen - Born in the U.S.A..m4a")]
    [InlineData("J.B.O.", "Song", "J.B.O.", "J.B.O. - Song.m4a")]
    public void NamesKeepTrailingDots(string artist, string title, string folder, string file) =>
        Assert.Equal(Path.Combine(folder, "Album", file), PathBuilder.Build(new TagSet { AlbumArtist = artist, Artists = [artist], Album = "Album", Title = title }));

    [Fact]
    public void FileNameUsesTheMainTrackArtist()
    {
        var path = PathBuilder.Build(new TagSet
        {
            AlbumArtist = "Various Artists", Album = "Shell Shock", Title = "Sandblast", Artist = "Fox Stevenson feat. X", Artists = ["Fox Stevenson", "X"],
        });
        Assert.Equal(Path.Combine("Various Artists", "Shell Shock", "Fox Stevenson - Sandblast.m4a"), path);
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
    [InlineData("Black Music Month Playlist", "Liked Songs", null)]
    [InlineData("Liked Songs", "Liked Songs", null)]
    [InlineData("Road Trip", "Road Trip", null)]
    [InlineData("Black Reign", "Liked Songs", "Black Reign")]
    [InlineData("Eyes Cut Deeper (Remixes)", "Liked Songs", "Eyes Cut Deeper (Remixes)")]
    public void PreCleaner_MusicFabDropsPlaylistAlbums(string album, string show, string? expected)
    {
        var profile = new SourceProfile { Name = "MusicFab", FolderName = "musicfab", DropPlaylistAlbum = true, DeleteTags = ["show"] };
        var tags = new TagSet { Artist = "A", Title = "t", Album = album };
        tags.Extra["show"] = show;
        Assert.Equal(expected, PreCleaner.Clean(tags, "x.m4a", profile).Tags.Album);
    }

    [Theory]
    [InlineData("The Beatles", "Beatles, The", 0.5)]
    [InlineData("Abbey Road (Remastered 2009)", "Abbey Road", 0.8)]
    [InlineData("Completely different", "Abbey Road", 0)]
    public void Similarity_IsTolerant(string a, string b, double min) =>
        Assert.True(TextUtil.Similarity(a, b) >= min);
}

public class MatchingFixTests
{
    private static readonly ArtistRuleSet Rules = new();

    private static TagSet Mb(string artist) => new() { Title = "x", Artist = artist, Artists = [artist] };

    [Fact]
    public void FeaturesFromSourceAreAddedWhenMusicBrainzHasOnlyTheMainArtist()
    {
        var t = TaggingRules.AddSourceArtists(Mb("Chase & Status"),
            new TagSet { Title = "Shutdown (feat. IRAH & Col3trane)", Artist = "Chase & Status, IRAH, Col3trane", Artists = ["Chase & Status", "IRAH", "Col3trane"] }, Rules);
        Assert.Equal("Chase & Status feat. IRAH & Col3trane", t.Artist);
        Assert.Equal(["Chase & Status", "IRAH", "Col3trane"], t.Artists);
    }

    [Fact]
    public void OtherSourceArtistsAreAddedAsCollaborators()
    {
        var t = TaggingRules.AddSourceArtists(Mb("Me&You"),
            new TagSet { Title = "The Hoop Loop", Artists = ["Me&You", "TM Juke", "Robert Luis"] }, Rules);
        Assert.Equal("Me&You, TM Juke & Robert Luis", t.Artist);
        Assert.Equal(["Me&You", "TM Juke", "Robert Luis"], t.Artists);
    }

    [Fact]
    public void MusicBrainzCreditsWinWhenTheyHaveAdditionalArtists()
    {
        var mb = new TagSet { Artist = "Czarface & Frankie Pulitzer feat. Method Man", Artists = ["Czarface", "Frankie Pulitzer", "Method Man"] };
        var t = TaggingRules.AddSourceArtists(mb, new TagSet { Artists = ["CZARFACE", "Frankie Pulitzer", "Someone Else"] }, Rules);
        Assert.Same(mb, t);
    }

    [Fact]
    public void MainArtistWithSeparatorIsNotSplitIntoCollaborators()
    {
        var mb = Mb("JJ Grey & Mofro");
        Assert.Same(mb, TaggingRules.AddSourceArtists(mb, new TagSet { Title = "Dirtfloorcraker", Artist = "JJ Grey & Mofro" }, Rules));

        var t = TaggingRules.AddSourceArtists(Mb("JJ Grey & Mofro"),
            new TagSet { Title = "Song (feat. Someone)", Artist = "JJ Grey & Mofro, Guest" }, Rules);
        Assert.Equal(["JJ Grey & Mofro", "Guest", "Someone"], t.Artists);
    }

    [Fact]
    public void SourceArtistsAreIgnoredWhenTheMainArtistDiffers()
    {
        var mb = Mb("Musica Nuda");
        var t = TaggingRules.AddSourceArtists(mb, new TagSet { Artists = ["Petra Magoni", "Ferruccio Spinetti"] }, Rules);
        Assert.Same(mb, t);
    }

    [Fact]
    public void FolderUsesOnlyTheMainAlbumArtist()
    {
        var rules = ArtistRuleSet.FromRules([new ArtistRule { Kind = ArtistRuleKind.ForceSplit, Pattern = "Audio88 & Yassin" }]);
        var t = TaggingRules.Apply(new TagSet { Title = "t", Album = "a", Artist = "Audio88 & Yassin feat. X", AlbumArtist = "Audio88 & Yassin" }, rules);
        Assert.Equal("Audio88 & Yassin", t.FolderArtist);
        Assert.StartsWith("Audio88 & Yassin" + Path.DirectorySeparatorChar, PathBuilder.Build(t));

        var many = TaggingRules.Apply(new TagSet { Title = "War Starter", Album = "Wrong Crowd EP", AlbumArtist = "Bou & Friction & MC Eksman" }, Rules);
        Assert.Equal(Path.Combine("Bou", "Wrong Crowd EP", "Bou - War Starter.m4a"), PathBuilder.Build(many));
    }

    [Theory]
    [InlineData(",", ", ")]
    [InlineData("&", " & ")]
    [InlineData(" feat. ", " feat. ")]
    [InlineData("", "")]
    public void JoinPhrasesAreNormalized(string input, string expected) =>
        Assert.Equal(expected, CreditFormatter.NormalizeJoinPhrase(input));

    [Fact]
    public void SingleAlbumTagDoesNotBlockAlbumCandidate()
    {
        var rec = new RecordingMatch
        {
            RecordingId = "r", Title = "No Way!", Credits = [new CreditPart("a", "Kofi Stone", "Kofi Stone", "")], LengthSeconds = 180,
            MatchScore = 0.95,
            Releases = [new ReleaseAppearance { ReleaseId = "x", Title = "The Last Flower to Bloom", ReleaseGroupId = "rg", PrimaryType = "Album", Status = "Official" }],
        };
        var result = ReleaseScorer.Score(new TagSet { Title = "No Way!", Artist = "Kofi Stone", Album = "No Way!" }, 180, [rec],
            GroupContext.Empty, new MusicManager.Core.Configuration.AppSettings());
        Assert.True(result.IsCertain, result.Reason);
    }
}

public class ForeignTitleTests
{
    [Theory]
    [InlineData("リトルワールド", true)]
    [InlineData("Little World (リトルワールド)", false)]
    [InlineData("Сплин", true)]
    [InlineData("Ágætis byrjun", false)]
    [InlineData("Song (Remix)", false)]
    [InlineData("リトルワールド (Remix)", true)]
    public void DetectsTitlesThatNeedTranslation(string title, bool expected) =>
        Assert.Equal(expected, ForeignTitles.NeedsTranslation(title));

    [Fact]
    public void CombinesTranslationAndOriginal() =>
        Assert.Equal("Little World (リトルワールド)", ForeignTitles.Combine("リトルワールド", "Little World"));

    [Fact]
    public void PathKeepsNonLatinNames() =>
        Assert.Equal(Path.Combine("PompadollS", "Little World (リトルワールド)", "PompadollS - 入曽.m4a"),
            PathBuilder.Build(new TagSet { AlbumArtist = "PompadollS", Album = "Little World (リトルワールド)", Title = "入曽" }));

    [Fact]
    public void LeadingDotWouldHideTheFolder() =>
        Assert.Equal("․..And You Will Know Us", PathBuilder.SanitizeComponent("...And You Will Know Us"));

    [Fact]
    public void LongNamesFitTheByteLimit()
    {
        var s = PathBuilder.SanitizeComponent(new string('入', 150));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(s) <= 240);
        Assert.Equal(80, s.Length);
    }
}

public class ExternalLinkTests
{
    [Fact]
    public void FindsSpotifyAndDeezerLinksInAnyField()
    {
        var links = MusicManager.Core.Services.ExternalLinks.Find(
        [
            new("COMMENT", ["https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQC?si=abc"]),
            new("URL", ["spotify:track:4uLU6hMCjMI75M1A2tKUQC", "https://www.deezer.com/de/track/4265202982"]),
        ]);
        Assert.Equal([("spotify", "4uLU6hMCjMI75M1A2tKUQC"), ("deezer", "4265202982")], links.Select(l => (l.Source, l.Id)));
    }

    [Fact]
    public void RatesExternalMatchesByTitleArtistAndDuration()
    {
        var m = new ExternalMatch { Source = "deezer", Id = "1", Title = "Shutdown (feat. IRAH & Col3trane)", Artists = ["Chase & Status"], DurationSeconds = 170 };
        Assert.True(MusicManager.Core.Services.ExternalSourceService.Rate(m, "Shutdown", "Chase & Status", 171) > 0.95);
        Assert.True(MusicManager.Core.Services.ExternalSourceService.Rate(m, "Other Song", "Someone", 300) < 0.4);
    }
}
