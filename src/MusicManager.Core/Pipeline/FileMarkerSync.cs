using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MusicManager.Core.Catalog;
using MusicManager.Core.Data;
using MusicManager.Core.Model;
using MusicManager.Core.Services;

namespace MusicManager.Core.Pipeline;

/// <summary>
/// Mirrors song ID, added date, favorite and rating of library songs into their files
/// (<see cref="FileMarkers"/>), so the library can be rebuilt from the files alone. Runs a few seconds after
/// the index or the user data changed and only touches files whose markers differ from the database
/// (<see cref="LibraryEntry.MarkersInFile"/>). Only M4A files written by this tool are changed; other files
/// are left alone. Files without a tag hash yet (<see cref="TagHashes"/>) get one on the same occasion.
/// </summary>
public sealed class FileMarkerSync(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settingsService,
    Storage storage,
    LibraryIndexer indexer,
    UserDataStore userData,
    ILogger<FileMarkerSync> logger) : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Request()
    {
        if (_signal.CurrentCount == 0) _signal.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        indexer.IndexChanged += Request;
        userData.MarksChanged += ids =>
        {
            if (ids.Any(id => id.StartsWith(CatalogIds.SongPrefix, StringComparison.Ordinal))) Request();
        };
        Request();

        while (!ct.IsCancellationRequested)
        {
            await _signal.WaitAsync(ct);
            await Task.Delay(Debounce, ct);
            try
            {
                await SyncAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Writing song markers into the files failed");
            }
        }
    }

    /// <summary>Writes the markers of every file that is out of date. Returns the number of written files.</summary>
    public async Task<int> SyncAsync(CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(ct);
        if (!settings.WriteMarkersToFiles) return 0;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entries = await db.Library.Where(e => e.SongId != null && e.TagSource != null && e.RelativePath.EndsWith(".m4a")).ToListAsync(ct);
        if (entries.Count == 0) return 0;

        var data = await userData.GetAsync(ct);
        var written = 0;
        foreach (var entry in entries)
        {
            var wanted = new FileMarkers(entry.SongId, entry.AddedAt, data.Rating(entry.SongId!), data.Starred(entry.SongId!));
            var canonical = wanted.Canonical();
            // Files written before the tag hash existed get one now, as the baseline for detecting outside edits.
            var needsHash = entry.TagsHash == null;
            if (canonical == entry.MarkersInFile && !needsHash) continue;

            var path = storage.LibraryPath(entry.RelativePath);
            var info = new FileInfo(path);
            // Changed on disk since the last scan: let the scan read it first instead of overwriting it.
            if (!info.Exists || info.Length != entry.FileSize || info.LastWriteTimeUtc != entry.FileModifiedUtc) continue;

            try
            {
                var hashes = TagIo.WriteMarkers(path, wanted, storeHashes: needsHash);
                info.Refresh();
                entry.MarkersInFile = canonical;
                if (hashes != null) entry.TagsHash = hashes.Tags;
                // The index stays "unchanged" for this file, so the next scan does not read it again.
                entry.FileSize = info.Length;
                entry.FileModifiedUtc = info.LastWriteTimeUtc;
                written++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TagLib.CorruptFileException)
            {
                logger.LogWarning("Could not write markers into {Path}: {Message}", entry.RelativePath, ex.Message);
            }
        }

        if (written > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Song ID, added date, favorite and rating written into {Count} file(s)", written);
        }

        return written;
    }
}
