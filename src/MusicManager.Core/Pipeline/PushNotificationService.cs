using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MusicManager.Core.Data;
using MusicManager.Core.Services;

namespace MusicManager.Core.Pipeline;

/// <summary>
/// Tells the subscribed devices when something waits for the user (review, not in MusicBrainz, duplicates,
/// errors). One summary per finished batch: it waits until the queue is idle and only notifies when the
/// number of open items has grown since the last notification.
/// </summary>
public sealed class PushNotificationService(
    IDbContextFactory<AppDbContext> dbFactory,
    WebPushSender sender,
    ILogger<PushNotificationService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private static readonly FileStatus[] Busy = [FileStatus.Waiting, FileStatus.Queued, FileStatus.Processing, FileStatus.Approved];

    private int? _notified;

    public sealed record Todo(int Review, int NotInMusicBrainz, int Duplicates, int Errors)
    {
        public int Total => Review + NotInMusicBrainz + Duplicates + Errors;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Push notification check failed");
            }

            await Task.Delay(Interval, ct);
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Files.AnyAsync(f => Busy.Contains(f.Status), ct)) return;

        var todo = await CountAsync(db, ct);
        // Items that were already open when the app started were announced before (or are known).
        if (_notified == null || todo.Total <= _notified)
        {
            _notified = todo.Total;
            return;
        }

        _notified = todo.Total;
        await SendAsync(db, Message(todo), ct);
    }

    public static async Task<Todo> CountAsync(AppDbContext db, CancellationToken ct)
    {
        var counts = await db.Files
            .Where(f => f.Status == FileStatus.NeedsReview || f.Status == FileStatus.NotInMusicBrainz ||
                        f.Status == FileStatus.Duplicate || f.Status == FileStatus.Error)
            .GroupBy(f => f.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return new Todo(counts.GetValueOrDefault(FileStatus.NeedsReview), counts.GetValueOrDefault(FileStatus.NotInMusicBrainz),
            counts.GetValueOrDefault(FileStatus.Duplicate), counts.GetValueOrDefault(FileStatus.Error));
    }

    public static object Message(Todo todo)
    {
        var parts = new List<string>();
        if (todo.Review > 0) parts.Add(todo.Review == 1 ? "1 Lied zu prüfen" : $"{todo.Review} Lieder zu prüfen");
        if (todo.NotInMusicBrainz > 0) parts.Add($"{todo.NotInMusicBrainz} nicht in MusicBrainz");
        if (todo.Duplicates > 0) parts.Add(todo.Duplicates == 1 ? "1 mögliches Duplikat" : $"{todo.Duplicates} mögliche Duplikate");
        if (todo.Errors > 0) parts.Add(todo.Errors == 1 ? "1 Fehler" : $"{todo.Errors} Fehler");

        var onlyDuplicates = todo.Duplicates > 0 && todo.Total == todo.Duplicates;
        return new
        {
            title = "Music Manager: es gibt etwas zu tun",
            body = string.Join(" · ", parts),
            url = onlyDuplicates ? "/duplikate" : "/pruefung",
            tag = "todo",
        };
    }

    /// <summary>Sends to every subscribed device and forgets subscriptions the push service no longer knows.</summary>
    public async Task<int> SendAsync(AppDbContext db, object payload, CancellationToken ct)
    {
        var sent = 0;
        foreach (var sub in await db.PushSubscriptions.ToListAsync(ct))
        {
            switch (await sender.SendAsync(sub, payload, ct))
            {
                case WebPushSender.Result.Sent:
                    sub.LastSentAt = DateTimeOffset.UtcNow;
                    sent++;
                    break;
                case WebPushSender.Result.Gone:
                    logger.LogInformation("Push subscription {Id} expired, removing it", sub.Id);
                    db.PushSubscriptions.Remove(sub);
                    break;
            }
        }

        await db.SaveChangesAsync(ct);
        return sent;
    }
}
