using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MusicManager.Core.Catalog;
using MusicManager.Core.Data;
using MusicManager.Core.Rules;
using MusicManager.Core.Services;

namespace MusicManager.Core.Pipeline;

public sealed record LibraryIndexStatus(bool Scanning, int Files, int Fingerprinted, int FingerprintPending, DateTimeOffset? LastScan, string? Activity);

/// <summary>
/// Index of the music library for duplicate detection: tags, MusicBrainz IDs, audio properties and
/// raw fingerprints. Built on first start, then kept up to date incrementally (size/mtime), so
/// changes made outside the tool are picked up as well.
/// </summary>
public sealed class LibraryIndexer(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settingsService,
    Storage storage,
    AudioTools audio,
    PipelineNotifier notifier,
    UserDataStore userData,
    ILogger<LibraryIndexer> logger) : BackgroundService
{
    /// <summary>Raise when <see cref="Fill"/> reads more or different data, so existing entries are re-read.</summary>
    public const int CurrentIndexVersion = 5;

    private readonly SemaphoreSlim _trigger = new(0, 1);
    private long _version;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _scanning;
    private string? _activity;
    private DateTimeOffset? _lastScan;

    /// <summary>
    /// Increases with every change of the index (scan, upsert, remove); lets caches such as
    /// <see cref="Catalog.MediaCatalog"/> detect that they are stale.
    /// </summary>
    public long Version => Interlocked.Read(ref _version);

    public bool Scanning => _scanning;

    /// <summary>Raised after the index changed (scan, upsert, remove).</summary>
    public event Action? IndexChanged;

    internal void OnChanged()
    {
        Interlocked.Increment(ref _version);
        IndexChanged?.Invoke();
    }

    public async Task<LibraryIndexStatus> GetStatusAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var files = await db.Library.CountAsync(ct);
        var fingerprinted = await db.Library.CountAsync(e => e.Fingerprint != null, ct);
        var pending = await db.Library.CountAsync(e => e.Fingerprint == null && !e.FingerprintFailed, ct);
        return new LibraryIndexStatus(_scanning, files, fingerprinted, pending, _lastScan, _activity);
    }

    /// <summary>Starts a scan now (instead of waiting for the interval).</summary>
    public void ScanNow()
    {
        if (_trigger.CurrentCount == 0) _trigger.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app start up first.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = await settingsService.GetAsync(stoppingToken);
            try
            {
                await ScanAsync(stoppingToken);
                if (settings.LibraryFingerprinting)
                    await FingerprintPassAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Library scan failed");
            }

            try
            {
                await _trigger.WaitAsync(TimeSpan.FromMinutes(Math.Max(1, settings.LibraryScanIntervalMinutes)), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal async Task ScanAsync(CancellationToken ct)
    {
        if (!Directory.Exists(storage.LibraryRoot))
        {
            logger.LogWarning("Library {Path} does not exist", storage.LibraryRoot);
            return;
        }

        await _lock.WaitAsync(ct);
        _scanning = true;
        try
        {
            var settings = await settingsService.GetAsync(ct);
            var extensions = settings.AudioExtensions.Select(e => e.ToLowerInvariant()).ToHashSet();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var existing = await db.Library.ToDictionaryAsync(e => e.RelativePath, StringComparer.Ordinal, ct);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var newEntries = new List<LibraryEntry>();
            // First build of the index: the files were there before, so their mtime is the best "added" date.
            var initialBuild = existing.Count == 0;
            int added = 0, updated = 0, count = 0;

            foreach (var path in Directory.EnumerateFiles(storage.LibraryRoot, "*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden,
                     }))
            {
                ct.ThrowIfCancellationRequested();
                if (!extensions.Contains(Path.GetExtension(path).ToLowerInvariant())) continue;

                var relative = Path.GetRelativePath(storage.LibraryRoot, path);
                seen.Add(relative);
                var info = new FileInfo(path);
                var unchanged = existing.TryGetValue(relative, out var entry) && entry.FileSize == info.Length && entry.FileModifiedUtc == info.LastWriteTimeUtc;
                if (unchanged && entry!.IndexVersion >= CurrentIndexVersion)
                    continue;

                if (entry == null)
                {
                    entry = new LibraryEntry { RelativePath = relative, AddedAt = initialBuild ? null : DateTimeOffset.UtcNow };
                    db.Library.Add(entry);
                    existing[relative] = entry;
                    newEntries.Add(entry);
                    added++;
                }
                else
                {
                    updated++;
                }

                Fill(entry, path, info, resetFingerprint: !unchanged);
                if (++count % 200 == 0)
                {
                    _activity = $"Bibliothek: {count} Dateien eingelesen";
                    notifier.Notify();
                    await db.SaveChangesAsync(ct);
                }
            }

            var removed = existing.Values.Where(e => !seen.Contains(e.RelativePath)).ToList();
            var moved = CarryOverMoved(removed, newEntries);

            DropDuplicateSongIds(existing.Values.Where(e => seen.Contains(e.RelativePath)));

            // Removed rows go first, so carried-over song IDs do not collide with the unique index.
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                db.Library.RemoveRange(removed);
                await db.SaveChangesAsync(ct);
                var assigned = AssignSongIds(existing.Values.Where(e => seen.Contains(e.RelativePath)), []);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                if (added + updated + removed.Count + assigned > 0) OnChanged();
            }

            await RestoreUserDataAsync(existing.Values, ct);

            _lastScan = DateTimeOffset.UtcNow;
            if (added + updated + removed.Count > 0)
                logger.LogInformation("Library index: {Added} added, {Updated} updated, {Removed} removed ({Moved} moved)", added, updated,
                    removed.Count, moved);
        }
        finally
        {
            _scanning = false;
            _activity = null;
            _lock.Release();
            notifier.Notify();
        }
    }

    /// <summary>
    /// Files that vanished and appeared in the same scan (moved or renamed outside the tool) keep their
    /// song ID and added date. Only unambiguous pairs count: same release track ID, or else same
    /// recording and release ID, with exactly one candidate on each side.
    /// </summary>
    internal static int CarryOverMoved(IReadOnlyList<LibraryEntry> removed, IReadOnlyList<LibraryEntry> added)
    {
        var moved = 0;
        var gone = removed.Where(e => e.SongId != null).ToList();
        if (gone.Count == 0 || added.Count == 0) return 0;

        Func<LibraryEntry, string?>[] keys =
        [
            e => e.MbTrackId,
            e => e.MbRecordingId != null && e.MbReleaseId != null ? $"{e.MbRecordingId}|{e.MbReleaseId}" : null,
        ];
        foreach (var key in keys)
        {
            var oldByKey = gone.Where(e => key(e) != null).GroupBy(e => key(e)!).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
            var newByKey = added.Where(e => e.SongId == null && key(e) != null).GroupBy(e => key(e)!).Where(g => g.Count() == 1);
            foreach (var g in newByKey)
            {
                if (!oldByKey.TryGetValue(g.Key, out var old)) continue;
                var entry = g.Single();
                entry.SongId = old.SongId;
                entry.AddedAt = old.AddedAt ?? entry.AddedAt;
                gone.Remove(old);
                moved++;
            }
        }

        return moved;
    }

    /// <summary>
    /// Gives every entry without one a song ID: "tr-" + the release track ID if no other entry uses it,
    /// otherwise a random one. Entries without an added date get their file's mtime (existing rows).
    /// </summary>
    /// <summary>
    /// A song ID taken from a file can be in use already (the file was copied): the entry that had it first
    /// keeps it, the others get a new one (and their file is rewritten by <see cref="FileMarkerSync"/>).
    /// </summary>
    internal static void DropDuplicateSongIds(IEnumerable<LibraryEntry> entries)
    {
        foreach (var group in entries.Where(e => e.SongId != null).GroupBy(e => e.SongId!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            foreach (var e in group.OrderBy(e => e.RestoredFromFile != null).ThenBy(e => e.Id == 0 ? int.MaxValue : e.Id)
                         .ThenBy(e => e.RelativePath, StringComparer.Ordinal).Skip(1))
            {
                e.SongId = null;
                e.RestoredFromFile = null;
            }
        }
    }

    /// <summary>Favorites and ratings of songs that took their identity from the file come back from the file.</summary>
    private async Task RestoreUserDataAsync(IEnumerable<LibraryEntry> entries, CancellationToken ct)
    {
        var restored = entries.Where(e => e is { SongId: not null, RestoredFromFile: not null }).ToList();
        if (restored.Count == 0) return;
        var count = await userData.RestoreAsync(restored.Select(e => (e.SongId!, e.RestoredFromFile!.Favorite, e.RestoredFromFile.Rating)).ToList(), ct);
        if (count > 0) logger.LogInformation("Restored {Count} favorites/ratings from the files", count);
        foreach (var e in restored) e.RestoredFromFile = null;
    }

    internal static int AssignSongIds(IEnumerable<LibraryEntry> entries, IEnumerable<string> usedElsewhere)
    {
        var list = entries.ToList();
        var used = new HashSet<string>(list.Select(e => e.SongId).OfType<string>().Concat(usedElsewhere), StringComparer.OrdinalIgnoreCase);
        var assigned = 0;
        // Stable order, so the same entry wins a contested release track ID on every installation.
        foreach (var entry in list.Where(e => e.SongId == null).OrderBy(e => e.RelativePath, StringComparer.Ordinal))
        {
            var id = entry.MbTrackId is { Length: > 0 } t ? $"{CatalogIds.SongPrefix}{t.ToLowerInvariant()}" : null;
            if (id == null || used.Contains(id)) id = CatalogIds.NewSongId();
            used.Add(id);
            entry.SongId = id;
            entry.AddedAt ??= new DateTimeOffset(DateTime.SpecifyKind(entry.FileModifiedUtc, DateTimeKind.Utc));
            assigned++;
        }

        return assigned;
    }

    private static void Fill(LibraryEntry entry, string path, FileInfo info, bool resetFingerprint = true)
    {
        var (tags, duration, bitrate, codec, compilation) = TagIo.ReadForIndex(path);
        entry.FileSize = info.Length;
        entry.FileModifiedUtc = info.LastWriteTimeUtc;
        entry.DurationSeconds = duration;
        entry.BitrateKbps = bitrate;
        entry.Codec = codec;
        entry.Title = tags.Title;
        entry.Artist = tags.Artist;
        entry.Album = tags.Album;
        entry.AlbumArtist = tags.AlbumArtist;
        entry.Date = tags.Date;
        entry.MbRecordingId = tags.MbRecordingId;
        entry.MbReleaseId = tags.MbReleaseId;
        entry.AcoustId = tags.AcoustId;
        entry.MbTrackId = tags.MbTrackId.NullIfEmpty();
        entry.OriginalDate = tags.OriginalDate.NullIfEmpty();
        entry.Isrc = tags.Isrc.NullIfEmpty();
        entry.ReleaseTypes = (tags.ReleaseType ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        entry.IsCompilation = compilation;
        entry.TagSource = tags.Extra.GetValueOrDefault(TagIo.SourceMarker);

        // Multi-value fields from the raw tags (names differ between M4A, Vorbis and ID3).
        var fields = TagFields.Read(path);
        entry.Artists = TagFields.Get(fields, "ARTIST");
        entry.AlbumArtists = TagFields.Get(fields, "ALBUMARTIST", "ALBUM ARTIST");
        entry.Genres = TagFields.Get(fields, "GENRE");
        entry.GenreOrigin = TagFields.Get(fields, Model.GenreOrigin.Marker).FirstOrDefault();

        // Identity and user data mirrored into the file: a song the index does not know yet (rebuilt database,
        // file moved outside the tool) takes its song ID and added date from the file.
        var markers = Model.FileMarkers.FromTags(name => TagFields.Get(fields, name).FirstOrDefault());
        entry.MarkersInFile = markers.Canonical();
        if (entry.SongId == null && markers.SongId?.StartsWith(CatalogIds.SongPrefix, StringComparison.Ordinal) == true)
        {
            entry.SongId = markers.SongId;
            entry.AddedAt = markers.AddedAt ?? entry.AddedAt;
            entry.RestoredFromFile = markers;
        }
        entry.MbArtistIds = TagFields.Get(fields, "MusicBrainz Artist Id", "MUSICBRAINZ_ARTISTID");
        entry.MbAlbumArtistIds = TagFields.Get(fields, "MusicBrainz Album Artist Id", "MUSICBRAINZ_ALBUMARTISTID");
        entry.MbReleaseGroupId = TagFields.Get(fields, "MusicBrainz Release Group Id", "MUSICBRAINZ_RELEASEGROUPID").FirstOrDefault();
        entry.RulesVersion = TagFields.Get(fields, TagIo.RulesMarker).FirstOrDefault();
        entry.TagSource ??= TagFields.Get(fields, TagIo.SourceMarker).FirstOrDefault();
        entry.HasCover = fields.Any(x => x.Name == "COVER") || TagFields.Get(fields, "METADATA_BLOCK_PICTURE").Count > 0;
        entry.TrackNumber = tags.TrackNumber;
        entry.DiscNumber = tags.DiscNumber;
        entry.Year = tags.Year;
        if (resetFingerprint)
        {
            entry.Fingerprint = null;
            entry.FingerprintFailed = false;
        }

        entry.IndexVersion = CurrentIndexVersion;
        entry.IndexedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Computes missing fingerprints, one file at a time (yields to new scans).</summary>
    private async Task FingerprintPassAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _trigger.CurrentCount == 0)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var batch = await db.Library.Where(e => e.Fingerprint == null && !e.FingerprintFailed).OrderBy(e => e.Id).Take(20).ToListAsync(ct);
            if (batch.Count == 0) return;

            var remaining = await db.Library.CountAsync(e => e.Fingerprint == null && !e.FingerprintFailed, ct);
            _activity = $"Fingerabdrücke der Bibliothek: noch {remaining}";
            notifier.Notify();
            foreach (var entry in batch)
            {
                var fp = await audio.RawFingerprintAsync(storage.LibraryPath(entry.RelativePath), ct);
                if (fp == null)
                    entry.FingerprintFailed = true;
                else
                    entry.Fingerprint = FingerprintMatcher.Encode(fp.Value.Values);
            }

            await db.SaveChangesAsync(ct);
        }

        _activity = null;
        notifier.Notify();
    }

    /// <summary>
    /// Adds or updates one file right after the pipeline filed it. <paramref name="replaces"/> is the
    /// library path of a file this one replaces or was moved from: its entry is removed, and its song
    /// ID and added date pass to this file.
    /// </summary>
    public async Task UpsertAsync(string relativePath, uint[]? fingerprint, CancellationToken ct, string? replaces = null)
    {
        var path = storage.LibraryPath(relativePath);
        if (!File.Exists(path)) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var entry = await db.Library.FirstOrDefaultAsync(e => e.RelativePath == relativePath, ct);

        LibraryEntry? old = null;
        if (replaces != null && replaces != relativePath)
        {
            old = await db.Library.FirstOrDefaultAsync(e => e.RelativePath == replaces, ct);
            if (old != null)
            {
                db.Library.Remove(old);
                await db.SaveChangesAsync(ct);
            }
        }

        if (entry == null)
        {
            entry = new LibraryEntry { RelativePath = relativePath, SongId = old?.SongId, AddedAt = old?.AddedAt ?? DateTimeOffset.UtcNow };
            db.Library.Add(entry);
        }
        else if (old?.SongId != null)
        {
            // The file at the target path is replaced as well; the moved/replacing identity wins.
            entry.SongId = old.SongId;
            entry.AddedAt = old.AddedAt ?? entry.AddedAt;
        }

        Fill(entry, path, new FileInfo(path));
        if (fingerprint is { Length: > 0 })
            entry.Fingerprint = FingerprintMatcher.Encode(fingerprint);
        // A song ID from the file that another entry already has (copied file) is not taken over.
        if (entry.RestoredFromFile != null && await db.Library.AnyAsync(e => e.SongId == entry.SongId && e.Id != entry.Id, ct))
        {
            entry.SongId = null;
            entry.RestoredFromFile = null;
        }

        if (entry.SongId == null)
        {
            var mbId = entry.MbTrackId == null ? null : $"{CatalogIds.SongPrefix}{entry.MbTrackId.ToLowerInvariant()}";
            var taken = mbId != null && await db.Library.AnyAsync(e => e.SongId == mbId && e.Id != entry.Id, ct);
            AssignSongIds([entry], taken ? [mbId!] : []);
        }

        var restore = entry;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        OnChanged();
        await RestoreUserDataAsync([restore], ct);
    }

    /// <summary>Removes an entry (file moved to the trash).</summary>
    public async Task RemoveAsync(string relativePath, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Library.Where(e => e.RelativePath == relativePath).ExecuteDeleteAsync(ct) > 0)
            OnChanged();
    }

    public override void Dispose()
    {
        _trigger.Dispose();
        _lock.Dispose();
        base.Dispose();
    }
}
