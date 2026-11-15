using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MusicManager.Core.Configuration;
using MusicManager.Core.Data;
using MusicManager.Core.Services;

namespace MusicManager.Core.Pipeline;

/// <summary>State of the run that cuts existing images to squares.</summary>
/// <param name="Phase">What is being worked on ("Album-Cover", "Künstlerbilder", "Eingebettete Cover").</param>
/// <param name="Squared">Images that were not square and were cut.</param>
/// <param name="Skipped">Song files changed on disk since the last scan; left for the next run.</param>
public sealed record ImageSquareState(bool Running, string? Phase, int Progress, int Total, int Squared, int Skipped, int Failed);

/// <summary>
/// Album and artist images are square: new ones are cut when they are fetched (<see cref="AudioTools.SquareImageAsync"/>).
/// This cuts the ones that exist already, on request: the cover files in the album folders (incl. own images;
/// the originals go to the trash), the cached artist images and the covers embedded in the library's M4A files.
/// Square images stay untouched.
/// </summary>
public sealed class ImageSquareService(
    IDbContextFactory<AppDbContext> dbFactory,
    AudioTools audio,
    Storage storage,
    IOptions<MusicManagerOptions> options,
    LibraryIndexer indexer,
    PipelineNotifier notifier,
    ILogger<ImageSquareService> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _phase;
    private int _progress, _total, _squared, _skipped, _failed;

    public ImageSquareState State => new(_lock.CurrentCount == 0, _phase, _progress, _total, _squared, _skipped, _failed);

    /// <summary>Starts a run in the background; false when one is already running.</summary>
    public bool Start()
    {
        if (!_lock.Wait(0)) return false;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Cutting images to squares failed");
            }
            finally
            {
                _phase = null;
                _lock.Release();
                notifier.Notify();
            }
        });
        return true;
    }

    internal async Task RunAsync(CancellationToken ct)
    {
        _progress = _squared = _skipped = _failed = 0;
        var folderCovers = Directory.Exists(storage.LibraryRoot)
            ? Directory.EnumerateFiles(storage.LibraryRoot, "*", SearchOption.AllDirectories)
                .Where(f => AlbumCoverService.IsCoverFile(Path.GetFileName(f))).ToList()
            : [];

        var dataRoot = Path.GetFullPath(options.Value.DataPath);
        List<string> artistImages;
        List<int> songs;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            artistImages = (await db.ArtistImages.Where(a => a.FilePath != null).Select(a => a.FilePath!).ToListAsync(ct))
                .Select(p => Path.Combine(dataRoot, p)).Where(File.Exists).ToList();
            songs = await db.Library.Where(e => e.HasCover && e.RelativePath.EndsWith(".m4a")).Select(e => e.Id).ToListAsync(ct);
        }

        _total = folderCovers.Count + artistImages.Count + songs.Count;
        notifier.Notify();

        _phase = "Album-Cover";
        // Cover files may be own images: the original goes to the trash. Artist images are a cache that can be
        // loaded again.
        foreach (var path in folderCovers) await ImageFileAsync(path, p => storage.KeepOriginalInTrash(p, TrashFolder), ct);
        _phase = "Künstlerbilder";
        foreach (var path in artistImages) await ImageFileAsync(path, null, ct);
        _phase = "Eingebettete Cover";
        await EmbeddedCoversAsync(songs, ct);

        logger.LogInformation("Square images: {Squared} cut, {Skipped} skipped, {Failed} failed, {Total} checked",
            _squared, _skipped, _failed, _total);
    }

    /// <summary>Folder in the trash for the originals of cut cover files.</summary>
    public const string TrashFolder = "zugeschnitten";

    private async Task ImageFileAsync(string path, Action<string>? keepOriginal, CancellationToken ct)
    {
        try
        {
            if (await audio.SquareImageFileAsync(path, keepOriginal, ct)) _squared++;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Could not cut {Path} to a square: {Message}", path, ex.Message);
            _failed++;
        }

        Step();
    }

    private async Task EmbeddedCoversAsync(List<int> songIds, CancellationToken ct)
    {
        var squaredBefore = _squared;
        try
        {
            await EmbeddedCoverChunksAsync(songIds, ct);
        }
        finally
        {
            // New file size and time are in the index: the catalog reloads, so cover caches keyed on them (Subsonic
            // getCoverArt) serve the new cover. The next scan finds the files unchanged and would not do it.
            if (_squared > squaredBefore) indexer.OnChanged();
        }
    }

    private async Task EmbeddedCoverChunksAsync(List<int> songIds, CancellationToken ct)
    {
        foreach (var chunk in songIds.Chunk(100))
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entries = await db.Library.Where(e => chunk.Contains(e.Id)).ToListAsync(ct);
            foreach (var entry in entries)
            {
                try
                {
                    await EmbeddedCoverAsync(entry, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning("Could not cut the cover of {Path} to a square: {Message}", entry.RelativePath, ex.Message);
                    _failed++;
                }

                Step();
            }

            // An entry the index removed in the meantime (song moved, replaced or deleted) must not end the run.
            await db.SaveSkippingDeletedAsync(ct);
        }
    }

    private async Task EmbeddedCoverAsync(LibraryEntry entry, CancellationToken ct)
    {
        var path = storage.LibraryPath(entry.RelativePath);
        var info = new FileInfo(path);
        // Changed on disk since the last scan: let the scan read it first instead of overwriting it.
        if (!info.Exists || info.Length != entry.FileSize || info.LastWriteTimeUtc != entry.FileModifiedUtc)
        {
            _skipped++;
            return;
        }

        if (TagIo.ReadEmbeddedCover(path) is not { } cover) return;
        var square = await audio.SquareImageAsync(cover, null, ct);
        if (square == null || ReferenceEquals(square, cover)) return;

        var hashes = TagIo.ReplaceCover(path, square);
        info.Refresh();
        if (hashes != null) entry.TagsHash = hashes.Tags;
        // The index stays "unchanged" for this file, so the next scan does not read it again.
        entry.FileSize = info.Length;
        entry.FileModifiedUtc = info.LastWriteTimeUtc;
        _squared++;
    }

    private void Step()
    {
        _progress++;
        if (_progress % 10 == 0 || _progress == _total) notifier.Notify();
    }
}
