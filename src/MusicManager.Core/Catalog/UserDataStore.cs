using Microsoft.EntityFrameworkCore;
using MusicManager.Core.Data;

namespace MusicManager.Core.Catalog;

/// <summary>Play count and last play of a song or album.</summary>
public readonly record struct PlayInfo(int Count, DateTimeOffset? LastPlayed);

/// <summary>
/// Immutable view of the user data (stars, ratings, play stats) for the Subsonic API. Lookups are
/// dictionary hits, so annotating a large search3 page costs no queries.
/// </summary>
public sealed class UserDataSnapshot
{
    private readonly Dictionary<string, DateTimeOffset> _stars;
    private readonly Dictionary<string, int> _ratings;
    private readonly Dictionary<string, PlayInfo> _plays;

    public UserDataSnapshot(long version, IEnumerable<Star> stars, IEnumerable<Rating> ratings, IEnumerable<PlayStat> plays)
    {
        Version = version;
        _stars = stars.ToDictionary(s => s.ItemId, s => s.StarredAt, StringComparer.Ordinal);
        _ratings = ratings.ToDictionary(r => r.ItemId, r => r.Value, StringComparer.Ordinal);
        _plays = plays.ToDictionary(p => p.SongId, p => new PlayInfo(p.PlayCount, p.LastPlayed), StringComparer.Ordinal);
    }

    public long Version { get; }

    public DateTimeOffset? Starred(string id) => _stars.TryGetValue(id, out var at) ? at : null;

    public int? Rating(string id) => _ratings.TryGetValue(id, out var v) ? v : null;

    public PlayInfo Plays(string songId) => _plays.GetValueOrDefault(songId);

    /// <summary>Album play count (sum of its songs) and last play (latest of its songs).</summary>
    public PlayInfo Plays(CatalogAlbum album)
    {
        var count = 0;
        DateTimeOffset? last = null;
        foreach (var song in album.Songs)
        {
            if (!_plays.TryGetValue(song.Id, out var p)) continue;
            count += p.Count;
            if (p.LastPlayed > last || last == null) last = p.LastPlayed;
        }

        return new PlayInfo(count, last);
    }

    /// <summary>Starred item IDs, most recently starred first.</summary>
    public IEnumerable<string> StarredIds =>
        _stars.OrderByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal).Select(s => s.Key);
}

/// <summary>
/// Stars, ratings and play stats of the single user, keyed by public catalog IDs. Rows of items that
/// are not in the library are kept, so they come back with the file. Reads use a cached snapshot that
/// is reloaded after every write.
/// </summary>
public sealed class UserDataStore(IDbContextFactory<AppDbContext> dbFactory)
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>One write at a time (two first plays of a song would both insert its row).</summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _version = 1;
    private volatile UserDataSnapshot? _snapshot;

    public async Task<UserDataSnapshot> GetAsync(CancellationToken ct = default)
    {
        var current = _snapshot;
        if (current != null && current.Version == Interlocked.Read(ref _version)) return current;

        await _lock.WaitAsync(ct);
        try
        {
            // Read the version before loading: a write during the load triggers the next reload.
            var version = Interlocked.Read(ref _version);
            current = _snapshot;
            if (current != null && current.Version == version) return current;

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            current = new UserDataSnapshot(version,
                await db.Stars.AsNoTracking().ToListAsync(ct),
                await db.Ratings.AsNoTracking().ToListAsync(ct),
                await db.PlayStats.AsNoTracking().ToListAsync(ct));
            _snapshot = current;
            return current;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Stars the items; items that are already starred keep their date.</summary>
    public Task StarAsync(IEnumerable<string> ids, CancellationToken ct = default) => WriteAsync(async db =>
    {
        var wanted = ids.Distinct(StringComparer.Ordinal).ToList();
        var existing = await db.Stars.Where(s => wanted.Contains(s.ItemId)).Select(s => s.ItemId).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        db.Stars.AddRange(wanted.Except(existing, StringComparer.Ordinal).Select(id => new Star { ItemId = id, StarredAt = now }));
        await db.SaveChangesAsync(ct);
    }, ct);

    public Task UnstarAsync(IEnumerable<string> ids, CancellationToken ct = default) => WriteAsync(async db =>
    {
        var list = ids.Distinct(StringComparer.Ordinal).ToList();
        await db.Stars.Where(s => list.Contains(s.ItemId)).ExecuteDeleteAsync(ct);
    }, ct);

    /// <summary>Sets the rating (1..5); 0 removes it.</summary>
    public Task SetRatingAsync(string id, int rating, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rating);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rating, 5);
        return WriteAsync(async db =>
        {
            var row = await db.Ratings.FirstOrDefaultAsync(r => r.ItemId == id, ct);
            if (rating == 0)
            {
                if (row != null) db.Ratings.Remove(row);
            }
            else if (row == null)
            {
                db.Ratings.Add(new Rating { ItemId = id, Value = rating });
            }
            else
            {
                row.Value = rating;
            }

            await db.SaveChangesAsync(ct);
        }, ct);
    }

    /// <summary>Counts one play per entry; the last play is the latest time seen (scrobbles may arrive late).</summary>
    public Task ScrobbleAsync(IReadOnlyList<(string SongId, DateTimeOffset PlayedAt)> plays, CancellationToken ct = default) => WriteAsync(async db =>
    {
        var ids = plays.Select(p => p.SongId).Distinct(StringComparer.Ordinal).ToList();
        var rows = await db.PlayStats.Where(p => ids.Contains(p.SongId)).ToDictionaryAsync(p => p.SongId, StringComparer.Ordinal, ct);
        foreach (var (songId, playedAt) in plays)
        {
            if (!rows.TryGetValue(songId, out var row))
            {
                row = new PlayStat { SongId = songId };
                rows[songId] = row;
                db.PlayStats.Add(row);
            }

            row.PlayCount++;
            if (row.LastPlayed == null || playedAt > row.LastPlayed) row.LastPlayed = playedAt;
        }

        await db.SaveChangesAsync(ct);
    }, ct);

    private async Task WriteAsync(Func<AppDbContext, Task> write, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await write(db);
        }
        finally
        {
            // Also after a failure: the write may have been partly applied.
            Interlocked.Increment(ref _version);
            _writeLock.Release();
        }
    }
}
