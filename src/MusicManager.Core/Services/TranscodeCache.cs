using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MusicManager.Core.Configuration;

namespace MusicManager.Core.Services;

/// <summary>
/// Finished transcodes under <c>&lt;data&gt;/cache/transcodes</c>. The first request of a song in a format is encoded while
/// it is sent and written here at the same time; later requests get the file as it is: right away, with its length and
/// with ranges (players can tell the duration at once, e.g. for Symfonium's waveform, and seek by bytes).
/// <para>
/// File names are <c>&lt;source&gt;-&lt;version&gt;-&lt;format&gt;.&lt;suffix&gt;</c>: a hash of the original's path, a hash
/// of its size and time, and format plus bitrate. So the entries of a song can be found by its path: when the original
/// changes, moves or goes, <see cref="Forget"/> (called by the library index) removes them, and writing a new version
/// removes the old ones too. The folder is kept below <see cref="MusicManagerOptions.TranscodeCacheMb"/> by removing
/// what was used longest ago; it can be deleted any time.
/// </para>
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
        return Path.Combine(Root, $"{SourcePrefix(source)}{Version(info)}-{plan.Format.Name}{plan.Kbps}.{plan.Format.Suffix}");
    }

    /// <summary>The start of the names of all entries of one original, whatever its version or the format.</summary>
    private static string SourcePrefix(string source) => $"{Hash(Path.GetFullPath(source))}-";

    private static string Version(FileInfo info) => Hash($"{info.Length}|{info.LastWriteTimeUtc.Ticks}");

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    /// <summary>
    /// Removes every entry of <paramref name="source"/> (all formats and versions): the original was changed, replaced,
    /// moved or deleted, so none of them fits any more. Returns the number of removed files.
    /// </summary>
    public int Forget(string source) => RemoveEntries(SourcePrefix(source), keepVersion: null);

    private int RemoveEntries(string prefix, string? keepVersion)
    {
        if (!Directory.Exists(Root)) return 0;
        var removed = 0;
        foreach (var file in new DirectoryInfo(Root).EnumerateFiles($"{prefix}*"))
        {
            // Unfinished transcodes are not touched here: their writer drops or commits them itself.
            if (file.Name.EndsWith(".tmp", StringComparison.Ordinal)) continue;
            if (keepVersion != null && file.Name.AsSpan(prefix.Length).StartsWith(keepVersion, StringComparison.Ordinal)) continue;
            if (TryDelete(file)) removed++;
        }

        return removed;
    }

    /// <summary>After a new version of <paramref name="path"/> was written: older versions of the same original go.</summary>
    private void RemoveOlderVersions(string path)
    {
        var name = Path.GetFileName(path);
        var parts = name.Split('-', 3);
        if (parts.Length == 3) RemoveEntries($"{parts[0]}-", keepVersion: parts[1]);
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

    private bool TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not remove {File} from the transcode cache", file.Name);
            return false;
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
            cache.RemoveOlderVersions(path);
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
