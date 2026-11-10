using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MusicManager.Core.Data;

namespace MusicManager.Core.Pipeline;

/// <summary>
/// Watches the inbox by polling (works on network shares, unlike FileSystemWatcher).
/// Files are grouped by their first-level folder (below the source folder, if any); loose
/// files in the inbox root form one group. A group is queued once nothing in its folder
/// changed for the configured quiet period.
/// </summary>
public sealed class InboxScanner(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settingsService,
    Storage storage,
    WorkQueue queue,
    PipelineNotifier notifier,
    ILogger<InboxScanner> logger) : BackgroundService
{
    private static readonly string[] IncompleteExtensions = [".part", ".crdownload", ".tmp", ".download", ".partial"];
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly SemaphoreSlim _trigger = new(0, 1);

    public DateTimeOffset? LastScan { get; private set; }

    /// <summary>Triggers a scan now; with <paramref name="ignoreQuietPeriod"/> all open groups are queued immediately.</summary>
    public async Task ScanNowAsync(bool ignoreQuietPeriod, CancellationToken ct = default)
    {
        await ScanAsync(ignoreQuietPeriod, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = await settingsService.GetAsync(stoppingToken);
            if (settings.InboxWatchEnabled)
            {
                try
                {
                    await ScanAsync(false, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Inbox scan failed");
                }
            }

            try
            {
                await _trigger.WaitAsync(TimeSpan.FromSeconds(Math.Max(2, settings.InboxScanIntervalSeconds)), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ScanAsync(bool ignoreQuietPeriod, CancellationToken ct)
    {
        await _scanLock.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(storage.InboxRoot))
            {
                logger.LogWarning("Inbox {Path} does not exist", storage.InboxRoot);
                return;
            }

            var settings = await settingsService.GetAsync(ct);
            var profiles = await settingsService.GetSourceProfilesAsync(ct);
            var extensions = settings.AudioExtensions.Select(e => e.ToLowerInvariant()).ToHashSet();

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var activePaths = (await db.Files
                    .Where(f => f.Status != FileStatus.Done && f.Status != FileStatus.Discarded)
                    .Select(f => f.RelativePath).ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
            var openGroups = await db.Groups.Where(g => g.Open).Include(g => g.Files).ToListAsync(ct);

            var changed = false;
            foreach (var path in Directory.EnumerateFiles(storage.InboxRoot, "*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden,
                     }))
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (!extensions.Contains(ext) || IncompleteExtensions.Contains(ext)) continue;

                var relative = storage.RelativeToInbox(path);
                if (activePaths.Contains(relative)) continue;

                var (groupFolder, profile) = GroupFolder(relative, profiles);
                var group = openGroups.FirstOrDefault(g => g.RelativeFolder == groupFolder);
                if (group == null)
                {
                    group = new InboxGroup { RelativeFolder = groupFolder, SourceProfile = profile?.Name };
                    db.Groups.Add(group);
                    openGroups.Add(group);
                }

                var info = new FileInfo(path);
                group.Files.Add(new TrackFile
                {
                    RelativePath = relative,
                    FileSize = info.Length,
                    FileModifiedUtc = info.LastWriteTimeUtc,
                    Events = [new FileEvent { Message = $"Im Eingang gefunden (Gruppe \"{(groupFolder == "" ? "Eingang" : groupFolder)}\")" }],
                });
                activePaths.Add(relative);
                changed = true;
            }

            // Queue groups whose folder has been quiet long enough.
            var now = DateTime.UtcNow;
            foreach (var group in openGroups)
            {
                var lastChange = LastChangeUtc(group);
                if (!ignoreQuietPeriod && (now - lastChange).TotalSeconds < settings.InboxQuietSeconds) continue;

                group.Open = false;
                foreach (var f in group.Files.Where(f => f.Status == FileStatus.Waiting))
                {
                    f.Status = FileStatus.Queued;
                    f.UpdatedAt = DateTimeOffset.UtcNow;
                }

                changed = true;
                await db.SaveChangesAsync(ct);
                queue.Enqueue(new WorkItem(WorkKind.ProcessGroup, group.Id));
                logger.LogInformation("Group {Folder} queued with {Count} files", group.RelativeFolder, group.Files.Count);
            }

            if (changed)
            {
                await db.SaveChangesAsync(ct);
                notifier.Notify();
            }

            LastScan = DateTimeOffset.UtcNow;
        }
        finally
        {
            _scanLock.Release();
        }
    }

    /// <summary>Newest modification in the group's folder (any file, e.g. a still growing download).</summary>
    private DateTime LastChangeUtc(InboxGroup group)
    {
        var latest = group.Files.Select(f => f.CreatedAt.UtcDateTime).DefaultIfEmpty(DateTime.MinValue).Max();
        if (group.RelativeFolder == "")
        {
            foreach (var f in group.Files)
            {
                var p = storage.InboxPath(f.RelativePath);
                if (File.Exists(p)) latest = Max(latest, File.GetLastWriteTimeUtc(p));
            }

            return latest;
        }

        var dir = storage.InboxPath(group.RelativeFolder);
        if (!Directory.Exists(dir)) return latest;
        foreach (var p in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories))
            latest = Max(latest, File.GetLastWriteTimeUtc(p));
        return latest;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    /// <summary>
    /// "Album/x.mp3" → "Album"; "musicfab/Album/x.mp3" → "musicfab/Album" with profile MusicFab;
    /// "x.mp3" → "" (loose files).
    /// </summary>
    public static (string Folder, SourceProfile? Profile) GroupFolder(string relativePath, IReadOnlyList<SourceProfile> profiles)
    {
        var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length == 1) return ("", null);

        var profile = profiles.FirstOrDefault(p => string.Equals(p.FolderName, parts[0], StringComparison.OrdinalIgnoreCase));
        if (profile == null) return (parts[0], null);

        return parts.Length == 2 ? (parts[0], profile) : (Path.Combine(parts[0], parts[1]), profile);
    }

    public override void Dispose()
    {
        _scanLock.Dispose();
        _trigger.Dispose();
        base.Dispose();
    }
}
