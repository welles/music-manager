using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MusicManager.Core.Data;
using MusicManager.Core.Services;

namespace MusicManager.Core.Pipeline;

/// <summary>State of the run that measures the loudness of library songs.</summary>
/// <param name="Written">Songs that got their ReplayGain tags.</param>
/// <param name="Skipped">Song files changed on disk since the last scan; left for the next run.</param>
/// <param name="Failed">Songs that could not be measured (e.g. silence) or written.</param>
public sealed record ReplayGainState(bool Running, int Progress, int Total, int Written, int Skipped, int Failed);

/// <summary>
/// New songs get their ReplayGain tags when they are filed. This measures, on request, the library songs that
/// have none yet (the tag itself tells whether a song is normalized) and writes the tags; the audio stays
/// untouched. Only M4A files are written, like everything else this tool writes into library files.
/// </summary>
public sealed class ReplayGainService(
    IDbContextFactory<AppDbContext> dbFactory,
    AudioTools audio,
    Storage storage,
    LibraryIndexer indexer,
    PipelineNotifier notifier,
    ILogger<ReplayGainService> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _progress, _total, _written, _skipped, _failed;

    public ReplayGainState State => new(_lock.CurrentCount == 0, _progress, _total, _written, _skipped, _failed);

    /// <summary>Number of library songs without ReplayGain tags that a run would measure.</summary>
    public async Task<int> CountMissingAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await Missing(db).CountAsync(ct);
    }

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
                logger.LogError(ex, "Measuring the loudness of the library failed");
            }
            finally
            {
                _lock.Release();
                notifier.Notify();
            }
        });
        return true;
    }

    private static IQueryable<LibraryEntry> Missing(AppDbContext db) =>
        db.Library.Where(e => e.ReplayGainTrackGain == null && e.RelativePath.EndsWith(".m4a"));

    internal async Task RunAsync(CancellationToken ct)
    {
        _progress = _written = _skipped = _failed = 0;
        List<int> ids;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            ids = await Missing(db).OrderBy(e => e.RelativePath).Select(e => e.Id).ToListAsync(ct);
        _total = ids.Count;
        notifier.Notify();

        try
        {
            // Saved in small chunks, so a stop (restart of the app) loses little.
            foreach (var chunk in ids.Chunk(20))
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var entries = await db.Library.Where(e => chunk.Contains(e.Id)).ToListAsync(ct);
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        await MeasureAsync(entry, ct);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        logger.LogWarning("Could not write ReplayGain into {Path}: {Message}", entry.RelativePath, ex.Message);
                        _failed++;
                    }

                    _progress++;
                    notifier.Notify();
                }

                // An entry the index removed in the meantime (song moved, replaced or deleted) must not end the run.
                await db.SaveSkippingDeletedAsync(ct);
            }
        }
        finally
        {
            // The catalog (Subsonic API, player) reads the values from the index; the next scan finds the files unchanged.
            if (_written > 0) indexer.OnChanged();
        }

        logger.LogInformation("ReplayGain: {Written} written, {Skipped} skipped, {Failed} failed", _written, _skipped, _failed);
    }

    private async Task MeasureAsync(LibraryEntry entry, CancellationToken ct)
    {
        var path = storage.LibraryPath(entry.RelativePath);
        var info = new FileInfo(path);
        // Changed on disk since the last scan: let the scan read it first instead of overwriting it.
        if (!info.Exists || info.Length != entry.FileSize || info.LastWriteTimeUtc != entry.FileModifiedUtc)
        {
            _skipped++;
            return;
        }

        var replayGain = await audio.MeasureReplayGainAsync(path, ct);
        if (replayGain == null)
        {
            logger.LogWarning("Loudness of {Path} cannot be measured", entry.RelativePath);
            _failed++;
            return;
        }

        // Replaced or changed while it was measured (e.g. a new version filed under the same path): the values
        // belong to the old audio.
        info.Refresh();
        if (!info.Exists || info.Length != entry.FileSize || info.LastWriteTimeUtc != entry.FileModifiedUtc)
        {
            _skipped++;
            return;
        }

        TagIo.WriteReplayGain(path, replayGain);
        info.Refresh();
        entry.ReplayGainTrackGain = replayGain.TrackGain;
        entry.ReplayGainTrackPeak = replayGain.TrackPeak;
        // The index stays "unchanged" for this file, so the next scan does not read it again.
        entry.FileSize = info.Length;
        entry.FileModifiedUtc = info.LastWriteTimeUtc;
        _written++;
    }
}
