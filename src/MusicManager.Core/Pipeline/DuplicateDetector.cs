using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MusicManager.Core.Data;
using MusicManager.Core.Model;
using MusicManager.Core.Rules;

namespace MusicManager.Core.Pipeline;

/// <summary>
/// Finds library files that are the same recording as a new file, in this order:
/// 1. same MusicBrainz recording ID, 2. similar audio fingerprint, 3. similar artist + title with
/// similar duration. Different recordings (remix, live, remaster …) are only reported as
/// "possibly another version".
/// </summary>
public sealed partial class DuplicateDetector(IDbContextFactory<AppDbContext> dbFactory, SettingsService settingsService)
{
    /// <summary>Duration window for fingerprint and text comparisons.</summary>
    private const double DurationWindow = 8;

    public async Task<List<DuplicateMatch>> FindAsync(TagSet tags, double? duration, uint[]? fingerprint, string? targetPath,
        CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var matches = new Dictionary<int, DuplicateMatch>();

        void Add(LibraryEntry e, DuplicateKind kind, string reason, double? similarity = null)
        {
            // Keep the strongest classification per library file.
            if (matches.TryGetValue(e.Id, out var existing) && (existing.Kind == DuplicateKind.Duplicate || kind == DuplicateKind.OtherVersion))
                return;
            matches[e.Id] = Snapshot(e, kind, reason, similarity);
        }

        // 1. Same MusicBrainz recording (album vs. single release etc.).
        if (!string.IsNullOrWhiteSpace(tags.MbRecordingId))
        {
            foreach (var e in await db.Library.AsNoTracking().Where(e => e.MbRecordingId == tags.MbRecordingId).ToListAsync(ct))
                Add(e, DuplicateKind.Duplicate, "Gleiche MusicBrainz-Recording-ID");
        }

        // 2./3. Fingerprint and artist/title within a duration window.
        if (duration is { } d)
        {
            var nearby = await db.Library.AsNoTracking()
                .Where(e => e.DurationSeconds != null && e.DurationSeconds >= d - DurationWindow && e.DurationSeconds <= d + DurationWindow)
                .ToListAsync(ct);
            foreach (var e in nearby)
            {
                if (fingerprint is { Length: > 0 } && e.Fingerprint != null)
                {
                    var similarity = FingerprintMatcher.Similarity(fingerprint, FingerprintMatcher.Decode(e.Fingerprint));
                    if (similarity >= settings.DuplicateFingerprintThreshold)
                    {
                        var (kind, why) = Classify(tags, e);
                        Add(e, kind, $"Ähnlicher Fingerabdruck ({similarity:P0}){why}", similarity);
                        continue;
                    }
                }

                var titleSimilarity = TextUtil.Similarity(StripVersion(tags.Title), StripVersion(e.Title));
                var artistSimilarity = TextUtil.Similarity(tags.PrimaryArtist ?? tags.Artist, e.Artist);
                if (titleSimilarity >= 0.9 && artistSimilarity >= 0.7 && Math.Abs(d - (e.DurationSeconds ?? 0)) <= 5)
                {
                    var (kind, why) = Classify(tags, e);
                    Add(e, kind, $"Gleicher Künstler und Titel, ähnliche Dauer{why}");
                }
            }
        }

        // The target path itself is taken.
        if (targetPath != null && await db.Library.AsNoTracking().FirstOrDefaultAsync(e => e.RelativePath == targetPath, ct) is { } atTarget)
            Add(atTarget, DuplicateKind.Duplicate, "Zieldatei existiert bereits");

        return matches.Values.OrderBy(m => m.Kind).ThenByDescending(m => m.Similarity ?? 0).ToList();
    }

    /// <summary>
    /// Same recording or another version? Different MusicBrainz recordings or different version
    /// markers in the titles ("Remix", "Live", "Remastered" …) mean another version.
    /// </summary>
    public static (DuplicateKind Kind, string Reason) Classify(TagSet tags, LibraryEntry e)
    {
        if (!string.IsNullOrWhiteSpace(tags.MbRecordingId) && !string.IsNullOrWhiteSpace(e.MbRecordingId) && tags.MbRecordingId != e.MbRecordingId)
            return (DuplicateKind.OtherVersion, " – aber anderes MusicBrainz-Recording");

        var a = VersionMarkers(tags.Title);
        var b = VersionMarkers(e.Title);
        if (!a.SetEquals(b))
            return (DuplicateKind.OtherVersion, $" – aber andere Version ({Describe(a)} / {Describe(b)})");

        return (DuplicateKind.Duplicate, "");
    }

    private static string Describe(HashSet<string> markers) => markers.Count == 0 ? "Original" : string.Join(", ", markers);

    public static HashSet<string> VersionMarkers(string? title)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (title == null) return set;
        foreach (Match m in VersionWords().Matches(title))
        {
            var word = m.Value.ToLowerInvariant();
            set.Add(word.StartsWith("remaster") ? "remaster" : word);
        }

        return set;
    }

    /// <summary>Title without bracketed version information, for the text comparison.</summary>
    private static string? StripVersion(string? title) =>
        title == null ? null : Regex.Replace(title, @"\s*[\(\[][^\)\]]*[\)\]]", "").Trim();

    private static DuplicateMatch Snapshot(LibraryEntry e, DuplicateKind kind, string reason, double? similarity) => new()
    {
        LibraryEntryId = e.Id,
        RelativePath = e.RelativePath,
        Kind = kind,
        Reason = reason,
        Similarity = similarity,
        Title = e.Title,
        Artist = e.Artist,
        Album = e.Album,
        Date = e.Date,
        Codec = e.Codec,
        BitrateKbps = e.BitrateKbps,
        DurationSeconds = e.DurationSeconds,
        FileSize = e.FileSize,
        MbRecordingId = e.MbRecordingId,
        MbReleaseId = e.MbReleaseId,
        TagSource = e.TagSource,
    };

    [GeneratedRegex(@"\b(remix|live|remaster(ed)?|acoustic|unplugged|edit|extended|instrumental|demo|radio|vip|club|dub|flip|rework|bootleg|mashup|cover|orchestral|piano|reprise|session)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex VersionWords();
}
