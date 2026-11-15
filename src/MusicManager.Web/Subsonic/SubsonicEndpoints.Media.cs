using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MusicManager.Core.Catalog;
using MusicManager.Core.Configuration;
using MusicManager.Core.Pipeline;
using MusicManager.Core.Services;

namespace MusicManager.Web.Subsonic;

// Media: stream, download and cover art (album folder cover, embedded cover, artist image).
public static partial class SubsonicEndpoints
{
    private static void MapMedia()
    {
        // The original file (with ranges), or transcoded on the fly when the client asks for a lower bitrate or
        // another format (maxBitRate, format, timeOffset, estimateContentLength); download always gets the original.
        Add("stream", r => ServeSongAsync(r, download: false));
        Add("download", r => ServeSongAsync(r, download: true));
        Add("getCoverArt", GetCoverArtAsync);
    }

    private static async Task<IResult> ServeSongAsync(SubsonicRequest r, bool download)
    {
        var catalog = await Catalog(r);
        var song = catalog.Song(r.Require("id")) ?? throw SubsonicException.NotFound("Song");
        var path = r.Service<Storage>().LibraryPath(song.RelativePath);
        if (!File.Exists(path)) throw SubsonicException.NotFound("File");
        // A jump into the song (timeOffset) needs a stream from that point even when the client wants the original.
        var plan = Transcoding.Decide(song.Suffix, song.BitrateKbps, r.Get("format"), r.GetInt("maxBitRate"))
                   ?? (r.GetInt("timeOffset") is > 0 && !IsRaw(r) ? Transcoding.ForOffset(song.Suffix, song.BitrateKbps) : null);
        if (!download && plan != null)
        {
            // Parameters first: one that fails must not leave a started ffmpeg behind without a reader.
            // A duration of 0 means unknown (unreadable file): then the offset is not limited.
            var offset = Math.Max(0, r.GetInt("timeOffset") ?? 0);
            if (song.Duration > 0) offset = Math.Min(offset, song.Duration - 1);
            var estimate = r.GetBool("estimateContentLength") == true;
            var mode = $"{plan.Format.Name} {plan.Kbps}k{(offset > 0 ? $" from {offset} s" : "")}";

            // Encoded before: the finished file, with its length and ranges.
            if (offset == 0 && r.Service<TranscodeCache>().Find(path, plan) is { } cached)
            {
                StreamMeter.Attach(r, song, $"{mode} cached");
                return Results.File(cached, plan.Format.ContentType, enableRangeProcessing: true);
            }

            if (Transcoded(r, path, song, plan, offset, estimate) is { } transcoded)
            {
                StreamMeter.Attach(r, song, mode);
                return transcoded;
            }
        }

        StreamMeter.Attach(r, song, download ? "download" : $"original {song.Suffix}{(song.BitrateKbps is { } k ? $" {k}k" : "")}");
        return Results.File(path, SubsonicMapper.ContentType(path), download ? Path.GetFileName(path) : null, enableRangeProcessing: true);
    }

    private static bool IsRaw(SubsonicRequest r) => string.Equals(r.Get("format")?.Trim(), "raw", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The song encoded while it is sent. No ranges (the size is not known in advance); a jump into the song comes as
    /// <c>timeOffset</c> in seconds (OpenSubsonic "transcodeOffset"). With <c>estimateContentLength</c> the response
    /// announces an estimated size and is padded or cut to exactly that, as some players need a length.
    /// </summary>
    /// <remarks>
    /// Null when ffmpeg cannot be started: then the original is served, music rather than an error. A whole song (no
    /// offset) is written to the <see cref="TranscodeCache"/> while it is sent and kept there if it ran through.
    /// </remarks>
    private static IResult? Transcoded(SubsonicRequest r, string path, CatalogSong song, TranscodePlan plan, int offset, bool estimate)
    {
        // Without a known duration no length is announced: an estimate from nothing would cut the song after a second.
        long? length = estimate && song.Duration > 0 ? Transcoding.EstimateBytes(plan, song.Duration, offset) : null;

        // HEAD only wants the headers: no need to encode the whole song for a body nobody receives.
        if (HttpMethods.IsHead(r.Http.Request.Method))
        {
            SetTranscodedHeaders(r, length);
            return Results.Stream(_ => Task.CompletedTask, plan.Format.ContentType);
        }

        var audio = r.Service<AudioTools>();
        if (audio.StartTranscode(path, plan, offset) is not { } ffmpeg) return null;

        var entry = offset == 0 ? r.Service<TranscodeCache>().Begin(path, plan) : null;
        SetTranscodedHeaders(r, length);
        return Results.Stream(async output =>
        {
            await using var _ = entry;
            var exact = length != null ? new ExactLengthStream(output, length.Value) : null;
            Stream target = exact ?? output;
            if (entry == null)
            {
                // Nothing to keep: when the client goes away (skip), ffmpeg is stopped right there.
                await audio.PumpAsync(ffmpeg, path, target, r.Aborted);
                if (exact != null) await exact.PadAsync(r.Aborted);
                return;
            }

            // Into the cache as well: the encode runs to the end even when the client goes away early (a skip, a
            // cancelled pre-cache), so the next request finds the whole song. Only a server shutdown stops it.
            // The cache gets ffmpeg's output as it is, before an estimated length pads or cuts what the client gets.
            var tee = new TeeStream(entry.Stream, target, r.Aborted);
            var complete = await audio.PumpAsync(ffmpeg, path, tee, r.Service<IHostApplicationLifetime>().ApplicationStopping);
            if (exact != null && !tee.ClientGone) await exact.PadAsync(r.Aborted);
            if (complete) await entry.CommitAsync();
        }, plan.Format.ContentType);
    }

    /// <summary>
    /// Writes everything to the cache file first, then to the client. Once the client is gone, the rest only goes to
    /// the file: a failing client write ends nothing but its own part.
    /// </summary>
    private sealed class TeeStream(Stream file, Stream client, CancellationToken clientAborted) : Stream
    {
        public bool ClientGone { get; private set; }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await file.WriteAsync(buffer, ct);
            if (ClientGone) return;
            try
            {
                await client.WriteAsync(buffer, clientAborted);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Connection closed or reset (OperationCanceledException, IOException …): the cache still wants the rest.
                ClientGone = true;
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => client.Flush();
        public override Task FlushAsync(CancellationToken ct) => client.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    }

    private static void SetTranscodedHeaders(SubsonicRequest r, long? length)
    {
        r.Http.Response.Headers.AcceptRanges = "none";
        if (length != null) r.Http.Response.ContentLength = length;
    }

    /// <summary>Passes on at most <c>length</c> bytes and fills up to it with zeros at the end (players skip those).</summary>
    private sealed class ExactLengthStream(Stream inner, long length) : Stream
    {
        private long _written;

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            var take = (int)Math.Min(buffer.Length, length - _written);
            if (take <= 0) return;
            await inner.WriteAsync(buffer[..take], ct);
            _written += take;
        }

        public async Task PadAsync(CancellationToken ct)
        {
            var zeros = new byte[16 * 1024];
            while (_written < length)
            {
                var n = (int)Math.Min(zeros.Length, length - _written);
                await inner.WriteAsync(zeros.AsMemory(0, n), ct);
                _written += n;
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _written; set => throw new NotSupportedException(); }
    }

    private static async Task<IResult> GetCoverArtAsync(SubsonicRequest r)
    {
        var id = r.Require("id");
        var requested = r.GetInt("size");
        int? size = requested > 0 ? Math.Min(requested.Value, 2000) : null;
        var catalog = await Catalog(r);
        var storage = r.Service<Storage>();

        // Source image and a stamp that changes whenever the image may have changed (cache key).
        Func<Task<byte[]?>>? load = null;
        string? stamp = null;
        if (id.StartsWith(CatalogIds.ArtistPrefix, StringComparison.Ordinal) && catalog.Artist(id) is { } artist)
        {
            var file = await r.Service<ArtistImageService>().GetImagePathAsync(artist.Name, artist.MbArtistId, r.Aborted);
            if (file != null)
            {
                var info = new FileInfo(file);
                stamp = $"{file}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                load = async () => await File.ReadAllBytesAsync(file, r.Aborted);
            }
        }
        else
        {
            var song = id.StartsWith(CatalogIds.SongPrefix, StringComparison.Ordinal) ? catalog.Song(id) : null;
            var album = song?.Album ?? catalog.Album(id);
            song ??= album?.CoverSong;
            if (song is { HasCover: false }) song = song.Album.CoverSong;

            // A cover file in the album's own folder (usually larger) goes before the embedded cover.
            if (album?.Folder is { } folder && AlbumCoverService.Find(storage.LibraryPath(folder)) is { } coverFile)
            {
                var info = new FileInfo(coverFile);
                stamp = $"{coverFile}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                load = async () => File.Exists(coverFile) ? await File.ReadAllBytesAsync(coverFile, r.Aborted) : null;
            }
            else if (song != null)
            {
                var path = storage.LibraryPath(song.RelativePath);
                stamp = $"{song.RelativePath}|{song.Size}|{song.FileModifiedUtc.Ticks}";
                load = () => Task.FromResult(File.Exists(path) ? TagIo.ReadEmbeddedCover(path) : null);
            }
        }

        if (load == null) throw SubsonicException.NotFound("Cover art");
        r.Http.Response.Headers.CacheControl = "private, max-age=3600";

        if (size == null)
        {
            var original = await load() ?? throw SubsonicException.NotFound("Cover art");
            return Results.File(original, ImageType(original));
        }

        var cacheDir = Path.Combine(Path.GetFullPath(r.Service<IOptions<MusicManagerOptions>>().Value.DataPath), "cache", "covers");
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{id}|{stamp}|{size}")))[..32];
        var cached = Path.Combine(cacheDir, $"{key}.jpg");
        if (File.Exists(cached)) return Results.File(cached, "image/jpeg");

        var image = await load() ?? throw SubsonicException.NotFound("Cover art");
        var scaled = await r.Service<AudioTools>().ResizeImageAsync(image, size.Value, r.Aborted);
        if (scaled == null) return Results.File(image, ImageType(image));

        Directory.CreateDirectory(cacheDir);
        var tmp = $"{cached}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(tmp, scaled, r.Aborted);
        File.Move(tmp, cached, overwrite: true);
        return Results.File(scaled, "image/jpeg");
    }

    private static string ImageType(byte[] data) => data.Length > 3 && data[0] == 0x89 && data[1] == 0x50 ? "image/png" : "image/jpeg";
}
