using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MusicManager.Core.Catalog;
using MusicManager.Core.Data;
using MusicManager.Core.Rules;

namespace MusicManager.Core.Pipeline;

/// <summary>
/// Keeps every playlist as an M3U8 file in the library's <see cref="PlaylistFile.Folder"/> folder, in both directions:
/// <list type="bullet">
/// <item>Changes in the app (Symfonium) and moved songs rewrite the file.</item>
/// <item>A file edited outside the tool (newer than the last change in the app) is taken over.</item>
/// <item>An unknown file becomes a playlist; after a lost database this restores all playlists, with their old IDs.</item>
/// <item>A file renamed outside the tool renames the playlist; a deleted file is written again (delete in the app).</item>
/// </list>
/// </summary>
public sealed partial class PlaylistFileSync(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settingsService,
    Storage storage,
    LibraryIndexer indexer,
    PlaylistStore playlists,
    ILogger<PlaylistFileSync> logger) : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    /// <summary>The folder is checked for outside edits this often.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public sealed record Result(int Imported, int Written);

    public void Request()
    {
        if (_signal.CurrentCount == 0) _signal.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        indexer.IndexChanged += Request;
        playlists.Changed += Request;

        while (!ct.IsCancellationRequested)
        {
            if (await _signal.WaitAsync(PollInterval, ct)) await Task.Delay(Debounce, ct);
            try
            {
                await SyncAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Syncing the playlist files failed");
            }
        }
    }

    public async Task<Result> SyncAsync(CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(ct);
        if (!settings.PlaylistFiles || !Directory.Exists(storage.LibraryRoot)) return new Result(0, 0);

        await _lock.WaitAsync(ct);
        try
        {
            return await SyncCoreAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Result> SyncCoreAsync(CancellationToken ct)
    {
        var dir = storage.LibraryPath(PlaylistFile.Folder);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var all = await db.Playlists.Include(p => p.Entries).OrderBy(p => p.Id).ToListAsync(ct);
        foreach (var p in all) p.Entries.Sort((a, b) => a.Position != b.Position ? a.Position.CompareTo(b.Position) : a.Id.CompareTo(b.Id));

        var songs = await db.Library.AsNoTracking().Where(e => e.SongId != null)
            .Select(e => new LibraryEntry { RelativePath = e.RelativePath, SongId = e.SongId, DurationSeconds = e.DurationSeconds, Artist = e.Artist, Title = e.Title })
            .ToListAsync(ct);
        var bySong = songs.GroupBy(e => e.SongId!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var byPath = songs.ToDictionary(e => e.RelativePath, e => e.SongId!, StringComparer.Ordinal);

        var files = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*", new EnumerationOptions { AttributesToSkip = FileAttributes.Hidden })
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is PlaylistFile.Extension or ".m3u")
                .ToDictionary(Path.GetFileName, f => f, StringComparer.Ordinal)
            : [];
        var imported = 0;

        // Files the database does not know: renamed outside the tool, restored, or new.
        var known = all.Where(p => p.FileName != null).Select(p => p.FileName!).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, path) in files.Where(f => !known.Contains(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            var parsed = PlaylistFile.Parse(Encoding.UTF8.GetString(bytes));
            var owner = parsed.Id is { } id ? all.FirstOrDefault(p => p.Id == id) : null;
            if (owner == null || (owner.FileName != null && files.ContainsKey(owner.FileName)))
            {
                // A copy of a playlist that still has its file gets a new ID.
                owner = new Playlist
                {
                    Id = parsed.Id is { } free && all.All(p => p.Id != free) ? free : 0,
                    Name = name,
                    CreatedAt = parsed.CreatedAt ?? File.GetLastWriteTimeUtc(path),
                };
                db.Playlists.Add(owner);
                all.Add(owner);
            }

            owner.FileName = name;
            Import(owner, parsed, name, bytes, dir, bySong, byPath);
            imported++;
        }

        // Known files edited outside the tool after the last change in the app.
        foreach (var p in all.Where(p => p.FileName != null && p.FileHash != null && files.ContainsKey(p.FileName!)))
        {
            var path = files[p.FileName!];
            var bytes = await File.ReadAllBytesAsync(path, ct);
            if (PlaylistFile.Hash(bytes) == p.FileHash || File.GetLastWriteTimeUtc(path) <= p.ChangedAt.UtcDateTime) continue;
            Import(p, PlaylistFile.Parse(Encoding.UTF8.GetString(bytes)), p.FileName!, bytes, dir, bySong, byPath);
            imported++;
        }

        // Write every playlist whose file is missing or differs (app changes, moved songs, normalizing imports).
        var written = 0;
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in all)
        {
            var wanted = PathBuilder.SanitizeComponent(p.Name);
            var fileName = p.FileName != null && IsNameFor(p.FileName, wanted) && taken.Add(p.FileName) ? p.FileName : null;
            if (fileName == null)
            {
                for (var n = 1; ; n++)
                {
                    var candidate = (n == 1 ? wanted : $"{wanted} ({n})") + PlaylistFile.Extension;
                    var ownedByOther = all.Any(o => o != p && o.FileName == candidate);
                    if (!ownedByOther && taken.Add(candidate))
                    {
                        fileName = candidate;
                        break;
                    }
                }
            }

            var content = PlaylistFile.Render(p, id => bySong.GetValueOrDefault(id));
            var hash = PlaylistFile.Hash(content);
            var target = Path.Combine(dir, fileName);
            if (p.FileName != fileName && p.FileName != null && files.TryGetValue(p.FileName, out var old)) File.Delete(old);

            if (!File.Exists(target) || PlaylistFile.Hash(await File.ReadAllBytesAsync(target, ct)) != hash)
            {
                Directory.CreateDirectory(dir);
                var temp = Path.Combine(dir, $".{Guid.NewGuid():N}.tmp");
                await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), ct);
                File.Move(temp, target, true);
                written++;
            }

            p.FileName = fileName;
            p.FileHash = hash;
        }

        await db.SaveChangesAsync(ct);
        if (imported > 0) logger.LogInformation("{Count} playlist file(s) taken over from the playlist folder", imported);
        if (written > 0) logger.LogInformation("{Count} playlist file(s) written", written);
        return new Result(imported, written);
    }

    private void Import(Playlist playlist, PlaylistFile.Parsed parsed, string fileName, byte[] bytes, string dir,
        Dictionary<string, LibraryEntry> bySong, Dictionary<string, string> byPath)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        // The name in the file unless the file was renamed (the name may contain characters a file name cannot).
        playlist.Name = parsed.Name is { Length: > 0 } n && IsNameFor(fileName, PathBuilder.SanitizeComponent(n)) ? n : stem;
        playlist.Comment = parsed.Comment.NullIfEmpty();
        playlist.Public = parsed.Public;

        var songIds = new List<string>();
        var skipped = 0;
        foreach (var entry in parsed.Entries)
        {
            // The path wins (it is what other tools edit); the song ID finds a moved file or keeps a missing song.
            var songId = entry.Path != null && LibraryRelative(entry.Path, dir) is { } rel && byPath.TryGetValue(rel, out var byPathId)
                ? byPathId
                : entry.SongId;
            if (songId != null) songIds.Add(songId);
            else skipped++;
        }

        if (skipped > 0) logger.LogWarning("Playlist file {File}: {Count} entr(ies) not in the library skipped", fileName, skipped);

        playlist.Entries.Clear();
        playlist.Entries.AddRange(songIds.Select((id, i) => new PlaylistEntry { SongId = id, Position = i }));
        playlist.ChangedAt = DateTimeOffset.UtcNow;
        playlist.FileHash = PlaylistFile.Hash(bytes);
    }

    /// <summary>The library-relative path of a playlist line (relative to the playlist file, or absolute in the library).</summary>
    private string? LibraryRelative(string line, string dir)
    {
        var path = line.Trim();
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) path = Uri.UnescapeDataString(path["file://".Length..]);
        if (path.Contains("://", StringComparison.Ordinal)) return null;
        path = path.Replace('\\', '/');
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(dir, path));
        var rel = Path.GetRelativePath(storage.LibraryRoot, full);
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? null : rel;
    }

    /// <summary>"Name.m3u8" or "Name (2).m3u8" for the sanitized name "Name".</summary>
    private static bool IsNameFor(string fileName, string sanitizedName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem == sanitizedName) return true;
        var m = NumberSuffix().Match(stem);
        return m.Success && m.Groups[1].Value == sanitizedName;
    }

    [GeneratedRegex(@"^(.*) \(\d+\)$")]
    private static partial Regex NumberSuffix();
}
