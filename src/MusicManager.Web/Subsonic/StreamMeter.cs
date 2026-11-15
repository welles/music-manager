using System.Diagnostics;
using MusicManager.Core.Catalog;

namespace MusicManager.Web.Subsonic;

/// <summary>
/// One log line per stream or download: what was sent (original, range, transcoded), how many bytes, how long it took,
/// the resulting rate and when the first byte went out, and whether the client went away early (skipped). Meant for
/// slow playback on the go (issue #17): the time is measured on the server, where the bytes go into the Cloudflare
/// tunnel, so for files of several MB it follows the speed of the way to the phone. No URL is logged (it holds the
/// API key).
/// </summary>
public static class StreamMeter
{
    public const string Category = "MusicManager.Web.Subsonic.Stream";

    public static void Attach(SubsonicRequest r, CatalogSong song, string mode)
    {
        var http = r.Http;
        var meter = new CountingStream(http.Response.Body);
        http.Response.Body = meter;
        var logger = r.Service<ILoggerFactory>().CreateLogger(Category);
        var range = http.Request.Headers.Range.ToString();
        var client = r.Get("c");
        http.Response.OnCompleted(() =>
        {
            var seconds = Stopwatch.GetElapsedTime(meter.Started).TotalSeconds;
            var kbps = seconds > 0 ? meter.Bytes * 8 / seconds / 1000 : 0;
            var first = meter.FirstByte is { } f ? $"{Stopwatch.GetElapsedTime(meter.Started, f).TotalMilliseconds:0} ms" : "–";
            logger.LogInformation(
                "Stream {Song} [{Mode}{Range}] to {Client}: {Kilobytes:0} KB in {Seconds:0.0} s = {Kbps:0} kbit/s, first byte after {FirstByte}{Aborted}",
                $"{song.DisplayArtist} – {song.Title}", mode, range.Length > 0 ? $", {range}" : "", client ?? "?",
                meter.Bytes / 1024.0, seconds, kbps, first, http.RequestAborted.IsCancellationRequested ? ", aborted by the client" : "");
            return Task.CompletedTask;
        });
    }

    /// <summary>Passes everything on and counts it; notes when the first byte was written.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Started { get; } = Stopwatch.GetTimestamp();
        public long? FirstByte { get; private set; }
        public long Bytes { get; private set; }

        private void Count(int n)
        {
            if (n <= 0) return;
            FirstByte ??= Stopwatch.GetTimestamp();
            Bytes += n;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await inner.WriteAsync(buffer, ct);
            Count(buffer.Length);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await inner.WriteAsync(buffer.AsMemory(offset, count), ct);
            Count(count);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            Count(count);
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Bytes; set => throw new NotSupportedException(); }
    }
}
