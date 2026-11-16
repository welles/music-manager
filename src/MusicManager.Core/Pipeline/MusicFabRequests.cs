using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MusicManager.Core.Data;
using MusicManager.Core.Services;

namespace MusicManager.Core.Pipeline;

/// <summary>
/// Requests downloads from MusicFab (<see cref="MusicFabClient"/>) and keeps their state in step: every
/// <see cref="ActiveInterval"/> while one is running, otherwise only when asked. The files themselves need nothing from
/// here: MusicFab writes them into the inbox, where the scanner picks them up.
/// </summary>
public sealed class MusicFabRequests(
    IDbContextFactory<AppDbContext> dbFactory,
    MusicFabClient musicFab,
    PipelineNotifier notifier,
    ILogger<MusicFabRequests> logger) : BackgroundService
{
    public static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _signal = new(0, 1);

    public bool IsConfigured => musicFab.IsConfigured;

    /// <summary>Asks MusicFab for <paramref name="url"/> and remembers the job.</summary>
    /// <exception cref="MusicFabException">MusicFab refused it or is not reachable; the message is for the user.</exception>
    public async Task<MusicFabRequest> RequestAsync(string url, string? origin, CancellationToken ct)
    {
        var job = await musicFab.RequestAsync(url.Trim(), ct);
        var request = new MusicFabRequest { JobId = job.Id, Url = url.Trim(), Origin = origin, State = job.State };
        Apply(request, job);
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.MusicFabRequests.Add(request);
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation("Requested {Url} from MusicFab (job {JobId})", request.Url, job.Id);
        notifier.Notify();
        Wake();
        return request;
    }

    /// <summary>Cancels a request MusicFab has not started on; false when it already started.</summary>
    public async Task<bool> CancelAsync(int id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.MusicFabRequests.FindAsync([id], ct) is not { } request || request.IsFinished) return false;
        if (!await musicFab.CancelAsync(request.JobId, ct)) return false;
        request.State = "Cancelled";
        request.FinishedAt = request.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        notifier.Notify();
        return true;
    }

    /// <summary>Forgets a finished request (the files stay where they are).</summary>
    public async Task RemoveAsync(int id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.MusicFabRequests.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
        notifier.Notify();
    }

    /// <summary>Updates the running requests soon (a new request, the page's refresh button).</summary>
    public void Wake()
    {
        if (_signal.CurrentCount == 0) _signal.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var active = false;
            try
            {
                active = await RefreshAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Updating the MusicFab requests failed: {Message}", ex.Message);
                active = true;
            }

            await _signal.WaitAsync(active ? ActiveInterval : Timeout.InfiniteTimeSpan, ct);
        }
    }

    /// <summary>Fetches the state of every unfinished request; true while some are still running.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct)
    {
        if (!musicFab.IsConfigured) return false;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var open = (await db.MusicFabRequests.ToListAsync(ct)).Where(r => !r.IsFinished).ToList();
        foreach (var request in open)
        {
            var job = await musicFab.GetAsync(request.JobId, ct);
            if (job == null)
            {
                // MusicFab.Manager lost its database, or the job was removed there
                request.State = "Lost";
                request.Error = "MusicFab kennt den Auftrag nicht mehr.";
                request.FinishedAt = request.UpdatedAt = DateTimeOffset.UtcNow;
                continue;
            }

            Apply(request, job);
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
            notifier.Notify();
        }

        return open.Any(r => !r.IsFinished);
    }

    private static void Apply(MusicFabRequest request, MusicFabJob job)
    {
        if (request.State == job.State && request.Files == job.Files.Count && request.Progress == job.Progress &&
            request.Title == job.Title && request.Error == job.Error) return;
        request.State = job.State;
        request.Title = job.Title ?? request.Title;
        request.Tracks = job.Tracks ?? request.Tracks;
        request.Files = job.Files.Count;
        request.Progress = job.Progress;
        request.Error = job.Error;
        request.InboxFolder = InboxFolder(job.Files) ?? request.InboxFolder;
        request.UpdatedAt = DateTimeOffset.UtcNow;
        if (job.IsFinished) request.FinishedAt ??= DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// The inbox group of MusicFab's files: <c>/output/musicfab/Album/x.m4a</c> → <c>musicfab/Album</c> (MusicFab's
    /// <c>/output</c> is this inbox), as <see cref="InboxScanner.GroupFolder"/> groups them.
    /// </summary>
    public static string? InboxFolder(IEnumerable<string> files) =>
        files
            .Select(f => f.Replace('\\', '/'))
            .Select(f => f.IndexOf("/musicfab/", StringComparison.OrdinalIgnoreCase) is var i and >= 0 ? f[(i + 1)..] : null)
            .OfType<string>()
            .Select(f => f.Split('/'))
            .Select(parts => parts.Length > 2 ? $"{parts[0]}/{parts[1]}" : parts[0])
            .GroupBy(f => f)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();
}
