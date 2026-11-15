namespace MusicManager.Web.Subsonic;

/// <summary>
/// Sends a transcode to the client and into the transcode cache at the same time, so that either side can drop out
/// without taking the other with it:
/// <list type="bullet">
/// <item>Client gone (skip, cancelled pre-cache): the rest goes to the cache file only, the song gets cached complete.</item>
/// <item>Cache write fails (e.g. the data volume is full): the entry is given up (<see cref="CacheFailed"/>), the client
/// keeps getting the song – the cache is a help, never a reason for playback to stop.</item>
/// <item>Both gone: nothing needs the encode any more, so the write fails and the caller stops ffmpeg.</item>
/// </list>
/// </summary>
public sealed class CacheTeeStream(Stream file, Stream client, CancellationToken clientAborted, ILogger logger) : Stream
{
    public bool ClientGone { get; private set; }
    public bool CacheFailed { get; private set; }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (!CacheFailed)
        {
            try
            {
                await file.WriteAsync(buffer, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                CacheFailed = true;
                logger.LogWarning(ex, "Writing the transcode cache failed; streaming on without it");
            }
        }

        if (!ClientGone)
        {
            try
            {
                await client.WriteAsync(buffer, clientAborted);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Connection closed or reset (OperationCanceledException, IOException …).
                ClientGone = true;
            }
        }

        if (ClientGone && CacheFailed)
            throw new OperationCanceledException("Neither the client nor the cache takes the transcode any more");
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => client.Flush();
    public override Task FlushAsync(CancellationToken ct) => ClientGone ? Task.CompletedTask : client.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
}
