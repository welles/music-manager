using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MusicManager.Core.Data;
using MusicManager.Core.Pipeline;

namespace MusicManager.Core.Catalog;

/// <summary>
/// Songs marked for a later check (issue #18). In the car there is no button for it, so a song that is starred
/// and unstarred again within <see cref="Configuration.AppSettings.ReportUnstarSeconds"/> counts as a report. The
/// reports are listed on the "Meldungen" page until they are marked as done.
/// </summary>
public sealed class SongReportService(IDbContextFactory<AppDbContext> dbFactory, SettingsService settings, PipelineNotifier notifier,
    ILogger<SongReportService> logger)
{
    /// <summary>One write at a time, so two reports of the same song do not open two rows.</summary>
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Reports the songs among the just removed stars that were set no longer than the configured window ago.
    /// Stars of albums and artists are ignored. Returns the reported song IDs.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReportQuickUnstarsAsync(IEnumerable<Star> removed, DateTimeOffset now, CancellationToken ct = default)
    {
        var seconds = (await settings.GetAsync(ct)).ReportUnstarSeconds;
        if (seconds <= 0) return [];
        var since = now - TimeSpan.FromSeconds(seconds);
        var songs = removed.Where(s => CatalogIds.IsSong(s.ItemId) && s.StarredAt >= since)
            .Select(s => s.ItemId).Distinct(StringComparer.Ordinal).ToList();
        if (songs.Count == 0) return [];
        await ReportAsync(songs, now, ct);
        logger.LogInformation("Song reported by a quick unstar: {Songs}", string.Join(", ", songs));
        return songs;
    }

    /// <summary>Opens a report per song, or counts up the open one.</summary>
    public async Task ReportAsync(IReadOnlyCollection<string> songIds, DateTimeOffset now, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var ids = songIds.Distinct(StringComparer.Ordinal).ToList();
            var open = await db.SongReports.Where(r => ids.Contains(r.SongId) && r.ResolvedAt == null).ToListAsync(ct);
            var songs = await db.Library.AsNoTracking().Where(e => e.SongId != null && ids.Contains(e.SongId))
                .Select(e => new { e.SongId, e.Title, e.Artist }).ToListAsync(ct);
            foreach (var id in ids)
            {
                if (open.FirstOrDefault(r => r.SongId == id) is { } row)
                {
                    row.Count++;
                    row.LastReportedAt = now;
                    continue;
                }

                var song = songs.FirstOrDefault(s => s.SongId == id);
                db.SongReports.Add(new SongReport { SongId = id, Title = song?.Title, Artist = song?.Artist, ReportedAt = now, LastReportedAt = now });
            }

            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _lock.Release();
        }

        notifier.Notify();
    }

    /// <summary>Open reports (most recent first), then the last <paramref name="resolvedCount"/> done ones.</summary>
    public async Task<List<SongReport>> GetAsync(int resolvedCount = 100, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var open = await db.SongReports.AsNoTracking().Where(r => r.ResolvedAt == null).OrderByDescending(r => r.LastReportedAt).ToListAsync(ct);
        var done = await db.SongReports.AsNoTracking().Where(r => r.ResolvedAt != null).OrderByDescending(r => r.ResolvedAt)
            .Take(resolvedCount).ToListAsync(ct);
        return [.. open, .. done];
    }

    public async Task<int> CountOpenAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SongReports.CountAsync(r => r.ResolvedAt == null, ct);
    }

    /// <summary>Marks the report as done, or opens it again (when the song has no other open report).</summary>
    public Task SetResolvedAsync(int id, bool resolved, CancellationToken ct = default) => UpdateAsync(id, async (db, r) =>
    {
        if (resolved)
        {
            r.ResolvedAt ??= DateTimeOffset.UtcNow;
            return;
        }

        // A newer report of the same song is already open: keep that one.
        if (await db.SongReports.AnyAsync(o => o.SongId == r.SongId && o.ResolvedAt == null && o.Id != r.Id, ct)) return;
        r.ResolvedAt = null;
    }, ct);

    public Task SetNoteAsync(int id, string? note, CancellationToken ct = default) => UpdateAsync(id, (_, r) =>
    {
        r.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        return Task.CompletedTask;
    }, ct);

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            await db.SongReports.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
        }

        notifier.Notify();
    }

    private async Task UpdateAsync(int id, Func<AppDbContext, SongReport, Task> change, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = await db.SongReports.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (row == null) return;
            await change(db, row);
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _lock.Release();
        }

        notifier.Notify();
    }
}
