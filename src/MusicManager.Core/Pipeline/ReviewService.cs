using Microsoft.EntityFrameworkCore;
using MusicManager.Core.Data;
using MusicManager.Core.Model;
using MusicManager.Core.Rules;
using MusicManager.Core.Services;

namespace MusicManager.Core.Pipeline;

/// <summary>Actions from the frontend: decisions in the review queue, manual tagging, retry, discard.</summary>
public sealed class ReviewService(
    IDbContextFactory<AppDbContext> dbFactory,
    MatchingPipeline pipeline,
    MusicBrainzService musicBrainz,
    CoverArtService coverArt,
    SettingsService settingsService,
    Storage storage,
    WorkQueue queue,
    PipelineNotifier notifier)
{
    /// <summary>Statuses in which a file still waits for a decision.</summary>
    public static readonly FileStatus[] OpenStatuses = [FileStatus.NeedsReview, FileStatus.NotInMusicBrainz, FileStatus.Error];

    /// <summary>Builds the tag preview for a candidate without changing the file.</summary>
    public async Task<TrackFile> PreviewCandidateAsync(int fileId, string candidateKey, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var file = await db.Files.AsNoTracking().FirstAsync(f => f.Id == fileId, ct);
        var candidate = file.Candidates.First(c => c.Key == candidateKey);
        await pipeline.BuildFinalTagsAsync(file, candidate, await settingsService.GetAsync(ct), ct);
        return file;
    }

    /// <summary>Tags a Spotify/Deezer match would produce – nothing is saved.</summary>
    public async Task<TrackFile> PreviewExternalAsync(int fileId, string matchKey, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var file = await db.Files.AsNoTracking().FirstAsync(f => f.Id == fileId, ct);
        var match = file.ExternalMatches.First(m => m.Key == matchKey);
        await pipeline.BuildManualTagsAsync(file, ExternalSourceService.ToTagSet(match), ct);
        file.ExternalSource = match.Source;
        file.Decision = DecisionSource.External;
        file.SelectedCandidateKey = null;
        return file;
    }

    /// <summary>Chooses a candidate from the list and files it.</summary>
    public async Task ChooseCandidateAsync(int fileId, string candidateKey, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var file = await db.Files.FirstAsync(f => f.Id == fileId, ct);
        var candidate = file.Candidates.First(c => c.Key == candidateKey);
        file.SelectedCandidateKey = candidateKey;
        file.Decision = DecisionSource.Manual;
        await pipeline.BuildFinalTagsAsync(file, candidate, await settingsService.GetAsync(ct), ct);
        Approve(file, "Release manuell gewählt");
        await db.SaveChangesAsync(ct);
        queue.Enqueue(new WorkItem(WorkKind.WriteFile, file.Id));
        notifier.Notify();
    }

    /// <summary>Confirms the tag preview of a file (when automatic filing is disabled).</summary>
    public async Task ApproveAsync(int fileId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var file = await db.Files.FirstAsync(f => f.Id == fileId, ct);
        if (file.FinalTags == null) throw new InvalidOperationException("Keine Tag-Vorschau vorhanden");
        Approve(file, "Vorschau bestätigt");
        await db.SaveChangesAsync(ct);
        queue.Enqueue(new WorkItem(WorkKind.WriteFile, file.Id));
        notifier.Notify();
    }

    public Task<List<ReleaseAppearance>> SearchReleasesAsync(string query, CancellationToken ct = default) =>
        musicBrainz.SearchReleasesAsync(query, 25, ct);

    /// <summary>
    /// Assigns a release (e.g. from the manual search) to files. The track is found by recording ID
    /// (from the candidates) or by title and duration.
    /// </summary>
    public async Task<List<string>> AssignReleaseAsync(IReadOnlyList<int> fileIds, string releaseId, CancellationToken ct = default)
    {
        var problems = new List<string>();
        var release = await musicBrainz.GetReleaseAsync(releaseId, ct);
        var settings = await settingsService.GetAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var files = await db.Files.Where(f => fileIds.Contains(f.Id)).ToListAsync(ct);
        var used = new HashSet<string>();

        foreach (var file in files)
        {
            var tracks = release.Media.SelectMany(m => m.Tracks.Select(t => (Medium: m, Track: t))).ToList();
            var candidateRecordings = file.Candidates.Select(c => c.RecordingId).ToHashSet();
            var match = tracks.FirstOrDefault(t => candidateRecordings.Contains(t.Track.RecordingId));
            if (match.Track == null)
            {
                var title = file.CleanedTags?.Title ?? file.FileName;
                match = tracks.Where(t => !used.Contains(t.Track.TrackId))
                    .Select(t => (t, Score: TextUtil.Similarity(title, t.Track.Title)
                                           - (file.DurationSeconds != null && t.Track.LengthSeconds != null
                                               ? Math.Min(0.5, Math.Abs(file.DurationSeconds.Value - t.Track.LengthSeconds.Value) / 60)
                                               : 0)))
                    .Where(x => x.Score >= 0.6)
                    .OrderByDescending(x => x.Score).Select(x => x.t).FirstOrDefault();
            }

            if (match.Track == null)
            {
                problems.Add($"{file.FileName}: kein passender Track auf \"{release.Title}\" gefunden");
                continue;
            }

            used.Add(match.Track.TrackId);
            var candidate = new ReleaseCandidate
            {
                RecordingId = match.Track.RecordingId,
                RecordingTitle = match.Track.Title,
                RecordingArtist = CreditFormatter.Join(match.Track.Credits),
                RecordingLengthSeconds = match.Track.LengthSeconds,
                MatchScore = 1,
                MatchSource = "manual",
                Release = new ReleaseAppearance
                {
                    ReleaseId = release.ReleaseId, Title = release.Title, ReleaseGroupId = release.ReleaseGroupId,
                    PrimaryType = release.PrimaryType, SecondaryTypes = release.SecondaryTypes, Status = release.Status,
                    Country = release.Country, Date = release.Date, ArtistString = CreditFormatter.Join(release.Credits),
                    Formats = release.Media.Select(m => m.Format).OfType<string>().Distinct().ToList(),
                    TrackPosition = match.Track.Position, TrackCount = match.Medium.Tracks.Count, MediumPosition = match.Medium.Position,
                },
            };
            file.Candidates = [candidate, .. file.Candidates.Where(c => c.Key != candidate.Key)];
            file.SelectedCandidateKey = candidate.Key;
            file.Decision = DecisionSource.Manual;
            await pipeline.BuildFinalTagsAsync(file, candidate, settings, ct);
            Approve(file, $"Release \"{release.Title}\" manuell zugewiesen");
        }

        await db.SaveChangesAsync(ct);
        foreach (var f in files.Where(f => f.Status == FileStatus.Approved))
            queue.Enqueue(new WorkItem(WorkKind.WriteFile, f.Id));
        notifier.Notify();
        return problems;
    }

    /// <summary>
    /// Takes the tags of a Spotify/Deezer match (the given one, or the best of each file) incl. cover.
    /// Returns files without a match.
    /// </summary>
    public async Task<List<string>> ApplyExternalAsync(IReadOnlyList<int> fileIds, string? matchKey = null, CancellationToken ct = default)
    {
        var problems = new List<string>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var files = await db.Files.Where(f => fileIds.Contains(f.Id)).ToListAsync(ct);
        foreach (var file in files)
        {
            var match = matchKey != null
                ? file.ExternalMatches.FirstOrDefault(m => m.Key == matchKey)
                : file.ExternalMatches.OrderByDescending(m => m.Score).FirstOrDefault();
            if (match == null)
            {
                problems.Add($"{file.FileName}: kein Treffer aus Spotify/Deezer");
                continue;
            }

            await pipeline.BuildManualTagsAsync(file, ExternalSourceService.ToTagSet(match), ct);
            file.ExternalSource = match.Source;
            file.Decision = DecisionSource.External;
            if (match.CoverUrl != null && await coverArt.DownloadAsync(match.CoverUrl, MatchingPipeline.SourceName(match.Source), ct) is { } cover)
            {
                Directory.CreateDirectory(Path.Combine(storage.WorkRoot, "covers"));
                file.ManualCoverFile = Path.Combine(storage.WorkRoot, "covers", $"{Guid.NewGuid():N}.img");
                await File.WriteAllBytesAsync(file.ManualCoverFile, cover.Data, ct);
            }

            Approve(file, $"Tags aus {MatchingPipeline.SourceName(match.Source)} übernommen: {match.Title} ({match.Album})");
        }

        await db.SaveChangesAsync(ct);
        foreach (var f in files.Where(f => f.Status == FileStatus.Approved))
            queue.Enqueue(new WorkItem(WorkKind.WriteFile, f.Id));
        notifier.Notify();
        return problems;
    }

    /// <summary>Manual tagging (fallback when the recording is not in MusicBrainz).</summary>
    public async Task ApplyManualTagsAsync(IReadOnlyList<(int FileId, TagSet Tags)> edits, byte[]? cover, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        string? coverPath = null;
        if (cover != null)
        {
            Directory.CreateDirectory(Path.Combine(storage.WorkRoot, "covers"));
            coverPath = Path.Combine(storage.WorkRoot, "covers", $"{Guid.NewGuid():N}.img");
            await File.WriteAllBytesAsync(coverPath, cover, ct);
        }

        var ids = edits.Select(e => e.FileId).ToList();
        var files = await db.Files.Where(f => ids.Contains(f.Id)).ToListAsync(ct);
        foreach (var (fileId, tags) in edits)
        {
            var file = files.First(f => f.Id == fileId);
            await pipeline.BuildManualTagsAsync(file, tags, ct);
            file.ExternalSource = null;
            if (coverPath != null) file.ManualCoverFile = coverPath;
            Approve(file, "Manuell getaggt");
        }

        await db.SaveChangesAsync(ct);
        foreach (var f in files)
            queue.Enqueue(new WorkItem(WorkKind.WriteFile, f.Id));
        notifier.Notify();
    }

    /// <summary>
    /// Decision in the duplicate view. Keep new: the library file goes to the trash and the new one
    /// takes its path. Keep existing: the new file goes to the trash. Keep both: filed next to it.
    /// </summary>
    public async Task ResolveDuplicateAsync(int fileId, DuplicateDecision decision, int? libraryEntryId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var file = await db.Files.FirstAsync(f => f.Id == fileId, ct);
        if (file.FinalTags == null) throw new InvalidOperationException("Keine Tags zum Schreiben vorhanden");

        switch (decision)
        {
            case DuplicateDecision.KeepExisting:
                var path = storage.InboxPath(file.RelativePath);
                if (File.Exists(path))
                    file.TrashPath = storage.MoveToTrash(path, file.RelativePath);
                file.DuplicateDecision = decision;
                MatchingPipeline.SetStatus(file, FileStatus.Discarded, "Duplikat – vorhandene Version behalten");
                MatchingPipeline.AddEvent(file, "Duplikat: vorhandene Version behalten, neue Datei in den Papierkorb");
                await db.SaveChangesAsync(ct);
                notifier.Notify();
                return;

            case DuplicateDecision.KeepNew:
                var match = file.Duplicates.FirstOrDefault(d => d.LibraryEntryId == libraryEntryId)
                            ?? file.Duplicates.FirstOrDefault(d => d.Kind == DuplicateKind.Duplicate)
                            ?? throw new InvalidOperationException("Keine vorhandene Datei zum Ersetzen angegeben");
                file.ReplaceLibraryPath = match.RelativePath;
                file.DuplicateDecision = decision;
                Approve(file, $"Duplikat: neue Version ersetzt {match.RelativePath}");
                break;

            case DuplicateDecision.KeepBoth:
                file.DuplicateDecision = decision;
                Approve(file, "Duplikat: beide Versionen behalten");
                break;
        }

        await db.SaveChangesAsync(ct);
        queue.Enqueue(new WorkItem(WorkKind.WriteFile, file.Id));
        notifier.Notify();
    }

    /// <summary>Processes a file again from the beginning.</summary>
    public async Task RetryAsync(int fileId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var file = await db.Files.FirstAsync(f => f.Id == fileId, ct);
        MatchingPipeline.SetStatus(file, FileStatus.Queued, null);
        file.SelectedCandidateKey = null;
        file.Decision = DecisionSource.None;
        file.AiDecision = null;
        file.Duplicates = [];
        file.DuplicateDecision = null;
        file.ReplaceLibraryPath = null;
        MatchingPipeline.AddEvent(file, "Erneute Verarbeitung angestoßen");
        await db.SaveChangesAsync(ct);
        queue.Enqueue(new WorkItem(WorkKind.ProcessGroup, file.GroupId));
        notifier.Notify();
    }

    /// <summary>Moves the file into the trash without filing it.</summary>
    public async Task DiscardAsync(int fileId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var file = await db.Files.FirstAsync(f => f.Id == fileId, ct);
        var path = storage.InboxPath(file.RelativePath);
        if (File.Exists(path))
            file.TrashPath = storage.MoveToTrash(path, file.RelativePath);
        MatchingPipeline.SetStatus(file, FileStatus.Discarded, "Verworfen");
        MatchingPipeline.AddEvent(file, "Verworfen (in den Papierkorb verschoben)");
        await db.SaveChangesAsync(ct);
        notifier.Notify();
    }

    private static void Approve(TrackFile file, string message)
    {
        MatchingPipeline.SetStatus(file, FileStatus.Approved, null);
        MatchingPipeline.AddEvent(file, message);
    }
}
