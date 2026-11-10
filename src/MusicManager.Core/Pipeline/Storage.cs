using Microsoft.Extensions.Options;
using MusicManager.Core.Configuration;

namespace MusicManager.Core.Pipeline;

/// <summary>Paths and file operations on the inbox, library and trash volumes.</summary>
public sealed class Storage(IOptions<MusicManagerOptions> options)
{
    private readonly MusicManagerOptions _o = options.Value;

    public string InboxRoot => Path.GetFullPath(_o.InboxPath);
    public string LibraryRoot => Path.GetFullPath(_o.LibraryPath);
    public string TrashRoot => Path.GetFullPath(_o.TrashPath);
    public string WorkRoot => Path.GetFullPath(_o.WorkPath);

    public string InboxPath(string relative) => SafeCombine(InboxRoot, relative);
    public string LibraryPath(string relative) => SafeCombine(LibraryRoot, relative);

    public string RelativeToInbox(string absolute) => Path.GetRelativePath(InboxRoot, absolute);

    /// <summary>Moves a file into the trash (dated folder, original relative path) instead of deleting it.</summary>
    public string MoveToTrash(string absolutePath, string relativeName)
    {
        var target = SafeCombine(TrashRoot, Path.Combine(DateTime.Now.ToString("yyyy-MM-dd"), relativeName));
        target = UniquePath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(absolutePath, target);
        return target;
    }

    /// <summary>Moves a file, preserving its modification time (also across volumes).</summary>
    public static void MovePreservingTime(string source, string target, bool overwrite = false)
    {
        var mtime = File.GetLastWriteTimeUtc(source);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(source, target, overwrite);
        File.SetLastWriteTimeUtc(target, mtime);
    }

    public static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>Deletes empty directories from <paramref name="directory"/> upwards, stopping at <paramref name="stopAt"/>.</summary>
    public static void DeleteEmptyDirectories(string directory, string stopAt)
    {
        var stop = Path.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar);
        var dir = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        while (dir.Length > stop.Length && dir.StartsWith(stop, StringComparison.Ordinal) && Directory.Exists(dir))
        {
            // Remove empty subdirectories first.
            foreach (var sub in Directory.GetDirectories(dir))
                DeleteEmptyDirectories(sub, dir);

            if (Directory.EnumerateFileSystemEntries(dir).Any()) break;
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir)!;
        }
    }

    private static string SafeCombine(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidOperationException($"Path {relative} leaves {root}");
        return full;
    }
}
