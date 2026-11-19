using Microsoft.EntityFrameworkCore;
using MusicManager.Core.Catalog;
using MusicManager.Core.Data;
using MusicManager.Core.Pipeline;

namespace MusicManager.Tests.Subsonic;

/// <summary>Starring and unstarring a song again within a few seconds reports it for a later check (issue #18).</summary>
public class SongReportTests
{
    private static async Task<SubsonicTestHost> StartAsync()
    {
        var app = new SubsonicTestHost();
        await app.SeedAsync(Enumerable.Range(1, 2).Select(t => new LibraryEntry
        {
            RelativePath = $"Artist/Album/{t}.m4a", SongId = $"tr-{t}", Title = $"Song {t}", Artist = "Artist", Artists = ["Artist"],
            AlbumArtist = "Artist", AlbumArtists = ["Artist"], Album = "Album", TrackNumber = t, DurationSeconds = 100, FileSize = 1000,
            AddedAt = DateTimeOffset.UtcNow,
        }));
        return app;
    }

    private static async Task Ok(SubsonicTestHost app, string endpoint, string query)
    {
        var r = await app.CallAsync(endpoint, query);
        Assert.True(r.GetProperty("status").GetString() == "ok", r.ToString());
    }

    private static async Task<List<SongReport>> ReportsAsync(SubsonicTestHost app)
    {
        await using var db = await app.Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        return await db.SongReports.AsNoTracking().OrderBy(r => r.Id).ToListAsync();
    }

    [Fact]
    public async Task QuickUnstarReportsTheSongAndCountsRepeats()
    {
        using var app = await StartAsync();
        await Ok(app, "star", "id=tr-1");
        await Ok(app, "unstar", "id=tr-1");

        var report = Assert.Single(await ReportsAsync(app));
        Assert.Equal("tr-1", report.SongId);
        Assert.Equal("Song 1", report.Title);
        Assert.Equal("Artist", report.Artist);
        Assert.Equal(1, report.Count);
        Assert.Null(report.ResolvedAt);
        // The favorite itself is gone again.
        Assert.False((await app.CallAsync("getSong", "id=tr-1")).GetProperty("song").TryGetProperty("starred", out _));

        await Ok(app, "star", "id=tr-1");
        await Ok(app, "unstar", "id=tr-1");
        report = Assert.Single(await ReportsAsync(app));
        Assert.Equal(2, report.Count);

        // Once done, the next report opens a new one.
        var reports = app.Get<SongReportService>();
        await reports.SetResolvedAsync(report.Id, true);
        await Ok(app, "star", "id=tr-1");
        await Ok(app, "unstar", "id=tr-1");
        var all = await ReportsAsync(app);
        Assert.Equal(2, all.Count);
        Assert.Single(all, r => r.ResolvedAt == null);

        // Reopening the old one while a newer one is open keeps a single open report.
        await reports.SetResolvedAsync(report.Id, false);
        Assert.Single(await ReportsAsync(app), r => r.ResolvedAt == null);
    }

    [Fact]
    public async Task UnstarOfAnOlderFavoriteIsNoReport()
    {
        using var app = await StartAsync();
        await Ok(app, "star", "id=tr-1&id=tr-2");
        await using (var db = await app.Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
        {
            var star = await db.Stars.SingleAsync(s => s.ItemId == "tr-1");
            star.StarredAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            await db.SaveChangesAsync();
        }

        await Ok(app, "unstar", "id=tr-1&id=tr-2");
        Assert.Equal(["tr-2"], (await ReportsAsync(app)).Select(r => r.SongId));
    }

    [Fact]
    public async Task StarringAnExistingFavoriteAgainKeepsItsDate()
    {
        using var app = await StartAsync();
        await Ok(app, "star", "id=tr-1");
        await using (var db = await app.Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
        {
            var star = await db.Stars.SingleAsync(s => s.ItemId == "tr-1");
            star.StarredAt = DateTimeOffset.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        }

        await Ok(app, "star", "id=tr-1");
        await Ok(app, "unstar", "id=tr-1");
        Assert.Empty(await ReportsAsync(app));
    }

    [Fact]
    public async Task AlbumsAndArtistsAreNotReported()
    {
        using var app = await StartAsync();
        var album = CatalogIds.Album(null, "Artist", "Album");
        var artist = CatalogIds.Artist(null, "Artist");
        await Ok(app, "star", $"albumId={album}&artistId={artist}");
        await Ok(app, "unstar", $"albumId={album}&artistId={artist}");
        Assert.Empty(await ReportsAsync(app));
    }

    [Fact]
    public async Task WindowZeroTurnsReportsOff()
    {
        using var app = await StartAsync();
        var settings = app.Get<SettingsService>();
        var s = await settings.GetCopyAsync();
        s.ReportUnstarSeconds = 0;
        await settings.SaveAsync(s);

        await Ok(app, "star", "id=tr-1");
        await Ok(app, "unstar", "id=tr-1");
        Assert.Empty(await ReportsAsync(app));
    }
}
