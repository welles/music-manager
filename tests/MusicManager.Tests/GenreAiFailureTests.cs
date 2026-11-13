using System.Net;
using Anthropic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicManager.Core.Ai;
using MusicManager.Core.Configuration;
using MusicManager.Core.Model;
using MusicManager.Core.Pipeline;
using MusicManager.Core.Services;

namespace MusicManager.Tests;

/// <summary>
/// Claude only refines the genres: an unreachable API (e.g. 503 "credential validation failed" during an
/// outage) must not fail the file. Seen on a large import where every file of a few minutes ended in Error.
/// </summary>
public class GenreAiFailureTests
{
    private sealed class FailingHandler : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("credential validation failed"),
            });
        }
    }

    private static (GenreService Service, FailingHandler Handler) Create()
    {
        var options = Options.Create(new MusicManagerOptions { AnthropicApiKey = "test" });
        var handler = new FailingHandler();
        var ai = new AiService(options, NullLogger<AiService>.Instance)
        {
            Client = new AnthropicClient { ApiKey = "test", HttpClient = new HttpClient(handler), MaxRetries = 0 },
        };
        var musicBrainz = new MusicBrainzService(options, NullLogger<MusicBrainzService>.Instance);
        var lastFm = new LastFmClient(new HttpClient(), options, NullLogger<LastFmClient>.Instance);
        return (new GenreService(musicBrainz, lastFm, ai), handler);
    }

    private static GenreService.Request Request(params IReadOnlyDictionary<string, int>[] artistGenres) =>
        new(null, new Dictionary<string, int>(), artistGenres, "Artist", "Title", "Artist", "Album", []);

    [Fact]
    public async Task FailedGenreCheckKeepsTheArtistGenresUnconfirmed()
    {
        var (service, handler) = Create();

        var result = await service.ResolveAsync(Request(new Dictionary<string, int> { ["rock"] = 5 }), new AppSettings(), CancellationToken.None);

        Assert.True(handler.Calls > 0);
        Assert.Equal(["Rock"], result.Genres);
        Assert.Equal(GenreOrigin.Format(GenreOrigin.MusicBrainzArtist, GenreConfidence.Low), result.Origin);
        Assert.Contains("KI-Genreprüfung fehlgeschlagen", result.Note);
    }

    [Fact]
    public async Task FailedGenreSuggestionLeavesTheFileWithoutGenres()
    {
        var (service, handler) = Create();

        var result = await service.ResolveAsync(Request(), new AppSettings { AiGenreSuggestions = true }, CancellationToken.None);

        Assert.True(handler.Calls > 0);
        Assert.Empty(result.Genres);
        Assert.Contains("KI-Genrevorschlag fehlgeschlagen", result.Note);
    }
}
