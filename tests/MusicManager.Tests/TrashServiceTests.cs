using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicManager.Core.Configuration;
using MusicManager.Core.Pipeline;

namespace MusicManager.Tests;

/// <summary>Listing and emptying the trash from the web frontend.</summary>
public sealed class TrashServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mm-trash-" + Guid.NewGuid().ToString("N"));
    private readonly string _trash;
    private readonly string _outside;
    private readonly Storage _storage;
    private readonly TrashService _service;

    public TrashServiceTests()
    {
        _trash = Path.Combine(_root, "trash");
        _outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(_trash);
        Directory.CreateDirectory(_outside);
        _storage = new Storage(Options.Create(new MusicManagerOptions { TrashPath = _trash }));
        _service = new TrashService(_storage, NullLogger<TrashService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string Write(string relative, int bytes)
    {
        var path = Path.Combine(_trash, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public async Task ListsDaysNewestFirstWithSizesPerEntryDayAndTotal()
    {
        Write("2026-11-13/100 gecs - 1000 gecs/01.flac", 300);
        Write("2026-11-13/100 gecs - 1000 gecs/02.flac", 200);
        Write("2026-11-13/#UNSORTED (018)/a.mp3", 50);
        Write("2026-11-21/musicfab/x/y.m4a", 10);
        Write("zugeschnitten-alt/cover.jpg", 7);
        Write("loose.txt", 3);

        var c = await _service.ListAsync();

        Assert.Equal(["2026-11-21", "2026-11-13", "zugeschnitten-alt", null], c.Folders.Select(f => f.Name));
        var day = c.Folders[1];
        Assert.Equal(new DateOnly(2026, 11, 13), day.Date);
        Assert.Equal(550, day.Size);
        Assert.Equal(3, day.Files);
        Assert.Equal(["100 gecs - 1000 gecs", "#UNSORTED (018)"], day.Entries.Select(e => e.Name));
        Assert.Equal([500L, 50L], day.Entries.Select(e => e.Size));
        Assert.Equal(570, c.Size);
        Assert.Equal(6, c.Files);
    }

    [Fact]
    public async Task EmptyDeletesEverythingButKeepsTheTrashFolder()
    {
        Write("2026-11-13/album/01.flac", 300);
        Write("2026-11-21/musicfab/x/y.m4a", 10);
        Write("loose.txt", 3);

        var r = await _service.EmptyAsync();

        Assert.Equal(313, r.Size);
        Assert.Equal(3, r.Files);
        Assert.Empty(r.Errors);
        Assert.True(Directory.Exists(_trash));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_trash));
        Assert.Empty((await _service.ListAsync()).Folders);
    }

    [Fact]
    public async Task DeleteFolderOnlyTakesANameFoundInTheTrash()
    {
        Write("2026-11-13/album/01.flac", 300);
        Write("2026-11-21/album/01.flac", 10);
        var victim = Path.Combine(_outside, "keep.flac");
        File.WriteAllBytes(victim, new byte[5]);

        foreach (var name in new[] { "../outside", "..", "/", _outside, "2026-11-13/album", "" })
        {
            var r = await _service.DeleteFolderAsync(name);
            Assert.Equal(0, r.Files);
        }
        Assert.True(File.Exists(victim));
        Assert.Equal(2, (await _service.ListAsync()).Folders.Count);

        var deleted = await _service.DeleteFolderAsync("2026-11-13");
        Assert.Equal(300, deleted.Size);
        Assert.Equal(["2026-11-21"], (await _service.ListAsync()).Folders.Select(f => f.Name));
    }

    [Fact]
    public async Task SymlinksAreRemovedWithoutTouchingTheirTarget()
    {
        var target = Path.Combine(_outside, "music");
        Directory.CreateDirectory(target);
        var song = Path.Combine(target, "song.m4a");
        File.WriteAllBytes(song, new byte[1000]);
        Directory.CreateDirectory(Path.Combine(_trash, "2026-11-13"));
        Directory.CreateSymbolicLink(Path.Combine(_trash, "2026-11-13", "link"), target);
        Directory.CreateSymbolicLink(Path.Combine(_trash, "top-link"), target);

        var c = await _service.ListAsync();
        Assert.Equal(0, c.Size);

        await _service.EmptyAsync();

        Assert.True(File.Exists(song));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_trash));
    }

    [Fact]
    public void EntryThatVanishedBeforeDeletingIsSkipped()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Write("2026-11-13/album/01.flac", 300))!);
        var file = new FileInfo(Write("2026-11-13/loose.flac", 10));
        Directory.Delete(Path.Combine(_trash, "2026-11-13"), true);

        Assert.Equal(0, TrashService.Measure(dir).Size);
        Assert.Equal(0, TrashService.Measure(file).Size);
        TrashService.DeleteEntry(dir);
        TrashService.DeleteEntry(file);
    }

    [Fact]
    public async Task MissingTrashFolderIsEmpty()
    {
        Directory.Delete(_trash, true);
        Assert.Empty((await _service.ListAsync()).Folders);
        Assert.Equal(0, (await _service.EmptyAsync()).Files);
    }
}
