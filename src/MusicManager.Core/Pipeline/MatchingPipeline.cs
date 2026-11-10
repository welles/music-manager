using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MusicManager.Core.Ai;
using MusicManager.Core.Configuration;
using MusicManager.Core.Data;
using MusicManager.Core.Model;
using MusicManager.Core.Rules;
using MusicManager.Core.Services;

namespace MusicManager.Core.Pipeline;

/// <summary>
/// The processing pipeline: pre-cleaning → fingerprint/AcoustID → MusicBrainz → release scoring
/// → (AI) decision → tag building → conversion → filing into the library.
/// </summary>
public sealed partial class MatchingPipeline(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settingsService,
    Storage storage,
    AudioTools audio,
    AcoustIdClient acoustId,
    MusicBrainzService musicBrainz,
    CoverArtService coverArt,
    AiService ai,
    ExternalSourceService external,
    DuplicateDetector duplicates,
    LibraryIndexer libraryIndexer,
    PipelineNotifier notifier,
    WorkQueue queue,
    ILogger<MatchingPipeline> logger)
{
    private const int MaxTransientRetries = 5;
    private static readonly TimeSpan TransientRetryDelay = TimeSpan.FromMinutes(1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _transientRetries = new();

    // =====================================================================================
    // Group processing
    // =====================================================================================

    public async Task ProcessGroupAsync(int groupId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var group = await db.Groups.Include(g => g.Files).FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group == null) return;

        var files = group.Files.Where(f => f.Status == FileStatus.Queued).OrderBy(f => f.RelativePath).ToList();
        if (files.Count == 0) return;

        var settings = await settingsService.GetAsync(ct);
        var profiles = await settingsService.GetSourceProfilesAsync(ct);
        var profile = profiles.FirstOrDefault(p => p.Name == group.SourceProfile);

        // Phase 1: analyze and identify every file.
        var recordings = new Dictionary<int, List<RecordingMatch>>();
        var assignedByTags = new HashSet<int>();
        foreach (var file in files)
        {
            notifier.SetActivity($"Analysiere {file.FileName}");
            try
            {
                SetStatus(file, FileStatus.Processing, null);
                await db.SaveChangesAsync(ct);
                var identification = await AnalyzeAsync(file, profile, settings, ct);
                recordings[file.Id] = identification.Recordings;
                if (identification.AssignedByTags) assignedByTags.Add(file.Id);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Fail(file, ex);
            }

            await db.SaveChangesAsync(ct);
            notifier.Notify();
        }

        // Files of the group that were matched earlier also count as context.
        var groupFileNames = group.Files.Select(f => f.FileName).ToList();
        var earlier = group.Files.Where(f => !recordings.ContainsKey(f.Id) && f.Candidates.Count > 0)
            .Select(f => CandidatesAsRecordings(f.Candidates)).ToList();

        // Phase 2: score releases with group context and decide.
        foreach (var file in files.Where(f => f.Status == FileStatus.Processing))
        {
            var recs = recordings.GetValueOrDefault(file.Id) ?? [];
            if (recs.Count == 0)
            {
                await HandleNotInMusicBrainzAsync(file, settings, ct);
                await db.SaveChangesAsync(ct);
                continue;
            }

            try
            {
                var context = GroupContext.From(recordings.Where(r => r.Key != file.Id).Select(r => r.Value).Concat(earlier));
                var result = ReleaseScorer.Score(file.CleanedTags ?? new TagSet(), file.DurationSeconds, recs, context, settings);
                file.Candidates = result.Candidates.Take(40).ToList();
                AddEvent(file, $"{result.Candidates.Count} Release-Kandidaten bewertet: {result.Reason}");

                if (assignedByTags.Contains(file.Id) && result.Best != null)
                {
                    // Already assigned (e.g. by Picard): use the tagged release, the rest of the pipeline still runs.
                    file.SelectedCandidateKey = result.Best.Key;
                    file.Decision = DecisionSource.Automatic;
                    AddEvent(file, "Direkt über die MusicBrainz-Tags der Datei zugeordnet");
                }
                else if (result.IsCertain)
                {
                    file.SelectedCandidateKey = result.Best!.Key;
                    file.Decision = DecisionSource.Automatic;
                }
                else
                {
                    await DecideWithAiAsync(file, groupFileNames, result, settings, ct);
                }

                if (file.SelectedCandidateKey != null)
                {
                    notifier.SetActivity($"Erzeuge Tags für {file.FileName}");
                    await BuildFinalTagsAsync(file, file.SelectedCandidate!, settings, ct);
                    if (settings.AutoFileConfidentMatches)
                        SetStatus(file, FileStatus.Approved, null);
                    else
                        SetStatus(file, FileStatus.NeedsReview, "Vorschau: bitte Tags bestätigen (automatisches Einsortieren ist aus)");
                }
                else if (file.Status == FileStatus.Processing)
                {
                    SetStatus(file, FileStatus.NeedsReview, result.Reason);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Fail(file, ex);
            }

            await db.SaveChangesAsync(ct);
            notifier.Notify();
        }

        group.ProcessedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // Phase 3: write approved files.
        foreach (var file in files.Where(f => f.Status == FileStatus.Approved))
            await WriteAndFileAsync(db, file, ct);

        notifier.SetActivity(null);
    }

    private async Task<Identification> AnalyzeAsync(TrackFile file, SourceProfile? profile, AppSettings settings, CancellationToken ct)
    {
        var path = storage.InboxPath(file.RelativePath);
        if (!File.Exists(path))
            throw new FileNotFoundException("Datei ist nicht mehr im Eingang vorhanden", path);

        var info = await audio.ProbeAsync(path, ct);
        var fi = new FileInfo(path);
        file.Codec = info.Codec;
        file.DurationSeconds = info.DurationSeconds;
        file.BitrateKbps = info.BitrateKbps;
        file.FileSize = fi.Length;
        file.FileModifiedUtc = fi.LastWriteTimeUtc;
        if (info.Codec == null)
            throw new InvalidOperationException("Keine Audiospur gefunden");

        // Tags and pre-cleaning.
        file.OriginalTags = TagIo.Read(path);
        file.OriginalFields = TagFields.Read(path);
        var cleaned = PreCleaner.Clean(file.OriginalTags, file.FileName, profile);
        file.CleanedTags = cleaned.Tags;
        file.CleanupSource = "rules";
        foreach (var change in cleaned.Changes)
            AddEvent(file, change);

        var useAi = ai.IsConfigured && (settings.AiCleanupMode == AiCleanupMode.Always
                                        || (settings.AiCleanupMode == AiCleanupMode.WhenMessy && cleaned.LooksMessy));
        if (useAi)
        {
            try
            {
                var suggestion = await ai.CleanupTagsAsync(file.FileName, file.OriginalTags, cleaned.Tags, settings, ct);
                if (suggestion != null)
                {
                    file.AiSuggestion = suggestion;
                    if (suggestion.Confidence >= 0.6 && suggestion.Title != null && suggestion.Artist != null)
                    {
                        var t = cleaned.Tags.Clone();
                        t.Title = suggestion.Title;
                        t.Artist = suggestion.Artist;
                        t.Artists = suggestion.Artists;
                        t.Album ??= suggestion.Album;
                        file.CleanedTags = t;
                        file.CleanupSource = "ai";
                    }

                    AddEvent(file, $"KI-Bereinigung ({suggestion.Confidence:P0}): {suggestion.Notes}");
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                AddEvent(file, $"KI-Bereinigung fehlgeschlagen: {ex.Message}", EventLevel.Warning);
            }
        }

        return await IdentifyAsync(file, path, settings, ct);
    }

    private sealed record Identification(List<RecordingMatch> Recordings, bool AssignedByTags);

    /// <summary>
    /// Recording lookup: existing MusicBrainz tags → AcoustID fingerprint → MusicBrainz text search.
    /// Files that already carry recording and release IDs are assigned to that release directly.
    /// </summary>
    private async Task<Identification> IdentifyAsync(TrackFile file, string path, AppSettings settings, CancellationToken ct)
    {
        var ids = new List<(string Id, double Score, string Source)>();
        file.AcoustId = file.OriginalTags?.AcoustId;
        var hasTaggedRecording = Guid.TryParse(file.OriginalTags?.MbRecordingId, out var taggedRecording);

        // Fingerprint whenever the recording is unknown or the file has no AcoustID yet
        // (already assigned files still get their AcoustID).
        AcoustIdResult? lookup = null;
        if (acoustId.IsConfigured && (!hasTaggedRecording || file.AcoustId == null))
            lookup = await LookupAcoustIdAsync(file, path, settings, ct);
        else if (!acoustId.IsConfigured)
            AddEvent(file, "AcoustID-API-Key fehlt – kein Fingerabdruck-Abgleich", EventLevel.Warning);

        if (hasTaggedRecording)
        {
            try
            {
                var rec = await musicBrainz.GetRecordingAsync(taggedRecording.ToString(), 1.0, "tag", ct);
                var taggedRelease = Guid.TryParse(file.OriginalTags?.MbReleaseId, out var rel) ? rel.ToString() : null;
                var onRelease = rec.Releases.Where(r => r.ReleaseId == taggedRelease).ToList();
                if (onRelease.Count > 0 && !rec.IsVideo)
                {
                    AddEvent(file, $"MusicBrainz-Tags gefunden: Recording \"{rec.Title}\" auf Release \"{onRelease[0].Title}\"");
                    file.AcoustId ??= lookup?.BestAcoustId(rec.RecordingId, settings.AcoustIdMinScore);
                    return new Identification([WithReleases(rec, onRelease)], true);
                }

                AddEvent(file, taggedRelease == null
                    ? "MusicBrainz-Recording-ID gefunden, Release wird bewertet"
                    : "Getaggtes Release enthält das Recording nicht – Release wird neu bewertet", EventLevel.Info);
                ids.Add((rec.RecordingId, 1.0, "tag"));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                AddEvent(file, $"MusicBrainz-ID aus den Tags ungültig ({ex.Message}) – normale Erkennung", EventLevel.Warning);
            }
        }

        if (ids.Count == 0 && lookup != null)
        {
            var matches = lookup.Matches;
            var usable = matches.Where(m => m.Score >= settings.AcoustIdMinScore).ToList();
            AddEvent(file, matches.Count == 0
                ? "AcoustID: kein Recording verknüpft"
                : $"AcoustID: {matches.Count} Recordings, {usable.Count} über Schwellwert (bester Score {matches[0].Score:P0})");
            ids.AddRange(usable.Select(m => (m.RecordingId, m.Score, "acoustid")));
        }

        file.AcoustId ??= lookup?.BestAcoustId(ids.Select(i => i.Id).FirstOrDefault(), settings.AcoustIdMinScore);

        // Spotify/Deezer links in the tags: exact track, its ISRC may lead to MusicBrainz.
        file.ExternalMatches = [];
        if (ids.Count == 0)
        {
            var (linked, messages) = await external.FromLinksAsync(file.OriginalFields, ct);
            foreach (var message in messages) AddEvent(file, message);
            file.ExternalMatches.AddRange(linked);
            ids.AddRange(await RecordingsByIsrcAsync(file, linked, ct));
        }

        var tags = file.CleanedTags!;
        // Without an artist the text search only produces noise (e.g. "track03" from the file name).
        if (ids.Count == 0 && !string.IsNullOrWhiteSpace(tags.Title) && string.IsNullOrWhiteSpace(tags.Artists.FirstOrDefault() ?? tags.Artist))
            AddEvent(file, "Kein Künstler bekannt – MusicBrainz-Textsuche übersprungen");
        else if (ids.Count == 0 && !string.IsNullOrWhiteSpace(tags.Title))
        {
            notifier.SetActivity($"Textsuche für {file.FileName}");
            var artist = tags.Artists.FirstOrDefault() ?? tags.Artist;

            // With an album tag first search on that album, then without.
            var usable = new List<RecordingSearchHit>();
            foreach (var album in new[] { tags.Album, null }.Distinct())
            {
                if (album == null && usable.Count > 0) break;
                var results = await musicBrainz.SearchRecordingsAsync(artist, tags.Title, album, 50, ct);
                usable = results
                    .Where(r => r.Score >= settings.TextSearchMinScore && !r.IsVideo)
                    .Where(r => TextUtil.Similarity(tags.Title, r.Title) >= 0.7)
                    .Where(r => LengthFits(file.DurationSeconds, r.LengthSeconds))
                    .OrderByDescending(r => r.Score)
                    .ThenByDescending(r => r.ReleaseCount)
                    .ThenBy(r => file.DurationSeconds is { } d && r.LengthSeconds is { } l ? Math.Abs(d - l) : 999)
                    .ToList();
                AddEvent(file, $"MusicBrainz-Textsuche \"{artist} – {tags.Title}\"{(album != null ? $" auf \"{album}\"" : "")}: " +
                               $"{results.Count} Treffer, {usable.Count} passend (Titel, Länge, kein Video)");
            }

            ids.AddRange(usable.Select(r => (r.RecordingId, r.Score / 100.0, "search")));
        }

        var recordings = new List<RecordingMatch>();
        foreach (var (id, score, source) in ids.DistinctBy(i => i.Id).Take(settings.MaxRecordingsPerFile))
        {
            notifier.SetActivity($"MusicBrainz-Abfrage für {file.FileName}");
            var rec = await musicBrainz.GetRecordingAsync(id, score, source, ct);

            if (rec.IsVideo)
            {
                AddEvent(file, $"Recording {rec.Title} ist ein Video – ignoriert");
                continue;
            }

            if (rec.Releases.Count > 0)
                recordings.Add(rec);
        }

        if (recordings.Count == 0 && file.ExternalMatches.Count == 0)
            recordings = await ExternalSearchAsync(file, settings, ct);

        return new Identification(recordings, false);
    }

    private async Task<AcoustIdResult?> LookupAcoustIdAsync(TrackFile file, string path, AppSettings settings, CancellationToken ct)
    {
        notifier.SetActivity($"Fingerabdruck für {file.FileName}");
        var fp = await audio.FingerprintAsync(path, ct);
        if (fp == null)
        {
            AddEvent(file, "Fingerabdruck konnte nicht berechnet werden", EventLevel.Warning);
            return null;
        }

        try
        {
            var result = await acoustId.LookupAsync(fp, ct);
            if (result.Tracks.Count == 0)
                AddEvent(file, "AcoustID: Fingerabdruck unbekannt");
            return result;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // AcoustID problems (invalid key, outage) must not stop the file – fall back to text search.
            AddEvent(file, $"AcoustID-Abfrage fehlgeschlagen, weiter mit Textsuche: {ex.Message}", EventLevel.Warning);
            return null;
        }
    }

    private static RecordingMatch WithReleases(RecordingMatch r, List<ReleaseAppearance> releases) => new()
    {
        RecordingId = r.RecordingId, Title = r.Title, Credits = r.Credits, LengthSeconds = r.LengthSeconds,
        MatchScore = r.MatchScore, Source = r.Source, IsVideo = r.IsVideo, Releases = releases,
    };

    /// <summary>Searches Spotify/Deezer; a confident hit with an ISRC is looked up in MusicBrainz again.</summary>
    private async Task<List<RecordingMatch>> ExternalSearchAsync(TrackFile file, AppSettings settings, CancellationToken ct)
    {
        notifier.SetActivity($"Fremdquellen für {file.FileName}");
        var (found, messages) = await external.SearchAsync(file.CleanedTags!, file.DurationSeconds, settings, ct);
        foreach (var message in messages) AddEvent(file, message);
        file.ExternalMatches.AddRange(found);

        var recordings = new List<RecordingMatch>();
        foreach (var (id, score, source) in await RecordingsByIsrcAsync(file, found.Where(m => m.Score >= 0.9), ct))
        {
            var rec = await musicBrainz.GetRecordingAsync(id, score, source, ct);
            if (!rec.IsVideo && rec.Releases.Count > 0) recordings.Add(rec);
        }

        return recordings;
    }

    /// <summary>MusicBrainz recordings carrying the ISRC of external matches.</summary>
    private async Task<List<(string Id, double Score, string Source)>> RecordingsByIsrcAsync(TrackFile file, IEnumerable<ExternalMatch> matches,
        CancellationToken ct)
    {
        var result = new List<(string, double, string)>();
        foreach (var m in matches.Where(m => !string.IsNullOrWhiteSpace(m.Isrc)).DistinctBy(m => m.Isrc).Take(3))
        {
            var ids = await musicBrainz.SearchIsrcAsync(m.Isrc!, ct);
            if (ids.Count == 0) continue;
            AddEvent(file, $"ISRC {m.Isrc} aus {SourceName(m.Source)} in MusicBrainz gefunden ({ids.Count} Recording(s))");
            result.AddRange(ids.Select(id => (id, 0.95, "isrc")));
        }

        return result;
    }

    public static string SourceName(string source) => source switch
    {
        "spotify" => "Spotify",
        "deezer" => "Deezer",
        _ => source,
    };

    /// <summary>Text search hits must roughly match the file duration (unknown lengths are accepted).</summary>
    private static bool LengthFits(double? file, double? recording) =>
        file == null || recording == null || Math.Abs(file.Value - recording.Value) <= 10;

    private async Task HandleNotInMusicBrainzAsync(TrackFile file, AppSettings settings, CancellationToken ct)
    {
        SetStatus(file, FileStatus.NotInMusicBrainz, file.ExternalMatches.Count > 0
            ? $"Nicht in MusicBrainz – {file.ExternalMatches.Count} Vorschläge aus {string.Join(" und ", file.ExternalMatches.Select(m => SourceName(m.Source)).Distinct())}"
            : "Weder Fingerabdruck noch Textsuche haben einen MusicBrainz-Eintrag gefunden");

        // The AI suggestion is only needed when no source delivered a usable match.
        if (settings.AiSuggestForUnknown && ai.IsConfigured && file.AiSuggestion == null && file.ExternalMatches.Count == 0)
        {
            try
            {
                file.AiSuggestion = await ai.CleanupTagsAsync(file.FileName, file.OriginalTags ?? new TagSet(), file.CleanedTags ?? new TagSet(), settings, ct);
                if (file.AiSuggestion != null)
                    AddEvent(file, $"KI-Vorschlag für manuelles Taggen: {file.AiSuggestion.Artist} – {file.AiSuggestion.Title}");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                AddEvent(file, $"KI-Vorschlag fehlgeschlagen: {ex.Message}", EventLevel.Warning);
            }
        }
    }

    private async Task DecideWithAiAsync(TrackFile file, List<string> groupFileNames, ScoreResult result, AppSettings settings, CancellationToken ct)
    {
        if (!settings.AiDecisionEnabled || !ai.IsConfigured) return;

        notifier.SetActivity($"KI-Entscheidung für {file.FileName}");
        try
        {
            var decision = await ai.DecideReleaseAsync(file, groupFileNames, result.Candidates, settings, ct);
            if (decision == null) return;

            file.AiDecision = decision;
            if (decision.Accepted)
            {
                file.SelectedCandidateKey = $"{decision.RecordingId}/{decision.ReleaseId}";
                file.Decision = DecisionSource.Ai;
                AddEvent(file, $"KI hat entschieden ({decision.Confidence:P0}): {decision.Reasoning}");
            }
            else
            {
                AddEvent(file, $"KI unsicher ({decision.Decision}, {decision.Confidence:P0}): {decision.Reasoning}");
                SetStatus(file, FileStatus.NeedsReview, $"{result.Reason}; KI: {decision.Reasoning}");
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            AddEvent(file, $"KI-Entscheidung fehlgeschlagen: {ex.Message}", EventLevel.Warning);
        }
    }

    private static List<RecordingMatch> CandidatesAsRecordings(IEnumerable<ReleaseCandidate> candidates) =>
        candidates.GroupBy(c => c.RecordingId).Select(g => new RecordingMatch
        {
            RecordingId = g.Key,
            Title = g.First().RecordingTitle,
            Releases = g.Select(c => c.Release).ToList(),
        }).ToList();

    // =====================================================================================
    // Tag building
    // =====================================================================================

    /// <summary>Fetches the full release and builds the final tags and target path for a candidate.</summary>
    public async Task BuildFinalTagsAsync(TrackFile file, ReleaseCandidate candidate, AppSettings settings, CancellationToken ct)
    {
        var release = await musicBrainz.GetReleaseAsync(candidate.Release.ReleaseId, ct);
        var rules = await settingsService.GetArtistRulesAsync(ct);

        var found = TagBuilder.FindTrack(release, candidate.RecordingId)
                    ?? throw new InvalidOperationException("Recording nicht auf dem gewählten Release gefunden");

        var artists = new Dictionary<string, ArtistInfo>();
        if (!string.IsNullOrWhiteSpace(settings.ArtistLocale) || (settings.UseGenres && settings.UseArtistGenres))
        {
            var ids = found.Track.Credits.Concat(release.Credits).Concat(found.Track.Remixers)
                .Select(c => c.ArtistId).Where(id => Guid.TryParse(id, out _)).Distinct();
            foreach (var id in ids)
                artists[id] = await musicBrainz.GetArtistAsync(id, ct);
        }

        file.FinalTags = TagBuilder.Build(release, candidate.RecordingId, artists, settings, rules);
        file.FinalTags = TaggingRules.AddSourceArtists(file.FinalTags, file.CleanedTags, rules);
        file.FinalTags.AcoustId = file.AcoustId;
        await TranslateForeignTitlesAsync(file, settings, ct);
        file.TargetPath = PathBuilder.Build(file.FinalTags);
        file.ManuallyTagged = false;
        AddEvent(file, $"Release gewählt: {release.Title} ({release.Date}, {release.Country}) → {file.TargetPath}");
    }

    /// <summary>Title and album in non-Latin script become "English (Original)" (translated by Claude).</summary>
    private async Task TranslateForeignTitlesAsync(TrackFile file, AppSettings settings, CancellationToken ct)
    {
        var t = file.FinalTags;
        if (t == null || !settings.TranslateForeignTitles) return;

        var texts = new[] { t.Title, t.Album }.Where(ForeignTitles.NeedsTranslation).Distinct().Cast<string>().ToList();
        if (texts.Count == 0) return;
        if (!ai.IsConfigured)
        {
            AddEvent(file, "Nicht-lateinischer Titel – Übersetzung braucht einen Anthropic-API-Key", EventLevel.Warning);
            return;
        }

        try
        {
            var translations = await ai.TranslateAsync(texts, t.Artist, settings, ct);
            if (ForeignTitles.NeedsTranslation(t.Title)) t.Title = ForeignTitles.Combine(t.Title!, translations.GetValueOrDefault(t.Title!));
            if (ForeignTitles.NeedsTranslation(t.Album)) t.Album = ForeignTitles.Combine(t.Album!, translations.GetValueOrDefault(t.Album!));
            AddEvent(file, $"Übersetzt: {string.Join(", ", texts.Select(x => $"\"{x}\" → \"{translations.GetValueOrDefault(x)}\""))}");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            AddEvent(file, $"Übersetzung fehlgeschlagen: {ex.Message}", EventLevel.Warning);
        }
    }

    /// <summary>Applies the rules to manually entered tags (fallback for files not in MusicBrainz).</summary>
    public async Task BuildManualTagsAsync(TrackFile file, TagSet manual, CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(ct);
        var rules = await settingsService.GetArtistRulesAsync(ct);
        file.FinalTags = TaggingRules.Apply(manual, rules, settings.ConvertPunctuation);
        file.FinalTags.AcoustId = file.AcoustId;
        await TranslateForeignTitlesAsync(file, settings, ct);
        file.TargetPath = PathBuilder.Build(file.FinalTags);
        file.ManuallyTagged = true;
        file.Decision = DecisionSource.ManualTags;
        AddEvent(file, $"Manuell getaggt → {file.TargetPath}");
    }

    // =====================================================================================
    // Writing and filing
    // =====================================================================================

    public async Task WriteFileAsync(int fileId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var file = await db.Files.Include(f => f.Group).FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is not { Status: FileStatus.Approved }) return;
        await WriteAndFileAsync(db, file, ct);
        notifier.SetActivity(null);
    }

    private async Task WriteAndFileAsync(AppDbContext db, TrackFile file, CancellationToken ct)
    {
        notifier.SetActivity($"Schreibe {file.FileName}");
        var settings = await settingsService.GetAsync(ct);
        string? tmp = null;
        try
        {
            if (file.FinalTags == null || file.TargetPath == null)
                throw new InvalidOperationException("Keine Tags zum Schreiben vorhanden");

            var source = storage.InboxPath(file.RelativePath);
            if (!File.Exists(source))
                throw new FileNotFoundException("Datei ist nicht mehr im Eingang vorhanden", source);

            // Duplicate check against the library index (unless already decided in the duplicate view).
            notifier.SetActivity($"Duplikatprüfung für {file.FileName}");
            var fingerprint = (await audio.RawFingerprintAsync(source, ct))?.Values;
            if (file.DuplicateDecision == null)
            {
                file.Duplicates = await duplicates.FindAsync(file.FinalTags, file.DurationSeconds, fingerprint, file.TargetPath, ct);
                var dup = file.Duplicates.Where(d => d.Kind == DuplicateKind.Duplicate).ToList();
                if (dup.Count > 0 || File.Exists(storage.LibraryPath(file.TargetPath)))
                {
                    var reason = dup.Count > 0 ? $"{dup[0].Reason}: {dup[0].RelativePath}" : $"Zieldatei existiert bereits: {file.TargetPath}";
                    SetStatus(file, FileStatus.Duplicate, $"Mögliches Duplikat – {reason}");
                    AddEvent(file, $"Nicht einsortiert, mögliches Duplikat ({dup.Count} Treffer): {reason}", EventLevel.Warning);
                    await db.SaveChangesAsync(ct);
                    notifier.Notify();
                    return;
                }

                foreach (var other in file.Duplicates.Where(d => d.Kind == DuplicateKind.OtherVersion))
                    AddEvent(file, $"Mögliche andere Version in der Bibliothek: {other.RelativePath} ({other.Reason.TrimStart(' ', '–')})");
            }

            // "Keep new": the new file takes the path of the replaced one (as far as the extension allows).
            if (file.DuplicateDecision == DuplicateDecision.KeepNew && file.ReplaceLibraryPath != null)
                file.TargetPath = Path.ChangeExtension(file.ReplaceLibraryPath, ".m4a");
            else if (file.DuplicateDecision == DuplicateDecision.KeepBoth)
                file.TargetPath = Path.GetRelativePath(storage.LibraryRoot, Storage.UniquePath(storage.LibraryPath(file.TargetPath)));

            var target = storage.LibraryPath(file.TargetPath);
            if (File.Exists(target) && !(file.DuplicateDecision == DuplicateDecision.KeepNew && file.ReplaceLibraryPath == file.TargetPath))
                throw new InvalidOperationException($"Zieldatei existiert bereits: {file.TargetPath}");

            // Convert into the work folder.
            Directory.CreateDirectory(storage.WorkRoot);
            tmp = Path.Combine(storage.WorkRoot, $"{file.Id}-{Guid.NewGuid():N}.m4a");
            var info = await audio.ProbeAsync(source, ct);
            var remuxed = await audio.ConvertToM4aAsync(source, tmp, info.Codec, settings.AacBitrateKbps, ct);
            var alreadyM4a = Path.GetExtension(source).ToLowerInvariant() is ".m4a" or ".mp4";
            AddEvent(file, (remuxed, alreadyM4a) switch
            {
                (true, true) => "Bereits M4A/AAC – Audio unverändert übernommen, nur Metadaten und Cover neu geschrieben",
                (true, false) => $"AAC aus {Path.GetExtension(source)} ohne Neukodierung in M4A übernommen",
                _ => $"Konvertiert {info.Codec} → AAC {settings.AacBitrateKbps} kbps",
            });

            var cover = await GetCoverAsync(file, source, settings, ct);
            TagIo.WriteM4a(tmp, file.FinalTags, cover,
                SourceFor(file));

            // Replaced library file goes to the trash (decision can be undone from there).
            if (file.DuplicateDecision == DuplicateDecision.KeepNew && file.ReplaceLibraryPath != null)
            {
                var replaced = storage.LibraryPath(file.ReplaceLibraryPath);
                if (File.Exists(replaced))
                {
                    var trashed = storage.MoveToTrash(replaced, Path.Combine("ersetzt", file.ReplaceLibraryPath));
                    AddEvent(file, $"Vorhandene Datei {file.ReplaceLibraryPath} ersetzt (in den Papierkorb: {trashed})");
                }

                await libraryIndexer.RemoveAsync(file.ReplaceLibraryPath, ct);
            }

            // Keep the timestamp of the original file.
            File.SetLastWriteTimeUtc(tmp, File.GetLastWriteTimeUtc(source));
            Storage.MovePreservingTime(tmp, target);
            tmp = null;
            await libraryIndexer.UpsertAsync(file.TargetPath, fingerprint, ct);

            file.TrashPath = storage.MoveToTrash(source, file.RelativePath);
            SetStatus(file, FileStatus.Done, null);
            file.CompletedAt = DateTimeOffset.UtcNow;
            AddEvent(file, $"Einsortiert: {file.TargetPath} (Original im Papierkorb)");
            await db.SaveChangesAsync(ct);

            await FinalizeGroupAsync(db, file.GroupId, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Fail(file, ex);
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            if (tmp != null && File.Exists(tmp)) File.Delete(tmp);
            notifier.Notify();
        }
    }

    private async Task<byte[]?> GetCoverAsync(TrackFile file, string source, AppSettings settings, CancellationToken ct)
    {
        if (file.ManualCoverFile != null && File.Exists(file.ManualCoverFile))
        {
            file.CoverSource = "Upload";
            return await audio.ResizeImageAsync(await File.ReadAllBytesAsync(file.ManualCoverFile, ct), settings.CoverSize, ct);
        }

        if (!file.ManuallyTagged && file.FinalTags?.MbReleaseId != null)
        {
            var caa = await coverArt.GetFrontAsync(file.FinalTags.MbReleaseId, file.FinalTags.MbReleaseGroupId, settings.CoverSize, ct);
            if (caa != null)
            {
                file.CoverSource = caa.Source;
                return caa.Data;
            }
        }

        // Local cover file next to the audio file (Picard: cover|folder|albumart).
        var dir = Path.GetDirectoryName(source)!;
        var local = Directory.EnumerateFiles(dir).FirstOrDefault(f => LocalCoverPattern().IsMatch(Path.GetFileName(f)));
        if (local != null)
        {
            file.CoverSource = $"Lokale Datei {Path.GetFileName(local)}";
            return await audio.ResizeImageAsync(await File.ReadAllBytesAsync(local, ct), settings.CoverSize, ct);
        }

        var embedded = TagIo.ReadEmbeddedCover(source);
        if (embedded != null)
        {
            file.CoverSource = "Aus Originaldatei";
            return await audio.ResizeImageAsync(embedded, settings.CoverSize, ct);
        }

        file.CoverSource = null;
        return null;
    }

    /// <summary>
    /// When all files of a group are finished: move additional files (images) to the album
    /// folder and remove empty folders from the inbox.
    /// </summary>
    private async Task FinalizeGroupAsync(AppDbContext db, int groupId, CancellationToken ct)
    {
        var group = await db.Groups.Include(g => g.Files).FirstAsync(g => g.Id == groupId, ct);
        if (group.Open || group.Files.Any(f => f.Status is not (FileStatus.Done or FileStatus.Discarded)))
            return;
        if (group.RelativeFolder == "") return;

        var settings = await settingsService.GetAsync(ct);
        var folder = storage.InboxPath(group.RelativeFolder);
        if (!Directory.Exists(folder)) return;

        var targetDir = group.Files.Where(f => f is { Status: FileStatus.Done, TargetPath: not null })
            .GroupBy(f => Path.GetDirectoryName(f.TargetPath!)!)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

        foreach (var pattern in settings.AdditionalFilePatterns)
        {
            foreach (var extra in Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories))
            {
                try
                {
                    if (targetDir != null)
                    {
                        var dest = Storage.UniquePath(Path.Combine(storage.LibraryPath(targetDir), Path.GetFileName(extra)));
                        Storage.MovePreservingTime(extra, dest);
                    }
                    else
                    {
                        storage.MoveToTrash(extra, storage.RelativeToInbox(extra));
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not move {File}", extra);
                }
            }
        }

        var stopAt = group.SourceProfile != null
            ? storage.InboxPath(group.RelativeFolder.Split(Path.DirectorySeparatorChar)[0])
            : storage.InboxRoot;
        Storage.DeleteEmptyDirectories(folder, stopAt);
    }

    // =====================================================================================
    // Helpers
    // =====================================================================================

    /// <summary>Value of the MUSICMANAGER_SOURCE tag: where the tags came from.</summary>
    public static string SourceFor(TrackFile file) =>
        file.ManuallyTagged ? file.ExternalSource ?? "manual" : file.Decision == DecisionSource.Ai ? "musicbrainz-ai" : "musicbrainz";

    public static void SetStatus(TrackFile file, FileStatus status, string? reason)
    {
        file.Status = status;
        file.StatusReason = reason;
        file.UpdatedAt = DateTimeOffset.UtcNow;
        if (status != FileStatus.Error) file.Error = null;
    }

    public static void AddEvent(TrackFile file, string message, EventLevel level = EventLevel.Info) =>
        file.Events.Add(new FileEvent { Message = message, Level = level });

    private void Fail(TrackFile file, Exception ex)
    {
        // MusicBrainz busy / network hiccup: queue the file again instead of failing it.
        if (MusicBrainzService.IsTransient(ex) && _transientRetries.AddOrUpdate(file.Id, 1, (_, n) => n + 1) <= MaxTransientRetries)
        {
            logger.LogWarning("Transient error for {File}, retrying later: {Message}", file.RelativePath, ex.Message);
            SetStatus(file, FileStatus.Queued, $"Vorübergehender Fehler, neuer Versuch in {TransientRetryDelay.TotalMinutes:0} Minute(n)");
            AddEvent(file, $"Vorübergehender Fehler ({ex.Message}) – wird später erneut verarbeitet", EventLevel.Warning);
            var groupId = file.GroupId;
            _ = Task.Run(async () =>
            {
                await Task.Delay(TransientRetryDelay);
                queue.Enqueue(new WorkItem(WorkKind.ProcessGroup, groupId));
            });
            return;
        }

        logger.LogError(ex, "Processing {File} failed", file.RelativePath);
        SetStatus(file, FileStatus.Error, ex.Message);
        file.Error = ex.ToString();
        AddEvent(file, $"Fehler: {ex.Message}", EventLevel.Error);
    }

    [GeneratedRegex(@"^(?:cover|folder|albumart)(.*)\.(?:jpe?g|png|gif|tiff?|webp)$", RegexOptions.IgnoreCase)]
    private static partial Regex LocalCoverPattern();
}
