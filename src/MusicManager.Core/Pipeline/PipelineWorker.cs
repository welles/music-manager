using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MusicManager.Core.Data;

namespace MusicManager.Core.Pipeline;

/// <summary>Processes the work queue sequentially; re-queues unfinished work on startup.</summary>
public sealed class PipelineWorker(
    WorkQueue queue,
    MatchingPipeline pipeline,
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<PipelineWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeuePendingAsync(stoppingToken);

        await foreach (var item in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                switch (item.Kind)
                {
                    case WorkKind.ProcessGroup:
                        await pipeline.ProcessGroupAsync(item.Id, stoppingToken);
                        break;
                    case WorkKind.WriteFile:
                        await pipeline.WriteFileAsync(item.Id, stoppingToken);
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Work item {Item} failed", item);
            }
        }
    }

    private async Task RequeuePendingAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var groups = await db.Files.Where(f => f.Status == FileStatus.Queued).Select(f => f.GroupId).Distinct().ToListAsync(ct);
        foreach (var g in groups)
            queue.Enqueue(new WorkItem(WorkKind.ProcessGroup, g));

        var approved = await db.Files.Where(f => f.Status == FileStatus.Approved).Select(f => f.Id).ToListAsync(ct);
        foreach (var id in approved)
            queue.Enqueue(new WorkItem(WorkKind.WriteFile, id));
    }
}

/// <summary>Deletes trash folders older than the retention period once a day.</summary>
public sealed class TrashCleanupService(Storage storage, SettingsService settings, ILogger<TrashCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var retention = (await settings.GetAsync(stoppingToken)).TrashRetentionDays;
                if (retention > 0 && Directory.Exists(storage.TrashRoot))
                {
                    foreach (var dir in Directory.GetDirectories(storage.TrashRoot))
                    {
                        if (DateTime.TryParseExact(Path.GetFileName(dir), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var date)
                            && date < DateTime.Today.AddDays(-retention))
                        {
                            logger.LogInformation("Deleting trash folder {Dir}", dir);
                            Directory.Delete(dir, true);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Trash cleanup failed");
            }

            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }
}
