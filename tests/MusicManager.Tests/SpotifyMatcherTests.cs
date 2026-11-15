using MusicManager.Core.Catalog;
using MusicManager.Core.Data;

namespace MusicManager.Tests;

/// <summary>Which library song a Spotify playlist track is (issues #19/#21).</summary>
public class SpotifyMatcherTests
{
    private static LibraryEntry Entry(string id, string title, string artist, double seconds, string? isrc = null, params string[] more) => new()
    {
        RelativePath = $"{artist}/{title}/{id}.m4a", SongId = id, Title = title, Artist = string.Join(" & ", [artist, .. more]),
        Artists = [artist, .. more], AlbumArtist = artist, AlbumArtists = [artist], Album = title, DurationSeconds = seconds, Isrc = isrc,
        FileSize = 1,
    };

    private static SpotifyMatcher Matcher(params LibraryEntry[] entries) => SpotifyMatcher.For(CatalogSnapshot.Build(entries, 1));

    [Fact]
    public void TheIsrcWinsOverSpelling()
    {
        var m = Matcher(Entry("tr-1", "Haus am See", "Peter Fox", 220, "DEA810800130"));

        Assert.Equal("tr-1", m.Match("Haus Am See - Remastered 2019", ["Peter Fox"], 221_000, "dea810800130")?.Id);
    }

    [Fact]
    public void TitleAndArtistMatchDespiteFeaturingRemasterAccentsAndCase()
    {
        var m = Matcher(
            Entry("tr-1", "Judgment Day", "The Thunderclaps", 222, null, "Orifice Vulgatron"),
            Entry("tr-2", "Café del Mar", "Energy 52", 410),
            Entry("tr-3", "Paint It Black", "The Rolling Stones", 224));

        Assert.Equal("tr-1", m.Match("Judgment Day (feat. Orifice Vulgatron, Ghetto)", ["The Thunderclaps", "Orifice Vulgatron"], 222_500, null)?.Id);
        Assert.Equal("tr-2", m.Match("Cafe Del Mar", ["Energy 52"], 409_000, null)?.Id);
        Assert.Equal("tr-3", m.Match("Paint It, Black - Remastered 2002", ["The Rolling Stones"], 223_000, null)?.Id);
    }

    [Fact]
    public void AnotherArtistOrVersionIsNotTheSameSong()
    {
        var m = Matcher(
            Entry("tr-1", "Hurt", "Nine Inch Nails", 373),
            Entry("tr-2", "Crystal", "EMOTIONAL CLUB", 200));

        Assert.Null(m.Match("Hurt", ["Johnny Cash"], 218_000, null)); // a cover
        Assert.Null(m.Match("Crystal", ["EMOTIONAL CLUB"], 420_000, null)); // the extended mix: much longer
        Assert.Null(m.Match("Crystal (Aerial Remix)", ["EMOTIONAL CLUB"], 200_000, null)); // a remix is another title
    }

    [Fact]
    public void OfSeveralVersionsTheClosestLengthIsTaken()
    {
        var m = Matcher(
            Entry("tr-1", "Blue Monday", "New Order", 448),
            Entry("tr-2", "Blue Monday", "New Order", 452));

        Assert.Equal("tr-2", m.Match("Blue Monday", ["New Order"], 451_000, null)?.Id);
    }

    [Fact]
    public void AnArtistPairIsFoundThroughTheDisplayLine()
    {
        // Library lists the duo as one name, Spotify as two artists – or the other way round.
        var m = Matcher(Entry("tr-1", "The Boxer", "Simon & Garfunkel", 308));

        Assert.Equal("tr-1", m.Match("The Boxer", ["Simon & Garfunkel"], 308_000, null)?.Id);
        Assert.Equal("tr-1", m.Match("The Boxer", ["Simon", "Garfunkel"], 308_000, null)?.Id);
    }
}
