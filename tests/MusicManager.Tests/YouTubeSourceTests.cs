using System.Net;
using System.Text.Json;
using Anthropic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicManager.Core.Ai;
using MusicManager.Core.Configuration;
using MusicManager.Core.Model;
using MusicManager.Core.Services;

namespace MusicManager.Tests;

/// <summary>
/// Audio downloaded from a YouTube video (issue #31): the link in the tags leads to the video, the AI reads artist, title
/// and album from its title, channel and description, and the result is a suggestion with the thumbnail as cover.
/// </summary>
public class YouTubeSourceTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=ntsGK2tlnYY")]
    [InlineData("https://music.youtube.com/watch?v=ntsGK2tlnYY&si=abc")]
    [InlineData("https://www.youtube.com/watch?feature=share&v=ntsGK2tlnYY")]
    [InlineData("https://youtu.be/ntsGK2tlnYY?t=12")]
    [InlineData("https://youtube.com/shorts/ntsGK2tlnYY")]
    public void YouTubeLinksAreFound(string url)
    {
        var link = Assert.Single(ExternalLinks.Find([new TagField("PURL", [url])]));
        Assert.Equal(("youtube", "ntsGK2tlnYY"), (link.Source, link.Id));
    }

    [Fact]
    public void TheSameVideoInTwoFieldsIsOneLink() =>
        Assert.Single(ExternalLinks.Find([
            new TagField("PURL", ["https://www.youtube.com/watch?v=ntsGK2tlnYY"]),
            new TagField("COMMENT", ["https://music.youtube.com/watch?v=ntsGK2tlnYY"]),
        ]));

    [Theory]
    [InlineData("PT1M26S", 86)]
    [InlineData("PT1H26M30S", 5190)]
    [InlineData("P0D", 0)]
    public void DurationsAreRead(string iso, double seconds) => Assert.Equal(seconds, YouTubeClient.ParseDuration(iso));

    /// <summary>Answers the YouTube Data API and the Claude API; remembers what Claude was asked.</summary>
    private sealed class FakeApis(string? aiAnswer) : HttpMessageHandler
    {
        public string? AiRequest;
        public string? YouTubeRequest;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith("https://www.googleapis.com/youtube/v3/videos"))
            {
                YouTubeRequest = url;
                return Json(new
                {
                    items = new[]
                    {
                        new
                        {
                            snippet = new
                            {
                                title = "Car barz the Burial ft Bellyman", channelTitle = "MC Bellyman", publishedAt = "2019-07-09T17:00:00Z",
                                description = "Car barz the Burial ft Bellyman\n\nhttp://soundcloud.com/mcbellyman #dnb",
                                thumbnails = new
                                {
                                    high = new { url = "https://i.ytimg.com/vi/ntsGK2tlnYY/hqdefault.jpg" },
                                    maxres = new { url = "https://i.ytimg.com/vi/ntsGK2tlnYY/maxresdefault.jpg" },
                                },
                            },
                            contentDetails = new { duration = "PT1M26S" },
                        },
                    },
                });
            }

            AiRequest = await request.Content!.ReadAsStringAsync(ct);
            return Json(new
            {
                id = "msg_1", type = "message", role = "assistant", model = "claude-test",
                content = new[] { new { type = "text", text = aiAnswer } },
                stop_reason = "end_turn", stop_sequence = (string?)null,
                usage = new { input_tokens = 10, output_tokens = 10 },
            });
        }

        private static HttpResponseMessage Json(object body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json") };
    }

    private static ExternalSourceService Service(FakeApis apis, string? youTubeKey = "yt-key")
    {
        var options = Options.Create(new MusicManagerOptions { AnthropicApiKey = "test", YouTubeApiKey = youTubeKey });
        var ai = new AiService(options, NullLogger<AiService>.Instance)
        {
            Client = new AnthropicClient { ApiKey = "test", HttpClient = new HttpClient(apis), MaxRetries = 0 },
        };
        return new ExternalSourceService(new SpotifyClient(new HttpClient(apis), options, NullLogger<SpotifyClient>.Instance),
            new DeezerClient(new HttpClient(apis), NullLogger<DeezerClient>.Instance), new YouTubeClient(new HttpClient(apis), options), ai,
            NullLogger<ExternalSourceService>.Instance);
    }

    private static readonly TagField[] Fields =
    [
        new("PURL", ["https://www.youtube.com/watch?v=ntsGK2tlnYY"]),
        new("COMMENT", ["https://music.youtube.com/watch?v=ntsGK2tlnYY"]),
    ];

    [Fact]
    public async Task TheVideoBecomesASuggestionWithItsThumbnail()
    {
        var apis = new FakeApis(JsonSerializer.Serialize(new
        {
            artists = new[] { "MC Bellyman" }, featured = Array.Empty<string>(), title = "Car Barz (The Burial)", album = "", date = "",
            confidence = 0.8, notes = "Kanal ist der Künstler.",
        }));

        var (matches, messages) = await Service(apis).FromLinksAsync(Fields, new TagSet { Artist = "MC Bellyman", Title = "Car Barz" },
            new AppSettings(), CancellationToken.None);

        var m = Assert.Single(matches);
        Assert.Equal("youtube", m.Source);
        Assert.Equal(["MC Bellyman"], m.Artists);
        Assert.Equal("Car Barz (The Burial)", m.Title);
        Assert.Equal("Car Barz (The Burial)", m.Album); // no album named: a single
        Assert.Equal("single", m.AlbumType);
        Assert.Equal("2019-07-09", m.ReleaseDate); // the upload date
        Assert.Equal(86, m.DurationSeconds);
        Assert.Equal("https://i.ytimg.com/vi/ntsGK2tlnYY/maxresdefault.jpg", m.CoverUrl);
        Assert.True(m.FromTagLink);
        Assert.Contains("MC Bellyman – Car Barz (The Burial)", Assert.Single(messages));

        // Claude saw the video and the hand-typed tags; YouTube got the key.
        Assert.Contains("Car barz the Burial ft Bellyman", apis.AiRequest);
        Assert.Contains("MC Bellyman", apis.AiRequest);
        Assert.Contains("key=yt-key", apis.YouTubeRequest);
    }

    [Fact]
    public async Task FeaturedArtistsAndANamedAlbumAreKept()
    {
        var apis = new FakeApis(JsonSerializer.Serialize(new
        {
            artists = new[] { "Barrxn" }, featured = new[] { "Bellyman" }, title = "Praise the Lord (Barrxn Remix)", album = "War Trap Remixes",
            date = "2020-12", confidence = 0.7, notes = "",
        }));

        var m = Assert.Single((await Service(apis).FromLinksAsync(Fields, null, new AppSettings(), CancellationToken.None)).Matches);

        Assert.Equal(["Bellyman"], m.FeaturedArtists);
        Assert.Equal("War Trap Remixes", m.Album);
        Assert.Null(m.AlbumType);
        Assert.Equal("2020-12", m.ReleaseDate);
        var tags = ExternalSourceService.ToTagSet(m);
        Assert.Equal("Barrxn feat. Bellyman", tags.Artist);
    }

    [Fact]
    public async Task WithoutAKeyTheLinkIsOnlyMentioned()
    {
        var apis = new FakeApis(null);

        var (matches, messages) = await Service(apis, youTubeKey: null).FromLinksAsync(Fields, null, new AppSettings(), CancellationToken.None);

        Assert.Empty(matches);
        Assert.Contains("ohne YouTube-API-Schlüssel", Assert.Single(messages));
        Assert.Null(apis.YouTubeRequest);
    }
}
