using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MusicManager.Core.Data;
using MusicManager.Core.Model;
using MusicManager.Core.Rules;

namespace MusicManager.Core.Pipeline;

public sealed class LibraryDuplicateGroup
{
    public required string Key { get; init; }
    public required List<LibraryEntry> Entries { get; init; }
    public required List<string> Reasons { get; init; }
}

public sealed record LibraryDuplicateScanState(bool Running, int Progress, int Total, DateTimeOffset? Finished, int Groups);

/// <summary>
/// Library check: finds groups of library files that are the same recording, with the same rules as
/// for new files (MusicBrainz ID, fingerprint, artist/title + duration; version markers separate).
/// </summary>
public sealed class LibraryDuplicateScanner(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settingsService,
    Storage storage,
    LibraryIndexer libraryIndexer,
    PipelineNotifier notifier,
    ILogger<LibraryDuplicateScanner> logger)
{
    private const double DurationWindow = 8;

    /// <summary>Quick pre-check on the first values without alignment search.</summary>
    private const int QuickValues = 150;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _progress, _total;
    private bool _running;
    private DateTimeOffset? _finished;

    public List<LibraryDuplicateGroup> Groups { get; private set; } = [];

    public LibraryDuplicateScanState State => new(_running, _progress, _total, _finished, Groups.Count);

    public async Task ScanAsync(CancellationToken ct)
    {
        if (!await _lock.WaitAsync(0, ct)) return;
        _running = true;
        try
        {
            var settings = await settingsService.GetAsync(ct);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entries = await db.Library.AsNoTracking().OrderBy(e => e.DurationSeconds).ToListAsync(ct);
            var dismissed = (await db.DismissedDuplicates.Select(d => d.Key).ToListAsync(ct)).ToHashSet();
            _total = entries.Count;
            _progress = 0;

            var parent = Enumerable.Range(0, entries.Count).ToArray();
            int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
            var reasons = new Dictionary<(int, int), string>();

            void Union(int i, int j, string reason)
            {
                reasons.TryAdd((Math.Min(i, j), Math.Max(i, j)), reason);
                parent[Find(i)] = Find(j);
            }

            // 1. Same MusicBrainz recording.
            foreach (var g in entries.Select((e, i) => (e, i)).Where(x => !string.IsNullOrWhiteSpace(x.e.MbRecordingId))
                         .GroupBy(x => x.e.MbRecordingId).Where(g => g.Count() > 1))
            {
                var first = g.First();
                foreach (var other in g.Skip(1))
                {
                    if (DuplicateDetector.VersionMarkers(first.e.Title).SetEquals(DuplicateDetector.VersionMarkers(other.e.Title)))
                        Union(first.i, other.i, "Gleiche MusicBrainz-Recording-ID");
                }
            }

            // 2./3. Fingerprint and artist/title within a sliding duration window.
            var fingerprints = entries.Select(e => FingerprintMatcher.Decode(e.Fingerprint)).ToArray();
            for (var i = 0; i < entries.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                _progress = i + 1;
                if (i % 200 == 0) notifier.Notify();
                var a = entries[i];
                if (a.DurationSeconds == null) continue;

                for (var j = i + 1; j < entries.Count; j++)
                {
                    var b = entries[j];
                    if (b.DurationSeconds - a.DurationSeconds > DurationWindow) break;
                    if (Find(i) == Find(j)) continue;

                    string? reason = null;
                    if (fingerprints[i].Length > 0 && fingerprints[j].Length > 0
                        && FingerprintMatcher.Similarity(fingerprints[i].Take(QuickValues).ToArray(), fingerprints[j].Take(QuickValues).ToArray(), 0) > 0.6)
                    {
                        var similarity = FingerprintMatcher.Similarity(fingerprints[i], fingerprints[j]);
                        if (similarity >= settings.DuplicateFingerprintThreshold)
                            reason = $"Ähnlicher Fingerabdruck ({similarity:P0})";
                    }

                    if (reason == null
                        && TextUtil.Similarity(a.Title, b.Title) >= 0.95
                        && TextUtil.Similarity(a.Artist, b.Artist) >= 0.8
                        && Math.Abs((a.DurationSeconds ?? 0) - (b.DurationSeconds ?? 0)) <= 5)
                    {
                        reason = "Gleicher Künstler und Titel, ähnliche Dauer";
                    }

                    if (reason == null) continue;
                    var (kind, _) = DuplicateDetector.Classify(new TagSet { Title = a.Title, MbRecordingId = a.MbRecordingId }, b,
                        fingerprintMatches: reason.StartsWith("Ähnlicher"));
                    if (kind == DuplicateKind.Duplicate)
                        Union(i, j, reason);
                }
            }

            Groups = Enumerable.Range(0, entries.Count).GroupBy(Find).Where(g => g.Count() > 1)
                .Select(g =>
                {
                    var members = g.ToList();
                    var key = string.Join("|", members.Select(i => entries[i].RelativePath).Order(StringComparer.Ordinal));
                    return new LibraryDuplicateGroup
                    {
                        Key = key,
                        Entries = members.Select(i => entries[i]).OrderBy(e => e.RelativePath).ToList(),
                        Reasons = reasons.Where(r => members.Contains(r.Key.Item1)).Select(r => r.Value).Distinct().ToList(),
                    };
                })
                .Where(g => !dismissed.Contains(g.Key))
                .OrderBy(g => g.Entries[0].Artist).ThenBy(g => g.Entries[0].Title)
                .ToList();
            _finished = DateTimeOffset.UtcNow;
            logger.LogInformation("Library duplicate check: {Groups} groups in {Files} files", Groups.Count, entries.Count);
        }
        finally
        {
            _running = false;
            _lock.Release();
            notifier.Notify();
        }
    }

    /// <summary>Keeps one file of a group; the others go to the trash.</summary>
    public async Task KeepAsync(LibraryDuplicateGroup group, int keepEntryId, CancellationToken ct)
    {
        foreach (var e in group.Entries.Where(e => e.Id != keepEntryId))
        {
            var path = storage.LibraryPath(e.RelativePath);
            if (File.Exists(path))
                storage.MoveToTrash(path, Path.Combine("bibliothek-duplikate", e.RelativePath));
            await libraryIndexer.RemoveAsync(e.RelativePath, ct);
        }

        Groups = Groups.Where(g => g.Key != group.Key).ToList();
        notifier.Notify();
    }

    /// <summary>Marks a group as "not a duplicate"; it is not shown again.</summary>
    public async Task DismissAsync(LibraryDuplicateGroup group, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.DismissedDuplicates.Add(new DismissedDuplicateGroup { Key = group.Key });
        await db.SaveChangesAsync(ct);
        Groups = Groups.Where(g => g.Key != group.Key).ToList();
        notifier.Notify();
    }
}
