using Microsoft.EntityFrameworkCore;
using MusicManager.Core.Catalog;
using MusicManager.Core.Data;
using MusicManager.Core.Model;
using MusicManager.Core.Pipeline;
using MusicManager.Core.Services;

namespace MusicManager.Tests.Subsonic;

/// <summary>Song ID, added date, favorite and rating are mirrored into the files and restored from them.</summary>
public class FileMarkerTests
{
    private static TagSet Tags(string title, string? trackId = null) => new()
    {
        Title = title, Artist = "A", Artists = ["A"], Album = "B", AlbumArtist = "A", AlbumArtists = ["A"], MbTrackId = trackId,
    };

    [Fact]
    public async Task MarkersAreWrittenIntoTheFile()
    {
        using var app = new SubsonicTestHost();
        var path = app.AddFile("A/B/1.m4a", Tags("One"));
        await app.ScanAsync();
        var entry = (await app.EntryAsync("A/B/1.m4a"))!;

        await app.Get<UserDataStore>().StarAsync([entry.SongId!]);
        await app.Get<UserDataStore>().SetRatingAsync(entry.SongId!, 4);
        Assert.Equal(1, await app.Get<FileMarkerSync>().SyncAsync(CancellationToken.None));

        var markers = TagIo.ReadMarkers(path);
        Assert.Equal(entry.SongId, markers.SongId);
        Assert.Equal(entry.AddedAt?.ToUnixTimeSeconds(), markers.AddedAt?.ToUnixTimeSeconds());
        Assert.Equal(4, markers.Rating);
        Assert.NotNull(markers.Favorite);

        // Up to date: nothing to write; the next scan does not see a changed file.
        Assert.Equal(0, await app.Get<FileMarkerSync>().SyncAsync(CancellationToken.None));
        await app.Get<UserDataStore>().UnstarAsync([entry.SongId!]);
        Assert.Equal(1, await app.Get<FileMarkerSync>().SyncAsync(CancellationToken.None));
        Assert.Null(TagIo.ReadMarkers(path).Favorite);
    }

    [Fact]
    public async Task LostDatabaseIsRebuiltFromTheFiles()
    {
        using var app = new SubsonicTestHost();
        app.AddFile("A/B/1.m4a", Tags("One"));
        await app.ScanAsync();
        var before = (await app.EntryAsync("A/B/1.m4a"))!;
        await app.Get<UserDataStore>().StarAsync([before.SongId!]);
        await app.Get<UserDataStore>().SetRatingAsync(before.SongId!, 5);
        await app.Get<FileMarkerSync>().SyncAsync(CancellationToken.None);

        await using (var db = await app.Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
        {
            await db.Library.ExecuteDeleteAsync();
            await db.Stars.ExecuteDeleteAsync();
            await db.Ratings.ExecuteDeleteAsync();
        }

        await app.ScanAsync();
        var after = (await app.EntryAsync("A/B/1.m4a"))!;
        Assert.Equal(before.SongId, after.SongId);
        Assert.Equal(before.AddedAt?.ToUnixTimeSeconds(), after.AddedAt?.ToUnixTimeSeconds());
        var data = await app.Get<UserDataStore>().GetAsync();
        Assert.NotNull(data.Starred(after.SongId!));
        Assert.Equal(5, data.Rating(after.SongId!));
    }

    [Fact]
    public async Task CopiedFileGetsItsOwnId()
    {
        using var app = new SubsonicTestHost();
        var path = app.AddFile("A/B/1.m4a", Tags("One"));
        await app.ScanAsync();
        await app.Get<FileMarkerSync>().SyncAsync(CancellationToken.None);
        var original = (await app.EntryAsync("A/B/1.m4a"))!.SongId;

        File.Copy(path, Path.Combine(app.LibraryRoot, "A/B/1 copy.m4a"));
        await app.ScanAsync();
        Assert.Equal(original, (await app.EntryAsync("A/B/1.m4a"))!.SongId);
        var copy = (await app.EntryAsync("A/B/1 copy.m4a"))!.SongId;
        Assert.NotNull(copy);
        Assert.NotEqual(original, copy);

        // The copy's file gets its new ID.
        await app.Get<FileMarkerSync>().SyncAsync(CancellationToken.None);
        Assert.Equal(copy, TagIo.ReadMarkers(Path.Combine(app.LibraryRoot, "A/B/1 copy.m4a")).SongId);
    }

    [Fact]
    public void RetaggingKeepsTheMarkers()
    {
        var path = Path.Combine(Path.GetTempPath(), $"markers-{Guid.NewGuid():N}.m4a");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.m4a"), path);
        try
        {
            var markers = new FileMarkers("tr-abc", DateTimeOffset.Parse("2024-05-06T07:08:09Z"), 3, DateTimeOffset.Parse("2025-01-02T03:04:05Z"));
            TagIo.WriteM4a(path, Tags("One"), null, "musicbrainz");
            TagIo.WriteMarkers(path, markers);
            TagIo.WriteM4a(path, Tags("One (new)"), null, "musicbrainz");

            Assert.Equal(markers, TagIo.ReadMarkers(path));
            Assert.Equal("One (new)", TagIo.Read(path).Title);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
