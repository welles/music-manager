using MusicManager.Core.Configuration;
using MusicManager.Core.Model;

namespace MusicManager.Core.Rules;

/// <summary>Information about the other files of the same inbox group.</summary>
public sealed class GroupContext
{
    public static readonly GroupContext Empty = new([]);

    /// <summary>Per other file: release IDs and release group IDs among its candidates.</summary>
    public GroupContext(IReadOnlyList<(HashSet<string> Releases, HashSet<string> ReleaseGroups)> otherFiles)
    {
        OtherFiles = otherFiles;
    }

    public IReadOnlyList<(HashSet<string> Releases, HashSet<string> ReleaseGroups)> OtherFiles { get; }

    public static GroupContext From(IEnumerable<IEnumerable<RecordingMatch>> otherFiles) =>
        new(otherFiles.Select(recs =>
        {
            var list = recs.ToList();
            return (
                list.SelectMany(r => r.Releases).Select(r => r.ReleaseId).ToHashSet(),
                list.SelectMany(r => r.Releases).Select(r => r.ReleaseGroupId).OfType<string>().ToHashSet());
        }).ToList());
}

public sealed class ScoreResult
{
    public required List<ReleaseCandidate> Candidates { get; init; }
    public ReleaseCandidate? Best => Candidates.FirstOrDefault();

    /// <summary>Best candidate of a different release group (the relevant competitor).</summary>
    public ReleaseCandidate? RunnerUp { get; init; }

    public double Margin => Best == null ? 0 : RunnerUp == null ? double.PositiveInfinity : Best.Score - RunnerUp.Score;
    public bool IsCertain { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
/// Release selection heuristic: every release the matching recordings appear on gets points
/// for old tags, release type, group context, date, country, format and length.
/// </summary>
public static class ReleaseScorer
{
    public static ScoreResult Score(TagSet oldTags, double? fileDurationSeconds, IReadOnlyList<RecordingMatch> recordings,
        GroupContext group, AppSettings settings)
    {
        var w = settings.Weights;
        var candidates = new List<ReleaseCandidate>();
        var earliestYear = recordings.SelectMany(r => r.Releases).Select(r => r.Year).Where(y => y != null).Min();
        var maxParts = MaxParts(oldTags, fileDurationSeconds, group, settings);
        var maxScore = maxParts.Values.Sum();

        foreach (var rec in recordings)
        {
            // Recording-level parts are the same for all releases of a recording.
            var recParts = new Dictionary<string, double>
            {
                ["Erkennung"] = w.Match * rec.MatchScore,
            };

            if (!string.IsNullOrWhiteSpace(oldTags.Title))
                recParts["Titel-Ähnlichkeit"] = w.TitleSimilarity * TextUtil.Similarity(oldTags.Title, rec.Title);

            var oldArtist = oldTags.Artist ?? string.Join(" & ", oldTags.Artists);
            if (!string.IsNullOrWhiteSpace(oldArtist))
                recParts["Künstler-Ähnlichkeit"] = w.ArtistSimilarity * TextUtil.Similarity(oldArtist, rec.ArtistString);

            if (fileDurationSeconds is { } fileLen && rec.LengthSeconds is { } recLen)
            {
                var diff = Math.Abs(fileLen - recLen);
                recParts["Länge"] = diff <= settings.LengthToleranceSeconds
                    ? w.LengthMatch
                    : -Math.Min(w.LengthMismatchMax, (diff - settings.LengthToleranceSeconds) * w.LengthMismatchPerSecond);
            }

            foreach (var rel in rec.Releases)
            {
                var parts = new Dictionary<string, double>(recParts);

                if (!string.IsNullOrWhiteSpace(oldTags.Album))
                    parts["Album-Ähnlichkeit"] = w.AlbumSimilarity * AlbumMatch(oldTags.Album, rel.Title);

                if (!string.IsNullOrWhiteSpace(oldTags.AlbumArtist) && rel.ArtistString != null)
                    parts["Album-Künstler"] = w.AlbumArtistSimilarity * TextUtil.Similarity(oldTags.AlbumArtist, rel.ArtistString);

                if (DateMatch(oldTags.Date, rel.Date) is { } dateMatch)
                    parts["Datum-Tag"] = w.DateTag * dateMatch;

                if (oldTags.TrackNumber != null && rel.TrackPosition != null)
                    parts["Tracknummer"] = oldTags.TrackNumber == rel.TrackPosition ? w.TrackNumber : 0;

                if (rel.Status != null && w.Status.TryGetValue(rel.Status, out var statusScore))
                    parts["Status"] = statusScore;

                if (rel.PrimaryType != null && w.PrimaryType.TryGetValue(rel.PrimaryType, out var primaryScore))
                    parts["Typ"] = primaryScore;

                var secondary = rel.SecondaryTypes.Sum(t => w.SecondaryType.GetValueOrDefault(t));
                if (secondary != 0)
                    parts["Sekundärtyp"] = secondary;

                if (group.OtherFiles.Count > 0)
                {
                    var sameRelease = group.OtherFiles.Count(f => f.Releases.Contains(rel.ReleaseId));
                    var sameGroup = rel.ReleaseGroupId == null ? 0 : group.OtherFiles.Count(f => f.ReleaseGroups.Contains(rel.ReleaseGroupId));
                    parts["Ordnerkontext"] = w.GroupSameRelease * sameRelease / group.OtherFiles.Count
                                             + w.GroupSameReleaseGroup * sameGroup / group.OtherFiles.Count;
                }

                if (earliestYear != null && rel.Year != null)
                    parts["Datum"] = w.EarliestRelease * Math.Max(0, 1 - (rel.Year.Value - earliestYear.Value) / w.EarliestReleaseYears);

                if (rel.Country != null)
                {
                    var idx = settings.PreferredCountries.FindIndex(c => string.Equals(c, rel.Country, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0)
                        parts["Land"] = w.Country * (settings.PreferredCountries.Count - idx) / settings.PreferredCountries.Count;
                }

                if (rel.Formats.Any(f => settings.PreferredFormats.Contains(f, StringComparer.OrdinalIgnoreCase)))
                    parts["Format"] = w.Format;

                var score = parts.Values.Sum();
                candidates.Add(new ReleaseCandidate
                {
                    RecordingId = rec.RecordingId,
                    RecordingTitle = rec.Title,
                    RecordingArtist = rec.ArtistString,
                    RecordingLengthSeconds = rec.LengthSeconds,
                    MatchScore = rec.MatchScore,
                    MatchSource = rec.Source,
                    Release = rel,
                    Score = Math.Round(score, 2),
                    Confidence = maxScore > 0 ? Math.Clamp(score / maxScore, 0, 1) : 0,
                    MaxScore = Math.Round(maxScore, 2),
                    Breakdown = parts.ToDictionary(p => p.Key, p => Math.Round(p.Value, 2)),
                    BreakdownMax = maxParts,
                });
            }
        }

        candidates = candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Release.Date ?? "9999").ToList();
        var best = candidates.FirstOrDefault();
        if (best == null)
            return new ScoreResult { Candidates = candidates, IsCertain = false, Reason = "Keine Release-Kandidaten" };

        // Releases of the same release group are equivalent for the album name; the relevant
        // competitor is the best candidate of another release group.
        var runnerUp = candidates.FirstOrDefault(c => !SameReleaseGroup(c, best));
        var margin = runnerUp == null ? double.PositiveInfinity : best.Score - runnerUp.Score;

        string reason;
        bool certain;
        if (best.Confidence < settings.MinCandidateConfidence)
        {
            certain = false;
            reason = $"Beste Bewertung zu niedrig ({best.Confidence:P0} < {settings.MinCandidateConfidence:P0})";
        }
        else if (!string.IsNullOrWhiteSpace(oldTags.Album)
                 && TextUtil.Similarity(oldTags.Album, best.Release.Title) < 0.5
                 // An album tag equal to the title means "single"; if MusicBrainz only has the
                 // song on an album, that album is fine.
                 && TextUtil.Similarity(oldTags.Album, oldTags.Title) < 0.8)
        {
            // The file says which album it belongs to, but the best candidate is something else
            // (e.g. only compilations were found) – do not file automatically.
            certain = false;
            reason = $"Album-Tag \"{oldTags.Album}\" passt nicht zum besten Kandidaten \"{best.Release.Title}\"";
        }
        else if (best.MatchSource != "tag" && OldArtist(oldTags) is { } oldArtist
                 && TextUtil.Similarity(oldArtist, best.RecordingArtist) < 0.4
                 && !IsStrongFingerprintMatch(best, fileDurationSeconds, settings))
        {
            // Same title, but a completely different artist (e.g. a fingerprint hit on another song
            // of the same name) – do not file the song under someone else automatically. A very
            // close fingerprint with the same length is trusted: the source may credit the members
            // of a band ("Petra Magoni, Ferruccio Spinetti" for "Musica Nuda").
            certain = false;
            reason = $"Künstler-Tag \"{oldArtist}\" passt nicht zum besten Kandidaten \"{best.RecordingArtist}\"";
        }
        else if (margin < settings.ScoreMarginThreshold)
        {
            certain = false;
            reason = $"Knapper Abstand zum nächsten Release ({margin:0.#} < {settings.ScoreMarginThreshold:0.#} Punkte)";
        }
        else
        {
            certain = true;
            reason = runnerUp == null
                ? "Eindeutig (nur eine Release-Gruppe)"
                : $"Eindeutig ({margin:0.#} Punkte Vorsprung)";
        }

        return new ScoreResult { Candidates = candidates, RunnerUp = runnerUp, IsCertain = certain, Reason = reason };
    }

    private static bool IsStrongFingerprintMatch(ReleaseCandidate c, double? fileDurationSeconds, AppSettings settings) =>
        c.MatchSource == "acoustid" && c.MatchScore >= 0.95
        && fileDurationSeconds is { } fileLen && c.RecordingLengthSeconds is { } recLen
        && Math.Abs(fileLen - recLen) <= settings.LengthToleranceSeconds;

    private static string? OldArtist(TagSet t)
    {
        var artist = t.Artist ?? string.Join(" & ", t.Artists);
        return string.IsNullOrWhiteSpace(artist) ? null : artist;
    }

    /// <summary>
    /// Album similarity where an exact name counts clearly more than a name with additions
    /// ("Black Habits" vs. "Black Habits (Deluxe)"), which the token similarity treats as almost equal.
    /// </summary>
    internal static double AlbumMatch(string album, string releaseTitle) =>
        TextUtil.Normalize(album) == TextUtil.Normalize(releaseTitle) ? 1 : 0.8 * TextUtil.Similarity(album, releaseTitle);

    /// <summary>
    /// How well the date of the old tags fits the release date: 1 for the same day (or the same
    /// year when the tag only has a year), 0.5 for the same year, 0 otherwise; null without dates.
    /// </summary>
    internal static double? DateMatch(string? tagDate, string? releaseDate)
    {
        if (tagDate is not { Length: >= 4 } || releaseDate is not { Length: >= 4 }) return null;
        if (tagDate[..4] != releaseDate[..4]) return 0;
        if (tagDate.Length == 4) return 1;
        return tagDate.Length >= 10 && releaseDate.Length >= 10 && tagDate[..10] == releaseDate[..10] ? 1 : 0.5;
    }

    private static bool SameReleaseGroup(ReleaseCandidate a, ReleaseCandidate b) =>
        a.Release.ReleaseGroupId != null
            ? a.Release.ReleaseGroupId == b.Release.ReleaseGroupId
            : a.Release.ReleaseId == b.Release.ReleaseId;

    /// <summary>Maximum achievable points per category with the information available for this file.</summary>
    private static Dictionary<string, double> MaxParts(TagSet old, double? duration, GroupContext group, AppSettings s)
    {
        var w = s.Weights;
        var max = new Dictionary<string, double> { ["Erkennung"] = w.Match };
        if (!string.IsNullOrWhiteSpace(old.Title)) max["Titel-Ähnlichkeit"] = w.TitleSimilarity;
        if (!string.IsNullOrWhiteSpace(old.Artist) || old.Artists.Count > 0) max["Künstler-Ähnlichkeit"] = w.ArtistSimilarity;
        if (duration != null) max["Länge"] = w.LengthMatch;
        if (!string.IsNullOrWhiteSpace(old.Album)) max["Album-Ähnlichkeit"] = w.AlbumSimilarity;
        if (!string.IsNullOrWhiteSpace(old.AlbumArtist)) max["Album-Künstler"] = w.AlbumArtistSimilarity;
        if (old.TrackNumber != null) max["Tracknummer"] = w.TrackNumber;
        if (old.Date is { Length: >= 4 }) max["Datum-Tag"] = w.DateTag;
        max["Status"] = w.Status.Values.DefaultIfEmpty(0).Max();
        max["Typ"] = w.PrimaryType.Values.DefaultIfEmpty(0).Max();
        max["Sekundärtyp"] = 0;
        if (group.OtherFiles.Count > 0) max["Ordnerkontext"] = w.GroupSameRelease + w.GroupSameReleaseGroup;
        max["Datum"] = w.EarliestRelease;
        max["Land"] = w.Country;
        max["Format"] = w.Format;
        return max;
    }
}
