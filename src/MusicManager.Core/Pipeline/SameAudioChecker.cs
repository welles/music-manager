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
                m.AudioSimilarity = null;
                m.AudioWeakest = null;
                m.AudioLengthGap = null;
                m.AudioCheck = fresh is not { Values.Length: > 0 } ? "Audio-Vergleich nicht möglich: neue Datei nicht lesbar"
                    : "Audio-Vergleich nicht möglich: vorhandene Datei nicht lesbar";
                continue;
            }

            var comparison = FingerprintMatcher.Compare(fresh.Value.Values, existing.Value.Values);
            (m.SameAudio, m.AudioCheck) = Judge(comparison, fresh.Value.Duration, existing.Value.Duration);
            m.AudioSimilarity = comparison.Overall;
            m.AudioWeakest = comparison.Weakest;
            m.AudioLengthGap = Math.Abs(fresh.Value.Duration - existing.Value.Duration);
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

    /// <summary>Not compared yet, or compared before the numbers were kept (only the verdict and its text).</summary>
    public static bool NeedsCheck(TrackFile f) =>
        f.Duplicates.Any(d => d.Kind == DuplicateKind.Duplicate && (d.AudioCheck == null || d.SameAudio != null && (d.AudioSimilarity == null || d.AudioWeakest == null || d.AudioLengthGap == null)));

    /// <summary>Default of the "discard duplicates from … %" button.</summary>
    public const int DefaultDiscardPercent = 98;

    /// <summary>Similarity in whole percent, rounded like it is shown (0.996 → 100).</summary>
    public static int Percent(double similarity) => (int)Math.Round(similarity * 100, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The library file most similar to the new file by the whole-track audio comparison, among the matches that only
    /// differ evenly: no 10-second section below <see cref="SectionThreshold"/> (a commentary, a DJ tag, a bleeped word stays
    /// for review however high the average) and the same length (± <see cref="DurationTolerance"/>, measured like the
    /// verdict: a cut or longer edit is another version).
    /// </summary>
    public static DuplicateMatch? MostSimilar(TrackFile f) =>
        f.Duplicates
            .Where(d => d is { Kind: DuplicateKind.Duplicate, AudioSimilarity: not null, AudioWeakest: >= SectionThreshold, AudioLengthGap: <= DurationTolerance })
            .MaxBy(d => d.AudioSimilarity);

    /// <summary>A library file is at least <paramref name="percent"/> % the same audio as the new file, evenly and as long (see <see cref="MostSimilar"/>).</summary>
    public static bool IsAtLeast(TrackFile f, int percent) => MostSimilar(f) is { AudioSimilarity: { } s } && Percent(s) >= percent;

    /// <summary>
    /// New file that is discarded without asking: 100 % the same audio as a library file, judged the same audio as well (no
    /// section that sounds different, same length), from the same release, and not of better quality – a lossless or
    /// clearly higher-bitrate copy waits in the duplicate view, where "keep new" can replace the library file with it.
    /// </summary>
    public static DuplicateMatch? IdenticalMatch(TrackFile f) =>
        f.Duplicates.Where(d => d is { Kind: DuplicateKind.Duplicate, SameAudio: true, AudioSimilarity: { } s } && Percent(s) >= 100
                                && IsSameRelease(f, d) && !IsBetterQuality(f, d))
            .MaxBy(d => d.AudioSimilarity);

    /// <summary>
    /// The library file is the same track of the same release: same target path, or the same MusicBrainz release – the album
    /// name only counts where a release ID is missing. The same
    /// recording on another release (the single of an album track, a compilation) waits in the duplicate view, so an
    /// album is not left with a gap without asking.
    /// </summary>
    public static bool IsSameRelease(TrackFile f, DuplicateMatch d)
    {
        static bool Same(string? a, string? b) =>
            !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

        if (Same(f.TargetPath, d.RelativePath)) return true;
        // Both release IDs known: they decide (a deluxe edition has the same album name as the standard one).
        if (!string.IsNullOrWhiteSpace(f.FinalTags?.MbReleaseId) && !string.IsNullOrWhiteSpace(d.MbReleaseId))
            return Same(f.FinalTags.MbReleaseId, d.MbReleaseId);
        return Same(f.FinalTags?.Album, d.Album);
    }

    private static readonly HashSet<string> LosslessCodecs = new(StringComparer.OrdinalIgnoreCase)
        { "flac", "alac", "wav", "aiff", "ape", "wavpack", "tta", "truehd", "mlp" };

    private static bool IsLossless(string? codec) =>
        codec != null && (LosslessCodecs.Contains(codec) || codec.StartsWith("pcm", StringComparison.OrdinalIgnoreCase));

    /// <summary>The new file is lossless while the library file is not, or both are lossy and the new one has a clearly higher bitrate.</summary>
    public static bool IsBetterQuality(TrackFile f, DuplicateMatch d)
    {
        if (IsLossless(f.Codec)) return !IsLossless(d.Codec);
        if (IsLossless(d.Codec)) return false;
        return f.BitrateKbps is { } a && d.BitrateKbps is { } b && a > b * 1.1;
    }

    /// <summary>All duplicate matches of a file are the same audio: the new file can go without a listen.</summary>
    public static bool IsSafeToDiscard(TrackFile f) =>
        f.Duplicates.Where(d => d.Kind == DuplicateKind.Duplicate).ToList() is { Count: > 0 } d && d.All(m => m.SameAudio == true);
}
