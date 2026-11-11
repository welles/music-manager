using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MusicManager.Core.Data;
using MusicManager.Core.Model;
using MusicManager.Core.Rules;
using MusicManager.Core.Services;

namespace MusicManager.Core.Pipeline;

/// <summary>Planned change for one library file.</summary>
public sealed class RetagPreview
{
    public int EntryId { get; init; }
    public required string RelativePath { get; init; }
    public string? NewRelativePath { get; set; }
    public List<TagField> Before { get; init; } = [];
    public List<TagField> After { get; set; } = [];
    public TagSet? NewTags { get; set; }

    /// <summary>Value for MUSICMANAGER_SOURCE after writing.</summary>
    public string? Source { get; set; }

    /// <summary>How the new tags were built (for display).</summary>
    public string? Method { get; set; }

    public string? Error { get; set; }
    public bool Applied { get; set; }

    /// <summary>
    /// Tags or cover of the file were changed outside Music Manager. Writing the preview accepts the current
    /// state (the edits are kept unless the preview was built with "discard outside edits").
    /// </summary>
    public bool EditedOutside { get; set; }

    /// <summary>The outside edits are thrown away: the tags are rebuilt from MusicBrainz.</summary>
    public bool DiscardsOutsideEdits { get; set; }

    /// <summary>The tags were edited outside and can be rebuilt from MusicBrainz (source musicbrainz with IDs).</summary>
    public bool CanDiscardOutsideEdits { get; set; }

    public bool PathChanged => NewRelativePath != null && NewRelativePath != RelativePath;

    public bool TagsChanged =>
        Before.Count != After.Count
        || Before.Any(b => After.FirstOrDefault(a => a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase)) is not { } a || !a.Values.SequenceEqual(b.Values));

    /// <summary>An outside edit counts as a change even when the tags stay the same: writing stores the new hash.</summary>
    public bool Changed => Error == null && (TagsChanged || PathChanged || EditedOutside);
}

public sealed record RetagState(bool Running, string? Phase, int Progress, int Total);

/// <summary>
/// Re-tags library files with the current rules: files tagged from MusicBrainz get fresh tags from
/// their release; manually / externally tagged files only get the rules applied to their tags again.
/// First a preview is computed, then the confirmed changes are written (tags only, no re-encoding).
/// One job at a time; the state lives in memory.
/// </summary>
public sealed class RetagService(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settingsService,
    MatchingPipeline pipeline,
    LibraryIndexer libraryIndexer,
    Storage storage,
    PipelineNotifier notifier,
    ILogger<RetagService> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _progress, _total;
    private string? _phase;

    public List<RetagPreview> Previews { get; private set; } = [];
    public RetagState State => new(_lock.CurrentCount == 0, _phase, _progress, _total);

    /// <summary>
    /// Computes the preview. <paramref name="edits"/> (manual editing) overrides fields of the current
    /// tags per entry before the rules are applied; edited files count as manually tagged afterwards.
    /// Files whose tags were changed outside the tool keep those edits: the rules are applied to their
    /// current tags instead of rebuilding them from MusicBrainz, unless <paramref name="discardOutsideEdits"/>.
    /// </summary>
    public async Task<bool> StartPreviewAsync(IReadOnlyList<int> entryIds, IReadOnlyDictionary<int, Func<TagSet, TagSet>>? edits = null,
        bool discardOutsideEdits = false)
    {
        if (!await _lock.WaitAsync(0)) return false;
        Previews = [];
        _ = Task.Run(async () =>
        {
            try
            {
                _phase = "Vorschau";
                _total = entryIds.Count;
                _progress = 0;
                var settings = await settingsService.GetAsync();
                var rules = await settingsService.GetArtistRulesAsync();
                var rulesVersion = await settingsService.GetRulesVersionAsync();
                await using var db = await dbFactory.CreateDbContextAsync();
                var entries = await db.Library.AsNoTracking().Where(e => entryIds.Contains(e.Id)).ToListAsync();
                var result = new List<RetagPreview>();
                foreach (var entry in entries)
                {
                    result.Add(await PreviewOneAsync(entry, edits?.GetValueOrDefault(entry.Id), settings, rules, rulesVersion, discardOutsideEdits));
                    _progress++;
                    if (_progress % 5 == 0) notifier.Notify();
                }

                Previews = result.OrderByDescending(p => p.Changed).ThenBy(p => p.RelativePath).ToList();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Retag preview failed");
            }
            finally
            {
                _phase = null;
                _lock.Release();
                notifier.Notify();
            }
        });
        return true;
    }

    private async Task<RetagPreview> PreviewOneAsync(LibraryEntry entry, Func<TagSet, TagSet>? edit,
        Configuration.AppSettings settings, ArtistRuleSet rules, string rulesVersion, bool discardOutsideEdits = false)
    {
        var path = storage.LibraryPath(entry.RelativePath);
        var preview = new RetagPreview { EntryId = entry.Id, RelativePath = entry.RelativePath, Before = File.Exists(path) ? TagFields.Read(path) : [] };
        try
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Datei nicht mehr vorhanden");
            if (!Path.GetExtension(path).Equals(".m4a", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Nur M4A-Dateien können neu getaggt werden");

            var current = TagIo.Read(path);
            var source = TagFields.Get(preview.Before, TagIo.SourceMarker).FirstOrDefault();
            // Checked on the file itself, the index may be older.
            var outside = TagIo.CheckHashes(path);
            preview.EditedOutside = outside?.Edited == true;
            var fromMusicBrainz = source?.StartsWith("musicbrainz") == true && current is { MbReleaseId: not null, MbRecordingId: not null };
            // Only tags from MusicBrainz can be rebuilt; for all other files the current tags are the only basis.
            preview.CanDiscardOutsideEdits = outside?.TagsEdited == true && fromMusicBrainz && edit == null;
            var keepOutsideEdits = outside?.TagsEdited == true && !(discardOutsideEdits && preview.CanDiscardOutsideEdits);
            TagSet tags;
            if (edit == null && !keepOutsideEdits && fromMusicBrainz)
            {
                tags = await pipeline.BuildReleaseTagsAsync(current.MbReleaseId, current.MbRecordingId, current, settings, CancellationToken.None);
                preview.DiscardsOutsideEdits = outside?.TagsEdited == true;
                preview.Method = preview.DiscardsOutsideEdits ? "Neu aus MusicBrainz, externe Änderungen verworfen" : "Neu aus MusicBrainz";
            }
            else
            {
                var basis = current.Clone();
                basis.FolderArtist = null;
                if (edit != null)
                {
                    basis = edit(basis);
                    source = "manual";
                    preview.Method = "Bearbeitet";
                }
                else if (keepOutsideEdits)
                {
                    // The edited tags are no longer pure MusicBrainz tags: as "manual" a later retag keeps them too.
                    if (source?.StartsWith("musicbrainz") == true) source = "manual";
                    preview.Method = discardOutsideEdits
                        ? "Externe Änderungen behalten (nicht aus MusicBrainz, nicht verwerfbar), Regeln angewendet"
                        : "Externe Änderungen behalten, Regeln angewendet";
                }
                else
                {
                    preview.Method = "Regeln neu angewendet";
                }

                tags = TaggingRules.Apply(basis, rules, settings.ConvertPunctuation);
            }

            tags.AcoustId ??= current.AcoustId;
            await pipeline.TranslateTagsAsync(tags, settings, CancellationToken.None);

            // The cover stays as it is – keep the description so it does not show up as a change.
            var cover = preview.Before.FirstOrDefault(f => f.Name == "COVER")?.Values.FirstOrDefault();
            preview.NewTags = tags;
            preview.Source = source ?? "manual";
            preview.After = TagFields.ForFinalTags(tags, cover, preview.Source, rulesVersion);
            // The writer keeps song ID, added date, favorite and rating of the file. The tag hashes are
            // internal: they are rewritten, but not shown as a change.
            preview.After.AddRange(preview.Before.Where(b =>
                Model.FileMarkers.TagNames.Contains(b.Name, StringComparer.OrdinalIgnoreCase) || TagHashes.TagNames.Contains(b.Name, StringComparer.OrdinalIgnoreCase)));
            preview.NewRelativePath = PathBuilder.Build(tags);
        }
        catch (Exception ex)
        {
            preview.Error = ex.Message;
        }

        return preview;
    }

    /// <summary>Writes the selected previews. With <paramref name="movePaths"/> files are moved to the new path.</summary>
    public async Task<bool> StartApplyAsync(IReadOnlyCollection<RetagPreview> selected, bool movePaths)
    {
        if (!await _lock.WaitAsync(0)) return false;
        _ = Task.Run(async () =>
        {
            try
            {
                _phase = "Schreiben";
                _total = selected.Count;
                _progress = 0;
                var rulesVersion = await settingsService.GetRulesVersionAsync();
                foreach (var p in selected.Where(p => p.Changed && p.NewTags != null))
                {
                    try
                    {
                        await ApplyOneAsync(p, movePaths, rulesVersion);
                        p.Applied = true;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Retag of {Path} failed", p.RelativePath);
                        p.Error = ex.Message;
                    }

                    _progress++;
                    notifier.Notify();
                }
            }
            finally
            {
                _phase = null;
                _lock.Release();
                notifier.Notify();
            }
        });
        return true;
    }

    private async Task ApplyOneAsync(RetagPreview p, bool movePaths, string rulesVersion)
    {
        var path = storage.LibraryPath(p.RelativePath);
        var cover = TagIo.ReadEmbeddedCover(path);
        TagIo.WriteM4a(path, p.NewTags!, cover, p.Source ?? "manual", rulesVersion);

        var finalPath = p.RelativePath;
        if (movePaths && p.PathChanged)
        {
            var target = storage.LibraryPath(p.NewRelativePath!);
            if (File.Exists(target))
                throw new InvalidOperationException($"Tags geschrieben, aber nicht verschoben: {p.NewRelativePath} existiert bereits");
            Storage.MovePreservingTime(path, target);
            Storage.DeleteEmptyDirectories(Path.GetDirectoryName(path)!, storage.LibraryRoot);
            finalPath = p.NewRelativePath!;
        }

        // A moved file keeps its index entry identity (song ID, added date).
        await libraryIndexer.UpsertAsync(finalPath, null, CancellationToken.None, replaces: finalPath != p.RelativePath ? p.RelativePath : null);
    }
}
