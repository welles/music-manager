using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MusicManager.Core.Data;
using MusicManager.Core.Rules;
using MusicManager.Core.Services;

namespace MusicManager.Core.Pipeline;

/// <summary>Progress of the check of duplicates that were found before the audio comparison existed.</summary>
public sealed record SameAudioState(bool Running, int Done, int Total);

/// <summary>
/// Is a possible duplicate the same audio as the library file, only encoded differently? Compares both whole tracks by
/// their Chromaprint fingerprints: a re-encode matches closely everywhere, while a version with something laid over the
/// music (a commentary track, another mix) differs at least in some 10-second sections. Runs for every new duplicate,
/// and once after the start for open duplicates that were never compared.
/// </summary>
public sealed class SameAudioChecker(
    IDbContextFactory<AppDbContext> dbFactory,
    AudioTools audio,
    Storage storage,
    PipelineNotifier notifier,
    ILogger<SameAudioChecker> logger) : BackgroundService
{
    /// <summary>
    /// Re-encodes measured ≥ 0.98 over the whole track; a quiet voice over the music still averaged 0.95. Of 491 real
    /// duplicates, 423 passed (lowest 0.970); remasters sat evenly around 0.96.
    /// </summary>
    public const double OverallThreshold = 0.97;

    /// <summary>Re-encodes measured ≥ 0.975 in every 10-second section; a quiet voice pulled sections down to 0.87.</summary>
    public const double SectionThreshold = 0.93;

    /// <summary>Same audio has the same length; a few seconds of extra silence or a cut fade-out are another edit.</summary>
    public const double DurationTolerance = 2;

    public SameAudioState State { get; private set; } = new(false, 0, 0);

    /// <summary>Judges a comparison: same audio, and in words what was found.</summary>
    public static (bool Same, string Text) Judge(AudioComparison c, double durationNew, double durationExisting)
    {
        var gap = Math.Abs(durationNew - durationExisting);
        if (gap > DurationTolerance)
            return (false, $"Klingt anders: die Länge weicht um {gap:0} s ab");
        if (c.Weakest < SectionThreshold)
            return (false, $"Klingt anders: ab {c.WeakestAt:m\\:ss} nur {c.Weakest:P0} gleich (insgesamt {c.Overall:P0}) – " +
                           "z. B. Kommentar oder andere Abmischung");
        // Evenly a little off everywhere: the same take, but mastered or sourced differently – worth a listen.
        if (c.Overall < OverallThreshold)
            return (false, $"Fast gleich: überall ähnlich, aber nur {c.Overall:P0} gleich – wohl anderes Mastering oder andere Quelle");
        return (true, $"Gleiche Aufnahme: {c.Overall:P0} gleich über die ganze Länge, schwächster Abschnitt {c.Weakest:P0}");
    }

    /// <summary>Compares the new file with every <see cref="DuplicateKind.Duplicate"/> match and returns the matches with the result.</summary>
    public async Task<List<DuplicateMatch>> CheckAsync(string newPath, List<DuplicateMatch> matches, CancellationToken ct)
    {
        var duplicates = matches.Where(m => m.Kind == DuplicateKind.Duplicate).ToList();
        if (duplicates.Count == 0) return matches;

        var fresh = File.Exists(newPath) ? await audio.RawFingerprintAsync(newPath, ct, wholeTrack: true) : null;
        foreach (var m in duplicates)
        {
            var existingPath = storage.LibraryPath(m.RelativePath);
            var existing = File.Exists(existingPath) ? await audio.RawFingerprintAsync(existingPath, ct, wholeTrack: true) : null;
            if (fresh is not { Values.Length: > 0 } || existing is not { Values.Length: > 0 })
            {
                m.SameAudio = null;
                m.AudioCheck = fresh == null ? "Audio-Vergleich nicht möglich: neue Datei nicht lesbar"
                    : "Audio-Vergleich nicht möglich: vorhandene Datei nicht lesbar";
                continue;
            }

            (m.SameAudio, m.AudioCheck) = Judge(FingerprintMatcher.Compare(fresh.Value.Values, existing.Value.Values),
                fresh.Value.Duration, existing.Value.Duration);
        }

        // A new list, so the JSON column is seen as changed.
        return matches.ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            // Let the start (index, catalog) settle first.
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            await CheckOpenAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Audio comparison of open duplicates failed");
        }
        finally
        {
            State = State with { Running = false };
            notifier.Notify();
        }
    }

    /// <summary>Compares every open duplicate that was not compared yet (found before this check existed).</summary>
    public async Task CheckOpenAsync(CancellationToken ct)
    {
        List<int> ids;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            ids = (await db.Files.AsNoTracking().Where(f => f.Status == FileStatus.Duplicate).ToListAsync(ct))
                .Where(NeedsCheck).Select(f => f.Id).ToList();
        }

        if (ids.Count == 0) return;
        logger.LogInformation("Comparing the audio of {Count} open duplicates", ids.Count);
        State = new SameAudioState(true, 0, ids.Count);
        notifier.Notify();

        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var file = await db.Files.FirstOrDefaultAsync(f => f.Id == id, ct);
                if (file != null && file.Status == FileStatus.Duplicate && NeedsCheck(file))
                {
                    var checkedMatches = await CheckAsync(storage.InboxPath(file.RelativePath), file.Duplicates, ct);

                    // Decided while comparing? Then the result is no longer needed.
                    await db.Entry(file).ReloadAsync(ct);
                    if (file.Status == FileStatus.Duplicate)
                    {
                        file.Duplicates = checkedMatches;
                        await db.SaveChangesAsync(ct);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Audio comparison failed for file {Id}", id);
            }

            State = State with { Done = State.Done + 1 };
            notifier.Notify();
        }

        logger.LogInformation("Audio comparison of open duplicates finished");
    }

    private static bool NeedsCheck(TrackFile f) =>
        f.Duplicates.Any(d => d.Kind == DuplicateKind.Duplicate && d.AudioCheck == null);

    /// <summary>All duplicate matches of a file are the same audio: the new file can go without a listen.</summary>
    public static bool IsSafeToDiscard(TrackFile f) =>
        f.Duplicates.Where(d => d.Kind == DuplicateKind.Duplicate).ToList() is { Count: > 0 } d && d.All(m => m.SameAudio == true);
}
