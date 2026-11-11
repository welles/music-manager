using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MusicManager.Core.Data;
using MusicManager.Core.Pipeline;

namespace MusicManager.Core.Catalog;

/// <summary>
/// In-memory catalog of the library for the Subsonic API. The snapshot is rebuilt on the first
/// request after the library index changed (<see cref="LibraryIndexer.Version"/>), so requests never
/// group the whole table themselves.
/// </summary>
public sealed class MediaCatalog(IDbContextFactory<AppDbContext> dbFactory, LibraryIndexer indexer, ILogger<MediaCatalog> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile CatalogSnapshot? _snapshot;

    public async Task<CatalogSnapshot> GetAsync(CancellationToken ct = default)
    {
        var current = _snapshot;
        if (current != null && current.Version == indexer.Version) return current;

        await _lock.WaitAsync(ct);
        try
        {
            current = _snapshot;
            // Read the version before loading: a change during the load triggers the next rebuild.
            var version = indexer.Version;
            if (current != null && current.Version == version) return current;

            var started = DateTime.UtcNow;
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.Library.AsNoTracking().Where(e => e.SongId != null).Select(e => new LibraryEntry
            {
                Id = e.Id, RelativePath = e.RelativePath, FileSize = e.FileSize, FileModifiedUtc = e.FileModifiedUtc, BitrateKbps = e.BitrateKbps,
                DurationSeconds = e.DurationSeconds, Title = e.Title, Artist = e.Artist, Album = e.Album, AlbumArtist = e.AlbumArtist,
                MbRecordingId = e.MbRecordingId, MbReleaseId = e.MbReleaseId, MbTrackId = e.MbTrackId, OriginalDate = e.OriginalDate,
                ReleaseTypes = e.ReleaseTypes, IsCompilation = e.IsCompilation, Isrc = e.Isrc, SongId = e.SongId, AddedAt = e.AddedAt,
                Artists = e.Artists, AlbumArtists = e.AlbumArtists, Genres = e.Genres, TrackNumber = e.TrackNumber, DiscNumber = e.DiscNumber,
                Year = e.Year, MbArtistIds = e.MbArtistIds, MbAlbumArtistIds = e.MbAlbumArtistIds, HasCover = e.HasCover,
            }).ToListAsync(ct);
            current = CatalogSnapshot.Build(rows, version);
            _snapshot = current;
            logger.LogInformation("Catalog built: {Songs} songs, {Albums} albums, {Artists} artists in {Ms} ms", current.Songs.Count,
                current.Albums.Count, current.Artists.Count, (int)(DateTime.UtcNow - started).TotalMilliseconds);
            return current;
        }
        finally
        {
            _lock.Release();
        }
    }
}
