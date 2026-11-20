using Microsoft.EntityFrameworkCore;
using MusicManager.Core.Data;
using MusicManager.Core.Pipeline;

namespace MusicManager.Core.Catalog;

/// <summary>What the history shows for one event; a start becomes a skip or stays open.</summary>
public enum HistoryKind
{
    Played,
    Skipped,

    /// <summary>Started, not played yet and not over yet: may still become a play.</summary>
    Playing,
    Starred,
    Unstarred,
    Reported,
}

public sealed record HistoryEntry(DateTimeOffset At, string SongId, HistoryKind Kind, string? Title, string? Artist, string? Client);

/// <summary>One bar of the chart: plays and skips from <see cref="Start"/> (local time) until the next bucket.</summary>
public sealed record HistoryBucket(DateTime Start, int Plays, int Skips);

public enum BucketSize
{
    Day,
    Week,
    Month,
}

public sealed record SongCount(string SongId, string? Title, string? Artist, int Plays, int Skips);

public sealed record ArtistCount(string Name, int Plays, int Skips);

public sealed record PlayHistoryReport
{
    public int Plays { get; init; }
    public int Skips { get; init; }
    public int Starred { get; init; }
    public int Reported { get; init; }

    /// <summary>Length of the played songs (as far as they are in the library).</summary>
    public TimeSpan ListeningTime { get; init; }

    /// <summary>Different songs played.</summary>
    public int Songs { get; init; }

    /// <summary>Skips among all played and skipped songs (0..1); null without any.</summary>
    public double? SkipRate => Plays + Skips > 0 ? (double)Skips / (Plays + Skips) : null;

    /// <summary>True when a client reports starts ("now playing"); without them no skip can be seen.</summary>
    public bool HasStarts { get; init; }

    public BucketSize BucketSize { get; init; }
    public IReadOnlyList<HistoryBucket> Buckets { get; init; } = [];
    public IReadOnlyList<SongCount> TopPlayed { get; init; } = [];
    public IReadOnlyList<SongCount> TopSkipped { get; init; } = [];
    public IReadOnlyList<ArtistCount> TopArtists { get; init; } = [];

    /// <summary>Newest first.</summary>
    public IReadOnlyList<HistoryEntry> Entries { get; init; } = [];

    /// <summary>The first event at all; null while the history is empty.</summary>
    public DateTimeOffset? FirstEvent { get; init; }
}

/// <summary>
/// History of what was played, skipped, starred and reported in the players (issue #20), and statistics over it.
/// The events come from the Subsonic API: <c>scrobble</c> without <c>submission</c> ("now playing") is a start,
/// with it a play, plus <c>star</c>/<c>unstar</c>. A skip is a start of a song that got no play: none within
/// <see cref="Grace"/> before the start up to its length plus <see cref="Grace"/> after it. It is only decided
/// once the same client started another song or that time is over, and worked out on every read, so a play
/// that arrives late (an offline player sends its scrobbles later) turns the skip back into a play.
/// </summary>
public sealed class PlayHistoryService(IDbContextFactory<AppDbContext> dbFactory, MediaCatalog catalog, PipelineNotifier notifier)
{
    /// <summary>Slack around a start in which a play of the same song counts for it.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(15);

    /// <summary>A play may carry a time a little before the start was reported.</summary>
    private static readonly TimeSpan Before = TimeSpan.FromMinutes(2);

    /// <summary>Assumed length of a song that is not in the library.</summary>
    private const int UnknownSeconds = 600;

    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Stores events of songs (album and artist IDs are ignored). A start of the song the same client started last,
    /// while that one is not over yet, is not stored again (players repeat "now playing" e.g. after a pause).
    /// </summary>
    public async Task RecordAsync(PlayEventKind kind, IReadOnlyList<(string SongId, DateTimeOffset At)> events, string? client,
        CancellationToken ct = default)
    {
        var songs = events.Where(e => CatalogIds.IsSong(e.SongId)).ToList();
        if (songs.Count == 0) return;
        var snapshot = await catalog.GetAsync(ct);
        client = string.IsNullOrWhiteSpace(client) ? null : client.Trim();

        await _lock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            foreach (var (songId, at) in songs)
            {
                var song = snapshot.Song(songId);
                if (kind == PlayEventKind.Started)
                {
                    var last = await db.PlayEvents.AsNoTracking().Where(e => e.Kind == PlayEventKind.Started && e.Client == client)
                        .OrderByDescending(e => e.At).ThenByDescending(e => e.Id).FirstOrDefaultAsync(ct);
                    if (last != null && last.SongId == songId && at - last.At < Length(song) + Grace) continue;
                }

                db.PlayEvents.Add(new PlayEvent
                {
                    SongId = songId, Kind = kind, At = at, Client = client, Title = song?.Title, Artist = song?.DisplayArtist,
                });
                // The next start's check above must see this one.
                await db.SaveChangesAsync(ct);
            }
        }
        finally
        {
            _lock.Release();
        }

        notifier.Notify();
    }

    public Task RecordAsync(PlayEventKind kind, IEnumerable<string> songIds, DateTimeOffset at, string? client, CancellationToken ct = default) =>
        RecordAsync(kind, songIds.Distinct(StringComparer.Ordinal).Select(id => (id, at)).ToList(), client, ct);

    /// <summary>Statistics and history from <paramref name="from"/> (null = everything) until now.</summary>
    public async Task<PlayHistoryReport> GetReportAsync(DateTimeOffset? from, DateTimeOffset now, CancellationToken ct = default)
    {
        var snapshot = await catalog.GetAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var first = await db.PlayEvents.AsNoTracking().OrderBy(e => e.At).Select(e => (DateTimeOffset?)e.At).FirstOrDefaultAsync(ct);
        var query = db.PlayEvents.AsNoTracking();
        if (from is { } f)
        {
            // Plays a little before the period may belong to a start in it.
            var load = f - Before;
            query = query.Where(e => e.At >= load);
        }

        var events = await query.OrderBy(e => e.At).ThenBy(e => e.Id).ToListAsync(ct);
        var start = from ?? first ?? now;
        return Build(events, start, now, snapshot, first);
    }

    /// <summary>Works out skips and the statistics of the events (sorted by time) in [<paramref name="from"/>, <paramref name="now"/>].</summary>
    public static PlayHistoryReport Build(IReadOnlyList<PlayEvent> events, DateTimeOffset from, DateTimeOffset now, CatalogSnapshot catalog,
        DateTimeOffset? first = null)
    {
        var plays = events.Where(e => e.Kind == PlayEventKind.Played).ToLookup(e => e.SongId, StringComparer.Ordinal);
        var startsByClient = events.Where(e => e.Kind == PlayEventKind.Started).GroupBy(e => e.Client ?? "")
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.At).ThenBy(e => e.Id).ToList());

        var entries = new List<HistoryEntry>();
        foreach (var e in events)
        {
            if (e.At < from || e.At > now) continue;
            var song = catalog.Song(e.SongId);
            var title = song?.Title ?? e.Title;
            var artist = song?.DisplayArtist ?? e.Artist;
            HistoryKind? kind = e.Kind switch
            {
                PlayEventKind.Played => HistoryKind.Played,
                PlayEventKind.Starred => HistoryKind.Starred,
                PlayEventKind.Unstarred => HistoryKind.Unstarred,
                PlayEventKind.Reported => HistoryKind.Reported,
                PlayEventKind.Started => Classify(e, song, plays[e.SongId], startsByClient[e.Client ?? ""], now),
                _ => null,
            };
            if (kind is { } k) entries.Add(new HistoryEntry(e.At, e.SongId, k, title, artist, e.Client));
        }

        var played = entries.Where(e => e.Kind == HistoryKind.Played).ToList();
        var skipped = entries.Where(e => e.Kind == HistoryKind.Skipped).ToList();
        var skipsBySong = skipped.GroupBy(e => e.SongId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var playsBySong = played.GroupBy(e => e.SongId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var names = entries.GroupBy(e => e.SongId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        SongCount Count(string id) => new(id, names[id].Title, names[id].Artist, playsBySong.GetValueOrDefault(id), skipsBySong.GetValueOrDefault(id));

        // Artists one by one ("A feat. B" counts for both), from the library, else as they were when it was played.
        IEnumerable<string> Artists(HistoryEntry e) =>
            catalog.Song(e.SongId) is { Artists.Count: > 0 } s ? s.Artists.Select(a => a.Name).Distinct()
            : string.IsNullOrWhiteSpace(e.Artist) ? [] : [e.Artist];

        var artistPlays = played.SelectMany(e => Artists(e).Select(a => (a, play: 1, skip: 0)))
            .Concat(skipped.SelectMany(e => Artists(e).Select(a => (a, play: 0, skip: 1))))
            .GroupBy(x => x.a, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ArtistCount(g.First().a, g.Sum(x => x.play), g.Sum(x => x.skip)))
            .Where(a => a.Plays > 0)
            .OrderByDescending(a => a.Plays).ThenBy(a => a.Skips).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Take(10).ToList();

        var size = BucketFor(from, now);
        return new PlayHistoryReport
        {
            Plays = played.Count,
            Skips = skipped.Count,
            Starred = entries.Count(e => e.Kind == HistoryKind.Starred),
            Reported = entries.Count(e => e.Kind == HistoryKind.Reported),
            ListeningTime = TimeSpan.FromSeconds(played.Sum(e => catalog.Song(e.SongId)?.Duration ?? 0)),
            Songs = playsBySong.Count,
            HasStarts = startsByClient.Count > 0,
            BucketSize = size,
            Buckets = Buckets(from, now, size, played, skipped),
            TopPlayed = playsBySong.Keys.Select(Count).OrderByDescending(s => s.Plays).ThenBy(s => s.Skips)
                .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase).Take(10).ToList(),
            TopSkipped = skipsBySong.Keys.Select(Count).OrderByDescending(s => s.Skips).ThenBy(s => s.Plays)
                .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase).Take(10).ToList(),
            TopArtists = artistPlays,
            Entries = Enumerable.Reverse(entries).ToList(),
            FirstEvent = first ?? events.FirstOrDefault()?.At,
        };
    }

    /// <summary>
    /// Played (null: the start is shown by its play), skipped, or still playing. Skipped once the same client started
    /// another song or the song's length plus <see cref="Grace"/> is over, without a play of it in that time.
    /// </summary>
    private static HistoryKind? Classify(PlayEvent start, CatalogSong? song, IEnumerable<PlayEvent> plays, List<PlayEvent> clientStarts,
        DateTimeOffset now)
    {
        var end = start.At + Length(song) + Grace;
        if (plays.Any(p => p.At >= start.At - Before && p.At <= end)) return null;
        var index = clientStarts.IndexOf(start);
        var next = index >= 0 && index + 1 < clientStarts.Count ? clientStarts[index + 1] : null;
        return next != null || now > end ? HistoryKind.Skipped : HistoryKind.Playing;
    }

    private static TimeSpan Length(CatalogSong? song) => TimeSpan.FromSeconds(song?.Duration is > 0 and var d ? d : UnknownSeconds);

    private static BucketSize BucketFor(DateTimeOffset from, DateTimeOffset now) => (now - from).TotalDays switch
    {
        <= 62 => BucketSize.Day,
        <= 371 => BucketSize.Week,
        _ => BucketSize.Month,
    };

    /// <summary>Start (local time) of the bucket the moment falls into; weeks start on Monday.</summary>
    public static DateTime BucketStart(DateTime local, BucketSize size) => size switch
    {
        BucketSize.Day => local.Date,
        BucketSize.Week => local.Date.AddDays(-(((int)local.DayOfWeek + 6) % 7)),
        _ => new DateTime(local.Year, local.Month, 1),
    };

    private static DateTime Next(DateTime start, BucketSize size) => size switch
    {
        BucketSize.Day => start.AddDays(1),
        BucketSize.Week => start.AddDays(7),
        _ => start.AddMonths(1),
    };

    private static List<HistoryBucket> Buckets(DateTimeOffset from, DateTimeOffset now, BucketSize size, List<HistoryEntry> played,
        List<HistoryEntry> skipped)
    {
        var plays = played.GroupBy(e => BucketStart(e.At.LocalDateTime, size)).ToDictionary(g => g.Key, g => g.Count());
        var skips = skipped.GroupBy(e => BucketStart(e.At.LocalDateTime, size)).ToDictionary(g => g.Key, g => g.Count());
        var list = new List<HistoryBucket>();
        var last = BucketStart(now.LocalDateTime, size);
        for (var b = BucketStart(from.LocalDateTime, size); b <= last; b = Next(b, size))
            list.Add(new HistoryBucket(b, plays.GetValueOrDefault(b), skips.GetValueOrDefault(b)));
        return list;
    }
}
