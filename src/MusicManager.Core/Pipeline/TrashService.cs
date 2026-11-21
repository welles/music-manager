using Microsoft.Extensions.Logging;

namespace MusicManager.Core.Pipeline;

/// <summary>One entry of a trash folder (an album folder, a source folder such as <c>musicfab</c>, a single file).</summary>
public sealed record TrashEntry(string Name, bool IsDirectory, long Size, int Files, DateTime Modified);

/// <summary>A first-level folder of the trash, normally the day the files were moved there (<c>yyyy-MM-dd</c>).</summary>
/// <param name="Name">Folder name; <see langword="null"/> for files lying directly in the trash.</param>
public sealed record TrashFolder(string? Name, DateOnly? Date, long Size, int Files, IReadOnlyList<TrashEntry> Entries);

/// <summary>What lies in the trash, newest day first.</summary>
public sealed record TrashContents(IReadOnlyList<TrashFolder> Folders, long Size, int Files);

/// <summary>Outcome of emptying (part of) the trash.</summary>
public sealed record TrashDeleteResult(long Size, int Files, IReadOnlyList<string> Errors);

/// <summary>
/// Lists and empties the trash (<see cref="Storage.TrashRoot"/>). Deleting only ever touches entries found by
/// enumerating the trash itself, never a path from the caller, and does not follow symlinks out of the trash.
/// </summary>
public sealed class TrashService(Storage storage, ILogger<TrashService> logger)
{
    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        // Count hidden files too, but never follow a symlink out of the trash.
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private readonly SemaphoreSlim _deleting = new(1, 1);

    /// <summary>Scans the trash with the size of every entry. Walks the whole tree, so it runs on the thread pool.</summary>
    public Task<TrashContents> ListAsync(CancellationToken ct = default) => Task.Run(() => List(ct), ct);

    private TrashContents List(CancellationToken ct)
    {
        var root = storage.TrashRoot;
        if (!Directory.Exists(root)) return new TrashContents([], 0, 0);

        var folders = new List<TrashFolder>();
        var loose = new List<TrashEntry>();
        foreach (var info in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            if (info is DirectoryInfo dir && !IsLink(dir))
            {
                List<TrashEntry> entries;
                try
                {
                    entries = dir.EnumerateFileSystemInfos().Select(Measure).OrderByDescending(e => e.Size).ThenBy(e => e.Name).ToList();
                }
                catch (DirectoryNotFoundException)
                {
                    // Deleted in the meantime (e.g. by the daily cleanup).
                    continue;
                }
                folders.Add(new TrashFolder(dir.Name, ParseDay(dir.Name), entries.Sum(e => e.Size), entries.Sum(e => e.Files), entries));
            }
            else
            {
                loose.Add(Measure(info));
            }
        }

        // Days newest first, other folders after them by name, loose files last.
        var ordered = folders.OrderByDescending(f => f.Date.HasValue).ThenByDescending(f => f.Date).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
        if (loose.Count > 0)
            ordered.Add(new TrashFolder(null, null, loose.Sum(e => e.Size), loose.Sum(e => e.Files), loose.OrderByDescending(e => e.Size).ToList()));
        return new TrashContents(ordered, ordered.Sum(f => f.Size), ordered.Sum(f => f.Files));
    }

    /// <summary>
    /// Size and file count of an entry. One that vanishes while it is measured (the daily cleanup, a second tab
    /// emptying the trash) counts as empty instead of stopping the listing or the deletion.
    /// </summary>
    internal static TrashEntry Measure(FileSystemInfo info)
    {
        try
        {
            return MeasureUnsafe(info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TrashEntry(info.Name, info is DirectoryInfo, 0, 0, default);
        }
    }

    private static TrashEntry MeasureUnsafe(FileSystemInfo info)
    {
        if (info is FileInfo file) return new TrashEntry(file.Name, false, IsLink(file) ? 0 : file.Length, 1, file.LastWriteTime);
        var dir = (DirectoryInfo)info;
        if (IsLink(dir)) return new TrashEntry(dir.Name, false, 0, 1, dir.LastWriteTime);
        long size = 0;
        var files = 0;
        foreach (var f in dir.EnumerateFiles("*", Recursive))
        {
            size += f.Length;
            files++;
        }
        return new TrashEntry(dir.Name, true, size, files, dir.LastWriteTime);
    }

    /// <summary>Deletes everything in the trash. The trash folder itself stays (it is usually a mounted volume).</summary>
    public Task<TrashDeleteResult> EmptyAsync(CancellationToken ct = default) =>
        DeleteAsync(_ => true, "Trash emptied", ct);

    /// <summary>
    /// Deletes one first-level folder of the trash (a day). <paramref name="name"/> is only compared with the names
    /// found in the trash; nothing happens if it matches none.
    /// </summary>
    public Task<TrashDeleteResult> DeleteFolderAsync(string name, CancellationToken ct = default) =>
        DeleteAsync(info => info is DirectoryInfo && string.Equals(info.Name, name, StringComparison.Ordinal), $"Trash folder {name} deleted", ct);

    private async Task<TrashDeleteResult> DeleteAsync(Func<FileSystemInfo, bool> select, string message, CancellationToken ct)
    {
        await _deleting.WaitAsync(ct);
        try
        {
            var result = await Task.Run(() => Delete(select, ct), ct);
            logger.LogInformation("{Message}: {Files} files, {Size} bytes, {Errors} errors", message, result.Files, result.Size, result.Errors.Count);
            return result;
        }
        finally
        {
            _deleting.Release();
        }
    }

    private TrashDeleteResult Delete(Func<FileSystemInfo, bool> select, CancellationToken ct)
    {
        var root = storage.TrashRoot;
        if (!Directory.Exists(root)) return new TrashDeleteResult(0, 0, []);

        long size = 0;
        var files = 0;
        var errors = new List<string>();
        foreach (var top in new DirectoryInfo(root).EnumerateFileSystemInfos().Where(select).ToList())
        {
            // A day folder goes entry by entry, so a file moved into the trash at the same time only waits for one entry.
            List<FileSystemInfo> items;
            try
            {
                items = top is DirectoryInfo dir && !IsLink(dir) ? dir.EnumerateFileSystemInfos().ToList() : [top];
            }
            catch (DirectoryNotFoundException)
            {
                // Deleted in the meantime (e.g. by the daily cleanup).
                continue;
            }

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var measured = Measure(item);
                    lock (storage.TrashLock) DeleteEntry(item);
                    size += measured.Size;
                    files += measured.Files;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Could not delete {Path} from the trash", item.FullName);
                    errors.Add($"{Path.GetRelativePath(root, item.FullName)}: {ex.Message}");
                }
            }

            if (top is DirectoryInfo folder && !IsLink(folder))
            {
                try
                {
                    // Only if it is empty now: something may have been moved there in the meantime.
                    lock (storage.TrashLock)
                        if (folder.Exists && !folder.EnumerateFileSystemInfos().Any()) folder.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Could not delete the trash folder {Path}", folder.FullName);
                }
            }
        }
        return new TrashDeleteResult(size, files, errors);
    }

    /// <summary>Deletes a file, a symlink (not its target) or a directory tree.</summary>
    internal static void DeleteEntry(FileSystemInfo item)
    {
        item.Refresh();
        if (!item.Exists && item.LinkTarget == null) return;
        // Directory.Delete(recursive) removes symlinks inside the tree without following them.
        if (item is DirectoryInfo dir && !IsLink(dir)) dir.Delete(true);
        else item.Delete();
    }

    private static bool IsLink(FileSystemInfo info) => info.LinkTarget != null;

    private static DateOnly? ParseDay(string name) =>
        DateOnly.TryParseExact(name, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
}
