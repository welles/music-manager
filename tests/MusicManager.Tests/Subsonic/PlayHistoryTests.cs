using Microsoft.EntityFrameworkCore;
using MusicManager.Core.Catalog;
using MusicManager.Core.Data;

namespace MusicManager.Tests.Subsonic;

/// <summary>History and statistics of played, skipped, starred and reported songs (issue #20).</summary>
public class PlayHistoryTests
{
    private static async Task<SubsonicTestHost> StartAsync()
    {
        var app = new SubsonicTestHost();
        await app.SeedAsync(Enumerable.Range(1, 3).Select(t => new LibraryEntry
        {
            RelativePath = $"Artist/Album/{t}.m4a", SongId = $"tr-{t}", Title = $"Song {t}", Artist = t == 3 ? "Artist feat. Guest" : "Artist",
            Artists = t == 3 ? ["Artist", "Guest"] : ["Artist"], AlbumArtist = "Artist", AlbumArtists = ["Artist"], Album = "Album",
            TrackNumber = t, DurationSeconds = 200, FileSize = 1000, AddedAt = DateTimeOffset.UtcNow,
        }));
        return app;
    }

    private static async Task Ok(SubsonicTestHost app, string endpoint, string query)
    {
        var r = await app.CallAsync(endpoint, query);
        Assert.True(r.GetProperty("status").GetString() == "ok", r.ToString());
    }

    private static Task<PlayHistoryReport> ReportAsync(SubsonicTestHost app, DateTimeOffset? now = null) =>
        app.Get<PlayHistoryService>().GetReportAsync(DateTimeOffset.UtcNow.AddDays(-7), now ?? DateTimeOffset.UtcNow);

    private static async Task SeedAsync(SubsonicTestHost app, params PlayEvent[] events)
    {
        await using var db = await app.Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        db.PlayEvents.AddRange(events);
        await db.SaveChangesAsync();
    }

    private static PlayEvent Event(string song, PlayEventKind kind, DateTimeOffset at, string? client = "Symfonium") =>
        new() { SongId = song, Kind = kind, At = at, Client = client };

    [Fact]
    public async Task StartsPlaysAndSkipsComeFromScrobbles()
    {
        using var app = await StartAsync();
        // Song 1 is started and played, song 2 started and left for song 3 (a skip), song 3 still runs.
        await Ok(app, "scrobble", "id=tr-1&submission=false&c=Symfonium");
        // Players repeat "now playing" (e.g. after a pause): still one start.
        await Ok(app, "scrobble", "id=tr-1&submission=false&c=Symfonium");
        await Ok(app, "scrobble", "id=tr-1&c=Symfonium");
        await Ok(app, "scrobble", "id=tr-2&submission=false&c=Symfonium");
        await Ok(app, "scrobble", "id=tr-3&submission=false&c=Symfonium");

        var report = await ReportAsync(app);
        Assert.Equal(1, report.Plays);
        Assert.Equal(1, report.Skips);
        Assert.Equal(0.5, report.SkipRate);
        Assert.True(report.HasStarts);
        Assert.Equal(TimeSpan.FromSeconds(200), report.ListeningTime);
        Assert.Equal(
            [(HistoryKind.Playing, "tr-3"), (HistoryKind.Skipped, "tr-2"), (HistoryKind.Played, "tr-1")],
            report.Entries.Select(e => (e.Kind, e.SongId)));
        Assert.All(report.Entries, e => Assert.Equal("Symfonium", e.Client));
        Assert.Equal("Song 2", Assert.Single(report.TopSkipped).Title);
        Assert.Equal(("tr-1", 1), (Assert.Single(report.TopPlayed).SongId, report.TopPlayed[0].Plays));

        // The play counts are unchanged by "now playing".
        Assert.Equal(1, (await app.CallAsync("getSong", "id=tr-1")).GetProperty("song").GetProperty("playCount").GetInt64());
    }

    [Fact]
    public async Task FavoritesAndReportsAreRecorded()
    {
        using var app = await StartAsync();
        await Ok(app, "star", "id=tr-1&id=tr-2");
        // Starring again is no new favorite.
        await Ok(app, "star", "id=tr-1");
        await Ok(app, "unstar", "id=tr-1");
        await using (var db = await app.Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
        {
            var star = await db.Stars.SingleAsync(s => s.ItemId == "tr-2");
            star.StarredAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            await db.SaveChangesAsync();
        }

        await Ok(app, "unstar", "id=tr-2");
        // Albums are not part of the history.
        await Ok(app, "star", $"albumId={CatalogIds.Album(null, "Artist", "Album")}");

        var report = await ReportAsync(app);
        Assert.Equal(2, report.Starred);
        Assert.Equal(1, report.Reported);
        Assert.Equal(
            [(HistoryKind.Unstarred, "tr-2"), (HistoryKind.Reported, "tr-1"), (HistoryKind.Starred, "tr-2"), (HistoryKind.Starred, "tr-1")],
            report.Entries.Select(e => (e.Kind, e.SongId)));
    }

    [Fact]
    public async Task AStartIsOnlyASkipOnceItCouldNotBePlayedAnyMore()
    {
        using var app = await StartAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(app,
            // Long over, no play: skipped.
            Event("tr-1", PlayEventKind.Started, now.AddHours(-2)),
            // Long over, but the (late) play came: played.
            Event("tr-2", PlayEventKind.Started, now.AddHours(-1)),
            Event("tr-2", PlayEventKind.Played, now.AddHours(-1).AddSeconds(100)),
            // Started a minute ago on another client: may still be played.
            Event("tr-3", PlayEventKind.Started, now.AddMinutes(-1), "Other"));

        var report = await ReportAsync(app, now);
        Assert.Equal(
            [(HistoryKind.Playing, "tr-3"), (HistoryKind.Played, "tr-2"), (HistoryKind.Skipped, "tr-1")],
            report.Entries.Select(e => (e.Kind, e.SongId)));

        // Once its length and the grace time are over, it is a skip too.
        report = await ReportAsync(app, now.AddSeconds(200) + PlayHistoryService.Grace);
        Assert.Equal(2, report.Skips);
    }

    [Fact]
    public async Task ArtistsCountOneByOneAndBucketsCoverThePeriod()
    {
        using var app = await StartAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(app,
            Event("tr-3", PlayEventKind.Played, now.AddDays(-2)),
            Event("tr-1", PlayEventKind.Played, now.AddDays(-1)),
            Event("tr-1", PlayEventKind.Played, now.AddHours(-1)),
            // Not in the library any more: shown with the name it had.
            new PlayEvent { SongId = "tr-gone", Kind = PlayEventKind.Played, At = now.AddMinutes(-5), Title = "Old", Artist = "Someone" });

        var report = await ReportAsync(app, now);
        Assert.Equal(4, report.Plays);
        Assert.Equal(3, report.Songs);
        Assert.Equal([("Artist", 3), ("Guest", 1), ("Someone", 1)], report.TopArtists.Select(a => (a.Name, a.Plays)));
        Assert.Equal(("tr-1", 2), (report.TopPlayed[0].SongId, report.TopPlayed[0].Plays));
        Assert.Contains(report.TopPlayed, s => s is { SongId: "tr-gone", Title: "Old" });

        Assert.Equal(BucketSize.Day, report.BucketSize);
        Assert.Equal(4, report.Buckets.Sum(b => b.Plays));
        Assert.Equal(report.Buckets.Select(b => b.Start).Distinct().Count(), report.Buckets.Count);
        Assert.Equal(now.LocalDateTime.Date, report.Buckets[^1].Start);
        Assert.Equal(7, report.Days);
    }

    [Fact]
    public async Task DaysCountFromThePeriodNotFromTheFirstBucket()
    {
        using var app = await StartAsync();
        var now = DateTimeOffset.UtcNow;
        // A Wednesday a year ago: the first weekly bar starts on the Monday before it.
        var from = now.AddDays(-365);
        while (from.LocalDateTime.DayOfWeek != DayOfWeek.Wednesday) from = from.AddDays(1);
        var report = await app.Get<PlayHistoryService>().GetReportAsync(from, now);

        Assert.Equal(BucketSize.Week, report.BucketSize);
        Assert.True(report.Buckets[0].Start < from.LocalDateTime.Date);
        Assert.Equal(from, report.From);
        Assert.Equal((int)Math.Ceiling((now - from).TotalDays), report.Days);
    }
}
