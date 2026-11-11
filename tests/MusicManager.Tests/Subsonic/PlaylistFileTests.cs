using Microsoft.EntityFrameworkCore;
using MusicManager.Core.Catalog;
using MusicManager.Core.Data;
using MusicManager.Core.Model;
using MusicManager.Core.Pipeline;

namespace MusicManager.Tests.Subsonic;

/// <summary>Playlists are kept as M3U8 files in the library and taken over from there.</summary>
public class PlaylistFileTests
{
    private static TagSet Tags(string title) => new()
    {
        Title = title, Artist = "A", Artists = ["A"], Album = "B", AlbumArtist = "A", AlbumArtists = ["A"],
    };

    private static async Task<(SubsonicTestHost App, string One, string Two)> SetupAsync()
    {
        var app = new SubsonicTestHost();
        app.AddFile("A/B/1.m4a", Tags("One"));
        app.AddFile("A/B/2.m4a", Tags("Two"));
        await app.ScanAsync();
        return (app, (await app.EntryAsync("A/B/1.m4a"))!.SongId!, (await app.EntryAsync("A/B/2.m4a"))!.SongId!);
    }

    private static string FilePath(SubsonicTestHost app, string name) => Path.Combine(app.LibraryRoot, PlaylistFile.Folder, name);

    private static Task<PlaylistFileSync.Result> SyncAsync(SubsonicTestHost app) => app.Get<PlaylistFileSync>().SyncAsync(CancellationToken.None);

    [Fact]
    public async Task PlaylistIsWrittenRenamedAndDeleted()
    {
        var (app, one, two) = await SetupAsync();
        using var _ = app;
        var store = app.Get<PlaylistStore>();
        var playlist = await store.CreateAsync("AC/DC Mix", [two, one, "tr-gone"]);
        Assert.Equal(new PlaylistFileSync.Result(0, 1), await SyncAsync(app));

        var text = await File.ReadAllTextAsync(FilePath(app, "AC∕DC Mix.m3u8"));
        Assert.StartsWith("#EXTM3U\n#PLAYLIST:AC/DC Mix\n", text);
        Assert.Contains($"#MUSICMANAGER-ID:pl-{playlist.Id}", text);
        Assert.True(text.IndexOf("../A/B/2.m4a", StringComparison.Ordinal) < text.IndexOf("../A/B/1.m4a", StringComparison.Ordinal));
        Assert.Contains("#MUSICMANAGER-SONG:tr-gone", text);
        Assert.Equal(new PlaylistFileSync.Result(0, 0), await SyncAsync(app));

        await store.UpdateAsync(playlist.Id, new PlaylistChanges(Name: "Mix"), _ => true);
        await SyncAsync(app);
        Assert.False(File.Exists(FilePath(app, "AC∕DC Mix.m3u8")));
        Assert.True(File.Exists(FilePath(app, "Mix.m3u8")));

        await store.DeleteAsync(playlist.Id);
        Assert.False(File.Exists(FilePath(app, "Mix.m3u8")));
        Assert.Equal(new PlaylistFileSync.Result(0, 0), await SyncAsync(app));
    }

    [Fact]
    public async Task LostDatabaseIsRebuiltFromTheFiles()
    {
        var (app, one, two) = await SetupAsync();
        using var _ = app;
        var store = app.Get<PlaylistStore>();
        var created = await store.CreateAsync("AC/DC Mix", [one, "tr-gone", two]);
        await store.UpdateAsync(created.Id, new PlaylistChanges(Comment: "für unterwegs", Public: true), _ => true);
        await SyncAsync(app);

        await using (var db = await app.Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
            await db.Playlists.ExecuteDeleteAsync();

        Assert.Equal(new PlaylistFileSync.Result(1, 0), await SyncAsync(app));
        var restored = Assert.Single(await store.ListAsync());
        Assert.Equal(created.Id, restored.Id);
        Assert.Equal("AC/DC Mix", restored.Name);
        Assert.Equal("für unterwegs", restored.Comment);
        Assert.True(restored.Public);
        Assert.Equal([one, "tr-gone", two], restored.Entries.Select(e => e.SongId));
    }

    [Fact]
    public async Task FileEditedOutsideIsTakenOver()
    {
        var (app, one, two) = await SetupAsync();
        using var _ = app;
        var store = app.Get<PlaylistStore>();
        var playlist = await store.CreateAsync("Mix", [one]);
        await SyncAsync(app);

        // Another tool writes a plain M3U: new order, an absolute path, an unknown file.
        var path = FilePath(app, "Mix.m3u8");
        await File.WriteAllTextAsync(path, $"#EXTM3U\n../A/B/2.m4a\n{Path.Combine(app.LibraryRoot, "A/B/1.m4a")}\n../X/missing.m4a\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        Assert.Equal(new PlaylistFileSync.Result(1, 1), await SyncAsync(app));
        var after = (await store.GetAsync(playlist.Id))!;
        Assert.Equal([two, one], after.Entries.Select(e => e.SongId));
        Assert.Contains("#MUSICMANAGER-SONG:", await File.ReadAllTextAsync(path));

        // A new file in the folder becomes a playlist.
        await File.WriteAllTextAsync(FilePath(app, "Neu.m3u"), "../A/B/1.m4a\n");
        await SyncAsync(app);
        var added = (await store.ListAsync()).Single(p => p.Name == "Neu");
        Assert.Equal([one], added.Entries.Select(e => e.SongId));
    }

    [Fact]
    public async Task MovedSongUpdatesThePath()
    {
        var (app, one, _) = await SetupAsync();
        using var __ = app;
        await app.Get<PlaylistStore>().CreateAsync("Mix", [one]);
        await app.Get<FileMarkerSync>().SyncAsync(CancellationToken.None);
        await SyncAsync(app);

        Directory.CreateDirectory(Path.Combine(app.LibraryRoot, "A/C"));
        File.Move(Path.Combine(app.LibraryRoot, "A/B/1.m4a"), Path.Combine(app.LibraryRoot, "A/C/1.m4a"));
        await app.ScanAsync();
        Assert.Equal(one, (await app.EntryAsync("A/C/1.m4a"))!.SongId);

        Assert.Equal(new PlaylistFileSync.Result(0, 1), await SyncAsync(app));
        Assert.Contains("../A/C/1.m4a", await File.ReadAllTextAsync(FilePath(app, "Mix.m3u8")));
    }
}
