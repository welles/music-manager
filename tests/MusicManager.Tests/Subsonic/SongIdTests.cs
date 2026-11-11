using MusicManager.Core.Catalog;
using MusicManager.Core.Data;
using MusicManager.Core.Model;
using MusicManager.Core.Pipeline;

namespace MusicManager.Tests.Subsonic;

public class SongIdTests
{
    private static TagSet Tags(string title, string? trackId = null, string? recordingId = null, string? releaseId = null) => new()
    {
        Title = title, Artist = "A", Artists = ["A"], Album = "B", AlbumArtist = "A", AlbumArtists = ["A"], MbTrackId = trackId,
        MbRecordingId = recordingId, MbReleaseId = releaseId, OriginalDate = "1999-02-03", Isrc = "GBAAA0000001", ReleaseType = "album; compilation",
    };

    [Fact]
    public async Task ScanReadsNewFieldsAndUsesTheReleaseTrackId()
    {
        using var app = new SubsonicTestHost();
        var trackId = Guid.NewGuid().ToString();
        app.AddFile("A/B/1.m4a", Tags("One", trackId));
        await app.ScanAsync();

        var e = await app.EntryAsync("A/B/1.m4a");
        Assert.Equal($"tr-{trackId}", e!.SongId);
        Assert.Equal(trackId, e.MbTrackId);
        Assert.Equal("1999-02-03", e.OriginalDate);
        Assert.Equal("GBAAA0000001", e.Isrc);
        Assert.Equal(["album", "compilation"], e.ReleaseTypes);
        Assert.NotNull(e.AddedAt);
    }

    [Fact]
    public async Task IdNeverChangesWithTheTags()
    {
        using var app = new SubsonicTestHost();
        var path = app.AddFile("A/B/1.m4a", Tags("One"));
        await app.ScanAsync();
        var before = await app.EntryAsync("A/B/1.m4a");
        Assert.StartsWith("tr-", before!.SongId);

        // New tags (now with a release track ID) and a new mtime: same song ID.
        MusicManager.Core.Services.TagIo.WriteM4a(path, Tags("One (edited)", Guid.NewGuid().ToString()), null, "manual");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        await app.ScanAsync();
        var after = await app.EntryAsync("A/B/1.m4a");
        Assert.Equal("One (edited)", after!.Title);
        Assert.Equal(before.SongId, after.SongId);
        Assert.Equal(before.AddedAt, after.AddedAt);
    }

    [Fact]
    public async Task MoveOutsideTheToolKeepsTheId()
    {
        using var app = new SubsonicTestHost();
        var trackId = Guid.NewGuid().ToString();
        var path = app.AddFile("A/B/1.m4a", Tags("One", trackId));
        app.AddFile("A/B/2.m4a", Tags("Two", recordingId: "rec-2", releaseId: "rel-1"));
        await app.ScanAsync();
        var one = await app.EntryAsync("A/B/1.m4a");
        var two = await app.EntryAsync("A/B/2.m4a");

        Directory.CreateDirectory(Path.Combine(app.LibraryRoot, "C"));
        File.Move(path, Path.Combine(app.LibraryRoot, "C", "1.m4a"));
        File.Move(Path.Combine(app.LibraryRoot, "A/B/2.m4a"), Path.Combine(app.LibraryRoot, "C", "2.m4a"));
        await app.ScanAsync();

        Assert.Null(await app.EntryAsync("A/B/1.m4a"));
        var movedOne = await app.EntryAsync("C/1.m4a");
        var movedTwo = await app.EntryAsync("C/2.m4a");
        Assert.Equal(one!.SongId, movedOne!.SongId);
        Assert.Equal(one.AddedAt, movedOne.AddedAt);
        Assert.Equal(two!.SongId, movedTwo!.SongId);
    }

    [Fact]
    public async Task ReplacedFileTakesOverIdAndAddedDate()
    {
        using var app = new SubsonicTestHost();
        app.AddFile("A/B/old.m4a", Tags("Old"));
        await app.ScanAsync();
        var old = await app.EntryAsync("A/B/old.m4a");

        // "Keep new" or a retag that moves the file: the pipeline names the replaced path.
        File.Delete(Path.Combine(app.LibraryRoot, "A/B/old.m4a"));
        app.AddFile("A/B/new.m4a", Tags("New", Guid.NewGuid().ToString()));
        await app.Get<LibraryIndexer>().UpsertAsync("A/B/new.m4a", null, CancellationToken.None, replaces: "A/B/old.m4a");

        Assert.Null(await app.EntryAsync("A/B/old.m4a"));
        var replaced = await app.EntryAsync("A/B/new.m4a");
        Assert.Equal(old!.SongId, replaced!.SongId);
        Assert.Equal(old.AddedAt, replaced.AddedAt);

        // The API serves the replacing file under the old ID.
        var r = await app.CallAsync("getSong", $"id={old.SongId}");
        Assert.Equal("New", r.GetProperty("song").GetProperty("title").GetString());
    }

    [Fact]
    public async Task ReplacingAtTheSamePathKeepsTheId()
    {
        using var app = new SubsonicTestHost();
        app.AddFile("A/B/1.m4a", Tags("Old"));
        await app.ScanAsync();
        var old = await app.EntryAsync("A/B/1.m4a");

        app.AddFile("A/B/1.m4a", Tags("New", Guid.NewGuid().ToString()));
        await app.Get<LibraryIndexer>().UpsertAsync("A/B/1.m4a", null, CancellationToken.None, replaces: "A/B/1.m4a");
        Assert.Equal(old!.SongId, (await app.EntryAsync("A/B/1.m4a"))!.SongId);
    }

    [Fact]
    public void AmbiguousMovesDoNotCarryOver()
    {
        LibraryEntry E(string path, string? id, string? track) => new() { RelativePath = path, SongId = id, MbTrackId = track };
        var removed = new List<LibraryEntry> { E("a", "tr-1", "t"), E("b", "tr-2", "t") };
        var added = new List<LibraryEntry> { E("c", null, "t") };
        Assert.Equal(0, LibraryIndexer.CarryOverMoved(removed, added));
        Assert.Null(added[0].SongId);

        var removed2 = new List<LibraryEntry> { E("a", "tr-1", "t") };
        var added2 = new List<LibraryEntry> { E("c", null, "t"), E("d", null, "t") };
        Assert.Equal(0, LibraryIndexer.CarryOverMoved(removed2, added2));

        // Recording + release as the second key.
        var removed3 = new List<LibraryEntry> { new() { RelativePath = "a", SongId = "tr-9", MbRecordingId = "r", MbReleaseId = "l" } };
        var added3 = new List<LibraryEntry> { new() { RelativePath = "b", MbRecordingId = "r", MbReleaseId = "l" } };
        Assert.Equal(1, LibraryIndexer.CarryOverMoved(removed3, added3));
        Assert.Equal("tr-9", added3[0].SongId);
    }

    [Fact]
    public void UsedReleaseTrackIdGetsARandomId()
    {
        var entries = new List<LibraryEntry>
        {
            new() { RelativePath = "a", MbTrackId = "x" },
            new() { RelativePath = "b", MbTrackId = "x" },
        };
        LibraryIndexer.AssignSongIds(entries, []);
        Assert.Equal("tr-x", entries[0].SongId);
        Assert.NotEqual("tr-x", entries[1].SongId);
        Assert.StartsWith("tr-", entries[1].SongId);
    }

    [Fact]
    public void ExistingRowsUseTheFileTimeAsAddedDate()
    {
        var mtime = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var e = new LibraryEntry { RelativePath = "a", FileModifiedUtc = mtime };
        LibraryIndexer.AssignSongIds([e], []);
        Assert.Equal(new DateTimeOffset(mtime), e.AddedAt);
    }
}

public class CatalogIdTests
{
    [Fact]
    public void AlbumIdUsesReleaseIdElseNames()
    {
        Assert.Equal("al-abc", CatalogIds.Album("ABC", "x", "y"));
        Assert.Equal(CatalogIds.Album(null, "Björk ", "Post"), CatalogIds.Album(null, "bjork", "POST"));
        Assert.NotEqual(CatalogIds.Album(null, "A", "B"), CatalogIds.Album(null, "A", "C"));
    }

    [Fact]
    public void NonLatinNamesKeepDistinctIds() =>
        Assert.NotEqual(CatalogIds.Artist(null, "宇多田ヒカル"), CatalogIds.Artist(null, "椎名林檎"));

    [Fact]
    public void ArtistIdsArePrefixed()
    {
        Assert.StartsWith("ar-", CatalogIds.Artist(null, "X"));
        Assert.Equal("ar-1234", CatalogIds.Artist("1234", "X"));
    }
}
