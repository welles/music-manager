using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MusicManager.Core.Configuration;

namespace MusicManager.Core.Services;

/// <summary>
/// Scaled-down copies of covers, artist and playlist images under <c>&lt;data&gt;/cache/thumbnails</c>, for the web pages
/// (tiles, song lists, blurred previews) and Subsonic's getCoverArt alike. Each is made once with ffmpeg (square, JPEG)
/// and then served as a file; the images in the library are never changed.
/// <para>
/// File names are <c>&lt;source&gt;-&lt;version&gt;-&lt;size&gt;.jpg</c>: a hash of the source file's path (a cover file, an
/// audio file with an embedded cover, an artist image), a hash of its size and time, and the edge length. A changed
/// source (e.g. cut to a square, new cover) gets new entries; writing one removes the older versions of the same
/// source, and <see cref="Forget"/> (called by the library index) removes all entries of a song file that changed or
/// went. The folder can be deleted any time.
/// </para>
/// </summary>
public sealed class ThumbnailCache(IOptions<MusicManagerOptions> options, AudioTools audio, ILogger<ThumbnailCache> logger)
{
    /// <summary>Edge of the blurred preview shown until the real image is there (well below 1 KB).</summary>
    public const int PreviewSize = 32;

    /// <summary>Edge of the tiles in the artist, album and playlist grids (sharp up to about 240 CSS px on a 2x screen).</summary>
    public const int TileSize = 480;

    /// <summary>Edge of the covers in song lists (up to 96 CSS px on a 2x screen).</summary>
    public const int SongSize = 192;

    /// <summary>Largest size anyone may ask for; larger requests get this.</summary>
    public const int MaxSize = 2000;

    /// <summary>ffmpeg runs at the same time: a grid of 300 new tiles must not start 300 processes.</summary>
    public static readonly int Parallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    private readonly SemaphoreSlim _ffmpeg = new(Parallelism, Parallelism);
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _making = new();

    public string Root => Path.Combine(Path.GetFullPath(options.Value.DataPath), "cache", "thumbnails");

    /// <summary>Clamps a requested size; null or below 1 means the original.</summary>
    public static int? Normalize(int? size) => size is > 0 ? Math.Min(size.Value, MaxSize) : null;

    /// <summary>The cached copy of <paramref name="source"/> in <paramref name="size"/>, or null when there is none yet.</summary>
    public string? Find(string source, int size) =>
        PathFor(source, size) is { } path && File.Exists(path) ? path : null;

    /// <summary>
    /// The copy of <paramref name="source"/> in <paramref name="size"/>, made now if there is none. <paramref name="load"/>
    /// reads the source image (e.g. the cover embedded in an audio file). Null when the source is gone or cannot be
    /// read or scaled: callers then serve the original.
    /// </summary>
    public async Task<string?> GetAsync(string source, int size, Func<Task<byte[]?>> load, CancellationToken ct)
    {
        if (PathFor(source, size) is not { } path) return null;
        if (File.Exists(path)) return path;

        // Parallel requests for the same picture (the same tile on two pages, a page and the pre-generation) share one
        // run. It does not stop when the one who started it goes away: others may wait for it, and it is short.
        var lazy = _making.GetOrAdd(path, p => new Lazy<Task<string?>>(() => MakeAsync(p, load)));
        var making = lazy.Value;
        _ = making.ContinueWith(_ => _making.TryRemove(new KeyValuePair<string, Lazy<Task<string?>>>(path, lazy)), TaskScheduler.Default);
        return await making.WaitAsync(ct);
    }

    /// <summary><see cref="GetAsync"/> for an image file as the source.</summary>
    public Task<string?> GetAsync(string imageFile, int size, CancellationToken ct) =>
        GetAsync(imageFile, size, async () => File.Exists(imageFile) ? await File.ReadAllBytesAsync(imageFile) : null, ct);

    private async Task<string?> MakeAsync(string path, Func<Task<byte[]?>> load)
    {
        await _ffmpeg.WaitAsync();
        try
        {
            if (File.Exists(path)) return path;
            var image = await load();
            if (image == null) return null;
            var size = int.Parse(Path.GetFileNameWithoutExtension(path).Split('-')[2]);
            var scaled = await audio.ResizeImageAsync(image, size, CancellationToken.None);
            if (scaled == null) return null;

            Directory.CreateDirectory(Root);
            var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(tmp, scaled);
            File.Move(tmp, path, overwrite: true);
            RemoveOlderVersions(path);
            return path;
        }
        catch (Exception ex)
        {
            // E.g. no ffmpeg (only outside the Docker image) or a full disk: the original is served instead.
            logger.LogWarning(ex, "Could not make a thumbnail");
            return null;
        }
        finally
        {
            _ffmpeg.Release();
        }
    }

    /// <summary>Same source file (path, size, time) and size: same entry. Null when the source is gone.</summary>
    private string? PathFor(string source, int size)
    {
        var info = new FileInfo(source);
        if (!info.Exists) return null;
        return Path.Combine(Root, $"{SourcePrefix(source)}{Version(info)}-{size}.jpg");
    }

    private static string SourcePrefix(string source) => $"{Hash(Path.GetFullPath(source))}-";

    private static string Version(FileInfo info) => Hash($"{info.Length}|{info.LastWriteTimeUtc.Ticks}");

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    /// <summary>Removes every entry of <paramref name="source"/> (all sizes and versions): it changed, moved or went.</summary>
    public int Forget(string source) => RemoveEntries(SourcePrefix(source), keepVersion: null);

    /// <summary>After a new version of an entry was written: entries of older versions of the same source go.</summary>
    private void RemoveOlderVersions(string path)
    {
        var parts = Path.GetFileName(path).Split('-', 3);
        if (parts.Length == 3) RemoveEntries($"{parts[0]}-", keepVersion: parts[1]);
    }

    /// <remarks>Never throws: the cache is only a help, cleaning it must not stop a scan or a request.</remarks>
    private int RemoveEntries(string prefix, string? keepVersion)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(Root)) return 0;
            foreach (var file in new DirectoryInfo(Root).EnumerateFiles($"{prefix}*"))
            {
                if (file.Name.EndsWith(".tmp", StringComparison.Ordinal)) continue;
                if (keepVersion != null && file.Name.AsSpan(prefix.Length).StartsWith(keepVersion, StringComparison.Ordinal)) continue;
                try
                {
                    file.Delete();
                    removed++;
                }
                catch (Exception ex) when (IsFileSystemError(ex))
                {
                    logger.LogWarning(ex, "Could not remove {File} from the thumbnail cache", file.Name);
                }
            }
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            logger.LogWarning(ex, "Could not clean up the thumbnail cache");
        }

        return removed;
    }

    /// <summary>
    /// Removes the cache of older versions (<c>&lt;data&gt;/cache/covers</c>, keyed differently and never cleaned up)
    /// and temporary files left by a crash. Never throws.
    /// </summary>
    public void RemoveLeftovers()
    {
        try
        {
            var legacy = Path.Combine(Path.GetFullPath(options.Value.DataPath), "cache", "covers");
            if (Directory.Exists(legacy)) Directory.Delete(legacy, recursive: true);
            if (!Directory.Exists(Root)) return;
            foreach (var tmp in new DirectoryInfo(Root).EnumerateFiles("*.tmp").Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1)))
                tmp.Delete();
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            logger.LogWarning(ex, "Could not remove left-overs of the thumbnail cache");
        }
    }

    private static bool IsFileSystemError(Exception ex) => ex is IOException or UnauthorizedAccessException;
}
