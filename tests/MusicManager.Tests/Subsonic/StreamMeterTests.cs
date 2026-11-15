using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MusicManager.Core.Model;
using MusicManager.Web.Subsonic;

namespace MusicManager.Tests.Subsonic;

/// <summary>One log line per stream with size, time and rate, to see how fast songs reach the phone (issue #17).</summary>
public class StreamMeterTests
{
    private sealed class Capture : ILoggerProvider
    {
        public ConcurrentQueue<string> Written { get; } = new();
        public ILogger CreateLogger(string category) => new Lines(category == StreamMeter.Category ? Written : null);
        public void Dispose() { }

        private sealed class Lines(ConcurrentQueue<string>? lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => lines != null;

            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> format) =>
                lines?.Enqueue(format(state, ex));
        }
    }

    /// <summary>The line is written once the response is complete, slightly after the client has it.</summary>
    private static async Task<string> NextLineAsync(Capture capture)
    {
        for (var i = 0; i < 100; i++)
        {
            if (capture.Written.TryDequeue(out var line)) return line;
            await Task.Delay(20);
        }

        throw new TimeoutException("No stream log line");
    }

    [Fact]
    public async Task EveryStreamIsMeasuredWithoutTheKey()
    {
        using var app = new SubsonicTestHost();
        var path = app.AddFile("A/B/1.m4a", new TagSet { Title = "Song", Artist = "Band", Album = "B", AlbumArtist = "Band" });
        await app.ScanAsync();
        var id = (await app.EntryAsync("A/B/1.m4a"))!.SongId;
        var capture = new Capture();
        using var host = app.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<ILoggerProvider>(capture)));
        var client = host.CreateClient();

        var full = await client.GetByteArrayAsync($"/rest/stream?apiKey={SubsonicTestHost.Key}&id={id}&c=Symfonium");
        var line = await NextLineAsync(capture);

        Assert.Equal(await File.ReadAllBytesAsync(path), full);
        Assert.Contains("Band – Song", line);
        Assert.Contains("[original m4a", line);
        Assert.Contains("to Symfonium", line);
        Assert.Contains("kbit/s", line);
        Assert.DoesNotContain(SubsonicTestHost.Key, line);

        // Ranges still work through the meter and show up in the line.
        var request = new HttpRequestMessage(HttpMethod.Get, $"/rest/stream?apiKey={SubsonicTestHost.Key}&id={id}");
        request.Headers.Range = new RangeHeaderValue(0, 99);
        var partial = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(100, (await partial.Content.ReadAsByteArrayAsync()).Length);
        Assert.Contains("bytes=0-99", await NextLineAsync(capture));
    }
}
