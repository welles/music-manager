using MusicManager.Core.Data;
using MusicManager.Core.Model;
using MusicManager.Core.Pipeline;
using MusicManager.Core.Services;

namespace MusicManager.Tests.Subsonic;

/// <summary>Duplicate decision "keep new": the new file is filed at its own path and takes over the old song's identity.</summary>
public class KeepNewTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];

    private static TagSet Tags(string album, string title = "S") =>
        new() { Title = title, Artist = "A", AlbumArtist = "A", Album = album, MbTrackId = Guid.NewGuid().ToString() };

    /// <summary>The converted file as the pipeline hands it over: tagged, outside the library.</summary>
    private static string Written(SubsonicTestHost app, TagSet tags)
    {
        var path = Path.Combine(app.Root, "data", $"{Guid.NewGuid():N}.m4a");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.m4a"), path);
        TagIo.WriteM4a(path, tags, null, "musicbrainz");
        return path;
    }

    private static TrackFile KeepNew(string target, string replaces) => new()
    {
        RelativePath = "inbox/A - S.m4a", TargetPath = target, DuplicateDecision = DuplicateDecision.KeepNew, ReplaceLibraryPath = replaces,
    };

    private static string[] Trash(SubsonicTestHost app, string name) =>
        Directory.GetFiles(Path.Combine(app.Root, "trash"), name, SearchOption.AllDirectories);

    [Fact]
    public async Task NewFileGoesToItsOwnPathAndKeepsTheSongId()
    {
        using var app = new SubsonicTestHost();
        string Lib(params string[] parts) => Path.Combine([app.LibraryRoot, .. parts]);
        var oldPath = Path.Combine("A", "Old", "A - S.m4a");
        var newPath = Path.Combine("A", "New", "A - S.m4a");
        app.AddFile(oldPath, Tags("Old"));
        File.WriteAllBytes(Lib("A", "Old", "cover.jpg"), Jpeg);
        await app.ScanAsync();
        var old = (await app.EntryAsync(oldPath))!;
        var file = KeepNew(newPath, oldPath);

        await app.Get<MatchingPipeline>().PlaceInLibraryAsync(file, Written(app, Tags("New")), null, CancellationToken.None);

        // Filed at its own path, the replaced file is in the trash and its folder is gone.
        Assert.True(File.Exists(Lib(newPath)));
        Assert.False(Directory.Exists(Lib("A", "Old")));
        Assert.Single(Trash(app, "A - S.m4a"));
        Assert.Contains(file.Events, e => e.Message.Contains(oldPath));
        // The folder cover went along with the last song.
        Assert.Equal(Jpeg, File.ReadAllBytes(Lib("A", "New", "cover.jpg")));

        // Same song ID and added date: plays, favorites and playlists stay with the song.
        Assert.Null(await app.EntryAsync(oldPath));
        var entry = (await app.EntryAsync(newPath))!;
        Assert.Equal(old.SongId, entry.SongId);
        Assert.Equal(old.AddedAt, entry.AddedAt);
        var r = await app.CallAsync("getSong", $"id={old.SongId}");
        Assert.Equal("New", r.GetProperty("song").GetProperty("album").GetString());
    }

    [Fact]
    public async Task TheCoverStaysWhileOtherSongsAreLeft()
    {
        using var app = new SubsonicTestHost();
        string Lib(params string[] parts) => Path.Combine([app.LibraryRoot, .. parts]);
        var oldPath = Path.Combine("A", "Old", "A - S.m4a");
        app.AddFile(oldPath, Tags("Old"));
        app.AddFile(Path.Combine("A", "Old", "A - T.m4a"), Tags("Old", "T"));
        File.WriteAllBytes(Lib("A", "Old", "cover.jpg"), Jpeg);
        await app.ScanAsync();

        await app.Get<MatchingPipeline>().PlaceInLibraryAsync(KeepNew(Path.Combine("A", "New", "A - S.m4a"), oldPath),
            Written(app, Tags("New")), null, CancellationToken.None);

        Assert.Equal(["A - T.m4a", "cover.jpg"], Directory.GetFiles(Lib("A", "Old")).Select(Path.GetFileName).Order());
        Assert.Equal(["A - S.m4a"], Directory.GetFiles(Lib("A", "New")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ReplacingAtTheSamePathKeepsFolderAndId()
    {
        using var app = new SubsonicTestHost();
        string Lib(params string[] parts) => Path.Combine([app.LibraryRoot, .. parts]);
        var path = Path.Combine("A", "Same", "A - S.m4a");
        app.AddFile(path, Tags("Same"));
        File.WriteAllBytes(Lib("A", "Same", "cover.jpg"), Jpeg);
        await app.ScanAsync();
        var old = (await app.EntryAsync(path))!;

        await app.Get<MatchingPipeline>().PlaceInLibraryAsync(KeepNew(path, path), Written(app, Tags("Same", "S2")), null, CancellationToken.None);

        Assert.Equal(["A - S.m4a", "cover.jpg"], Directory.GetFiles(Lib("A", "Same")).Select(Path.GetFileName).Order());
        Assert.Equal("S2", TagIo.Read(Lib(path)).Title);
        Assert.Single(Trash(app, "A - S.m4a"));
        Assert.Equal(old.SongId, (await app.EntryAsync(path))!.SongId);
    }

    [Fact]
    public async Task WithoutKeepNewNothingIsReplaced()
    {
        using var app = new SubsonicTestHost();
        var oldPath = Path.Combine("A", "Old", "A - S.m4a");
        var newPath = Path.Combine("A", "Old", "A - S (2).m4a");
        app.AddFile(oldPath, Tags("Old"));
        await app.ScanAsync();
        var old = (await app.EntryAsync(oldPath))!;
        var file = new TrackFile { RelativePath = "inbox/x.m4a", TargetPath = newPath, DuplicateDecision = DuplicateDecision.KeepBoth, ReplaceLibraryPath = oldPath };

        await app.Get<MatchingPipeline>().PlaceInLibraryAsync(file, Written(app, Tags("Old")), null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(app.LibraryRoot, oldPath)));
        Assert.Empty(Trash(app, "*.m4a"));
        Assert.Equal(old.SongId, (await app.EntryAsync(oldPath))!.SongId);
        Assert.NotEqual(old.SongId, (await app.EntryAsync(newPath))!.SongId);
    }
}
