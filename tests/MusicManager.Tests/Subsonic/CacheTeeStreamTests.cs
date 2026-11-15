using Microsoft.Extensions.Logging.Abstractions;
using MusicManager.Web.Subsonic;

namespace MusicManager.Tests.Subsonic;

/// <summary>Client and cache get the transcode side by side; one failing must not stop the other.</summary>
public class CacheTeeStreamTests
{
    /// <summary>Takes <c>limit</c> bytes, then fails like a full disk or a closed connection.</summary>
    private sealed class FailingAfter(int limit, Exception error) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (Length + buffer.Length > limit) throw error;
            return base.WriteAsync(buffer, ct);
        }
    }

    private static readonly byte[] Chunk = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();

    private static async Task WriteChunksAsync(Stream tee, int count)
    {
        for (var i = 0; i < count; i++) await tee.WriteAsync(Chunk);
    }

    [Fact]
    public async Task AFullCacheDiskDoesNotStopPlayback()
    {
        var file = new FailingAfter(250, new IOException("No space left on device"));
        var client = new MemoryStream();
        var tee = new CacheTeeStream(file, client, default, NullLogger.Instance);

        await WriteChunksAsync(tee, 10);

        Assert.True(tee.CacheFailed);
        Assert.False(tee.ClientGone);
        Assert.Equal(1000, client.Length); // the client got the whole song
    }

    [Fact]
    public async Task AClientThatHangsUpLeavesTheRestToTheCache()
    {
        var file = new MemoryStream();
        var client = new FailingAfter(300, new IOException("Connection reset"));
        var tee = new CacheTeeStream(file, client, default, NullLogger.Instance);

        await WriteChunksAsync(tee, 10);

        Assert.True(tee.ClientGone);
        Assert.False(tee.CacheFailed);
        Assert.Equal(1000, file.Length); // complete in the cache
    }

    [Fact]
    public async Task WhenNeitherTakesItAnyMoreTheEncodeIsStopped()
    {
        var file = new FailingAfter(200, new IOException("No space left on device"));
        var client = new FailingAfter(500, new OperationCanceledException());
        var tee = new CacheTeeStream(file, client, default, NullLogger.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WriteChunksAsync(tee, 10));
        Assert.Equal(500, client.Length);
    }
}
