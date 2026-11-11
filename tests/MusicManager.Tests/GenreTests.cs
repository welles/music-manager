using System.Text.Json;
using MusicManager.Core.Configuration;
using MusicManager.Core.Rules;
using MusicManager.Core.Services;

namespace MusicManager.Tests;

public class GenreTests
{
    [Theory]
    [InlineData("drum and bass", "Drum And Bass")]
    [InlineData("Drum & Bass", "Drum And Bass")]
    [InlineData("dnb", "Drum And Bass")]
    [InlineData("hip-hop", "Hip Hop")]
    [InlineData("uk garage", "UK Garage")]
    [InlineData("mpb", "MPB")]
    [InlineData("edm", "EDM")]
    [InlineData("rnb", "R&B")]
    [InlineData("lo-fi", "Lo-Fi")]
    [InlineData("synthpop", "Synth-Pop")]
    [InlineData("k-pop", "K-Pop")]
    public void SpellingFollowsTheLibrary(string raw, string expected) =>
        Assert.Equal([expected], GenreRules.Finish([raw], new AppSettings()));

    [Fact]
    public void SelectionKeepsStrongGenresAndDropsTheFilter()
    {
        var votes = new Dictionary<string, double>();
        GenreRules.Add(votes, new Dictionary<string, int> { ["hip hop"] = 10, ["german hip hop"] = 6, ["seen live"] = 20, ["rap"] = 2 });
        Assert.Equal(["hip hop", "german hip hop"], GenreRules.Select(votes, new AppSettings()));
    }

    [Fact]
    public void WeightedRecordingGenresWinOverTheRelease()
    {
        var votes = new Dictionary<string, double>();
        GenreRules.Add(votes, new Dictionary<string, int> { ["uk garage"] = 3 }, weight: 2);
        GenreRules.Add(votes, new Dictionary<string, int> { ["drum and bass"] = 5 });
        Assert.Equal(["uk garage", "drum and bass"], GenreRules.Select(votes, new AppSettings()));
    }

    [Fact]
    public void OwnSpellingsAndRemovalsComeLast()
    {
        var settings = new AppSettings { GenreMap = ["Drum And Bass = Drum & Bass", "electronic ="] };
        Assert.Equal(["Drum & Bass", "Jungle"], GenreRules.Finish(["drum and bass", "Electronic", "jungle", "DnB"], settings));
    }

    [Fact]
    public void LastFmTopTagsAreParsed()
    {
        using var doc = JsonDocument.Parse("""
            {"toptags":{"tag":[{"name":"drum and bass","count":100},{"name":"seen live","count":"12"},{"name":"","count":5},{"name":"x","count":0}]}}
            """);
        Assert.Equal([new("drum and bass", 100), new KeyValuePair<string, int>("seen live", 12)], LastFmClient.Parse(doc.RootElement));

        using var error = JsonDocument.Parse("""{"error":6,"message":"Track not found"}""");
        Assert.Empty(LastFmClient.Parse(error.RootElement));
    }
}
