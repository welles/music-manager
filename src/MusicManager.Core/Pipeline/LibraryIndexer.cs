using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    ILogger<LibraryIndexer> logger) : BackgroundService
{
    /// <summary>Raise when <see cref="Fill"/> reads more or different data, so existing entries are re-read.</summary>
    public const int CurrentIndexVersion = 3;

    private readonly SemaphoreSlim _trigger = new(0, 1);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _scanning;
    private string? _activity;
    private DateTimeOffset? _lastScan;

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

    private async Task ScanAsync(CancellationToken ct)
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
                    entry = new LibraryEntry { RelativePath = relative };
                    db.Library.Add(entry);
                    existing[relative] = entry;
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
            db.Library.RemoveRange(removed);
            await db.SaveChangesAsync(ct);
            _lastScan = DateTimeOffset.UtcNow;
            if (added + updated + removed.Count > 0)
                logger.LogInformation("Library index: {Added} added, {Updated} updated, {Removed} removed", added, updated, removed.Count);
        }
        finally
        {
            _scanning = false;
            _activity = null;
            _lock.Release();
            notifier.Notify();
        }
    }

    private static void Fill(LibraryEntry entry, string path, FileInfo info, bool resetFingerprint = true)
    {
        var (tags, duration, bitrate, codec) = TagIo.ReadForIndex(path);
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
        entry.TagSource = tags.Extra.GetValueOrDefault(TagIo.SourceMarker);

        // Multi-value fields from the raw tags (names differ between M4A, Vorbis and ID3).
        var fields = TagFields.Read(path);
        entry.Artists = TagFields.Get(fields, "ARTIST");
        entry.AlbumArtists = TagFields.Get(fields, "ALBUMARTIST", "ALBUM ARTIST");
        entry.Genres = TagFields.Get(fields, "GENRE");
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

    /// <summary>Adds or updates one file right after the pipeline filed it.</summary>
    public async Task UpsertAsync(string relativePath, uint[]? fingerprint, CancellationToken ct)
    {
        var path = storage.LibraryPath(relativePath);
        if (!File.Exists(path)) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entry = await db.Library.FirstOrDefaultAsync(e => e.RelativePath == relativePath, ct);
        if (entry == null)
        {
            entry = new LibraryEntry { RelativePath = relativePath };
            db.Library.Add(entry);
        }

        Fill(entry, path, new FileInfo(path));
        if (fingerprint is { Length: > 0 })
            entry.Fingerprint = FingerprintMatcher.Encode(fingerprint);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Removes an entry (file moved to the trash).</summary>
    public async Task RemoveAsync(string relativePath, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Library.Where(e => e.RelativePath == relativePath).ExecuteDeleteAsync(ct);
    }

    public override void Dispose()
    {
        _trigger.Dispose();
        _lock.Dispose();
        base.Dispose();
    }
}
