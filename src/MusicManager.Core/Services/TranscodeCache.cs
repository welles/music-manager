using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MusicManager.Core.Configuration;

namespace MusicManager.Core.Services;

/// <summary>
/// Finished transcodes under <c>&lt;data&gt;/cache/transcodes</c>. The first request of a song in a format is encoded while
/// it is sent and written here at the same time; later requests get the file as it is: right away, with its length and
/// with ranges (players can tell the duration at once, e.g. for Symfonium's waveform, and seek by bytes). The key holds
/// the source file's size and time, so a retagged or replaced song is encoded again. The folder is kept below
/// <see cref="MusicManagerOptions.TranscodeCacheMb"/> by removing what was used longest ago; it can be deleted any time.
/// </summary>
public sealed class TranscodeCache(IOptions<MusicManagerOptions> options, ILogger<TranscodeCache> logger)
{
    private readonly MusicManagerOptions _o = options.Value;
    private readonly Lock _trimLock = new();

    public string Root => Path.Combine(Path.GetFullPath(_o.DataPath), "cache", "transcodes");

    public bool Enabled => _o.TranscodeCacheMb > 0;

    private long MaxBytes => _o.TranscodeCacheMb * 1024L * 1024L;

    /// <summary>The cached transcode of <paramref name="source"/>, if there is one; marks it as just used.</summary>
    public string? Find(string source, TranscodePlan plan)
    {
        if (!Enabled || PathFor(source, plan) is not { } path || !File.Exists(path)) return null;
        try
        {
            // The time of last use: what is used least goes first when the cache is trimmed.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (IOException)
        {
            // Being replaced right now by a parallel request: still a valid file.
        }

        return path;
    }

    /// <summary>
    /// A new cache entry to write the transcode into while it is sent. Null when the cache is off or the folder cannot be
    /// written; streaming then simply goes on without it.
    /// </summary>
    public Entry? Begin(string source, TranscodePlan plan)
    {
        if (!Enabled || PathFor(source, plan) is not { } path) return null;
        try
        {
            Directory.CreateDirectory(Root);
            var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            return new Entry(this, new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true), tmp, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Transcode cache not writable");
            return null;
        }
    }

    /// <summary>Same song file (path, size, time), format and bitrate: same entry. Null when the source is gone.</summary>
    private string? PathFor(string source, TranscodePlan plan)
    {
        var info = new FileInfo(source);
        if (!info.Exists) return null;
        var key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{plan.Format.Name}|{plan.Kbps}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
        return Path.Combine(Root, $"{hash}.{plan.Format.Suffix}");
    }

    /// <summary>
    /// Removes the entries used longest ago until the cache fits its limit again, and left-over temporary files of
    /// transcodes that never finished (older than an hour, so none that is being written).
    /// </summary>
    public void Trim()
    {
        if (!Directory.Exists(Root)) return;
        lock (_trimLock)
        {
            var files = new DirectoryInfo(Root).GetFiles();
            foreach (var tmp in files.Where(f => f.Name.EndsWith(".tmp", StringComparison.Ordinal) && f.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1)))
                TryDelete(tmp);

            long total = 0;
            foreach (var file in files.Where(f => !f.Name.EndsWith(".tmp", StringComparison.Ordinal)).OrderByDescending(f => f.LastWriteTimeUtc))
            {
                total += file.Length;
                if (total > MaxBytes) TryDelete(file);
            }
        }
    }

    private void TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not remove {File} from the transcode cache", file.Name);
        }
    }

    /// <summary>A transcode being written: kept with <see cref="CommitAsync"/> only when it ran through completely.</summary>
    public sealed class Entry(TranscodeCache cache, FileStream stream, string tmp, string path) : IAsyncDisposable
    {
        private bool _done;

        public Stream Stream => stream;

        public async Task CommitAsync()
        {
            if (_done) return;
            _done = true;
            await stream.DisposeAsync();
            File.Move(tmp, path, overwrite: true);
            cache.Trim();
        }

        /// <summary>Drops a transcode that failed or was broken off (client gone): never a partial file in the cache.</summary>
        public async ValueTask DisposeAsync()
        {
            if (_done) return;
            _done = true;
            await stream.DisposeAsync();
            try
            {
                File.Delete(tmp);
            }
            catch (IOException)
            {
                // Trim removes it later.
            }
        }
    }
}
