using MusicManager.Core.Ai;
using MusicManager.Core.Configuration;
using MusicManager.Core.Model;
using MusicManager.Core.Rules;
using MusicManager.Core.Services;

namespace MusicManager.Core.Pipeline;

/// <summary>
/// Finds the genres of a track and records where they come from and how reliable they are
/// (<see cref="GenreOrigin"/>). Sources in order, the first one with a result wins:
/// <list type="number">
/// <item>MusicBrainz recording (double weight) plus release and release group – high,</item>
/// <item>Last.fm tags of the track, then of the album, only names MusicBrainz knows as genres – medium,</item>
/// <item>MusicBrainz genres of the credited artists – medium when Claude confirmed them, otherwise low,</item>
/// <item>the genres already in the file – with their recorded origin, otherwise medium,</item>
/// <item>Last.fm tags of the main artist by MusicBrainz ID – low; Claude drops tags of a namesake,</item>
/// <item>trial: genres Claude knows for the song – very low.</item>
/// </list>
/// Spelling is unified at the end (<see cref="GenreRules.Finish"/>).
/// </summary>
public sealed class GenreService(MusicBrainzService musicBrainz, LastFmClient lastFm, AiService ai)
{
    public sealed record Request(
        string? RecordingId,
        IReadOnlyDictionary<string, int> ReleaseGenres,
        IReadOnlyList<IReadOnlyDictionary<string, int>> ArtistGenres,
        string? Artist,
        string? Title,
        string? AlbumArtist,
        string? Album,
        IReadOnlyList<string> Existing,
        IReadOnlyList<string>? ArtistMbids = null,
        string? ExistingOrigin = null,
        string? Year = null);

    /// <param name="Origin">Value for <see cref="GenreOrigin.Marker"/>; null without genres.</param>
    /// <param name="Note">What happened, for the file's history (e.g. why Claude dropped genres).</param>
    public sealed record Result(List<string> Genres, string? Origin, string? Note = null);

    public async Task<Result> ResolveAsync(Request r, AppSettings settings, CancellationToken ct)
    {
        if (!settings.UseGenres) return new Result([], null);
        var notes = new List<string>();
        var song = new AiService.GenreContext(r.Artist, r.Title, r.Album, r.Year);

        // 1. MusicBrainz: recording and release.
        var votes = new Dictionary<string, double>(StringComparer.Ordinal);
        if (r.RecordingId != null)
        {
            try
            {
                GenreRules.Add(votes, await musicBrainz.GetRecordingGenresAsync(r.RecordingId, ct), weight: 2);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Recording genres are a refinement; the release genres still count.
            }
        }

        GenreRules.Add(votes, r.ReleaseGenres);
        var picked = GenreRules.Select(votes, settings);
        if (picked.Count > 0) return Done(picked, GenreOrigin.MusicBrainz, GenreConfidence.High);

        // 2. Last.fm: track, then album.
        HashSet<string>? known = null;
        var useLastFm = settings.UseLastFmGenres && lastFm.Enabled && !string.IsNullOrWhiteSpace(r.Artist);
        if (useLastFm)
        {
            known = await musicBrainz.GetGenreNamesAsync(ct);
            if (!string.IsNullOrWhiteSpace(r.Title))
            {
                picked = GenreRules.Select(LastFmVotes(await lastFm.GetTrackTagsAsync(r.Artist!, r.Title, ct), known), settings);
                if (picked.Count > 0) return Done(picked, GenreOrigin.LastFmTrack, GenreConfidence.Medium);
            }

            if (!string.IsNullOrWhiteSpace(r.Album))
            {
                picked = GenreRules.Select(LastFmVotes(await lastFm.GetAlbumTagsAsync(r.AlbumArtist ?? r.Artist!, r.Album, ct), known), settings);
                if (picked.Count > 0) return Done(picked, GenreOrigin.LastFmAlbum, GenreConfidence.Medium);
            }
        }

        // 3. MusicBrainz artist genres – broad, checked by Claude.
        if (settings.UseArtistGenres && r.ArtistGenres.Count > 0)
        {
            votes.Clear();
            foreach (var a in r.ArtistGenres) GenreRules.Add(votes, a);
            picked = GenreRules.Select(votes, settings);
            if (picked.Count > 0 && await CheckAsync(picked, GenreOrigin.MusicBrainzArtist) is { } checkedArtist)
                return Done(checkedArtist.Genres, GenreOrigin.MusicBrainzArtist, checkedArtist.Confirmed ? GenreConfidence.Medium : GenreConfidence.Low);
        }

        // 4. Keep what the file already has, with the origin it was written with.
        if (settings.KeepOriginalGenres && r.Existing.Count > 0)
        {
            var origin = GenreOrigin.Parse(r.ExistingOrigin);
            return new Result(GenreRules.Finish(r.Existing.Take(settings.MaxGenres), settings),
                origin != null ? GenreOrigin.Format(origin.Value.Source, origin.Value.Confidence) : GenreOrigin.Format(GenreOrigin.Existing, GenreConfidence.Medium),
                Note(origin != null ? null : "aus der Datei übernommen"));
        }

        // 5. Last.fm artist tags by MusicBrainz ID – Last.fm merges namesakes, so Claude checks them.
        if (useLastFm)
        {
            foreach (var mbid in (r.ArtistMbids ?? []).Where(id => Guid.TryParse(id, out _)).Take(2))
            {
                picked = GenreRules.Select(LastFmVotes(await lastFm.GetArtistTagsAsync(mbid, ct), known!), settings);
                if (picked.Count > 0 && await CheckAsync(picked, GenreOrigin.LastFmArtist) is { } checkedTags)
                    return Done(checkedTags.Genres, GenreOrigin.LastFmArtist, GenreConfidence.Low);
            }
        }

        // 6. Trial: Claude's own knowledge, only genres MusicBrainz knows.
        if (settings.AiGenreSuggestions && ai.IsConfigured && !string.IsNullOrWhiteSpace(r.Artist))
        {
            var suggestion = await ai.SuggestGenresAsync(song, settings, ct);
            known ??= await musicBrainz.GetGenreNamesAsync(ct);
            var genres = suggestion?.Genres.Select(GenreRules.Key).Where(known.Contains).Distinct().Take(settings.MaxGenres).ToList() ?? [];
            if (genres.Count > 0)
            {
                notes.Add($"KI-Vorschlag ({suggestion!.Confidence:P0}): {suggestion.Reason}");
                return Done(genres, GenreOrigin.Ai, GenreConfidence.VeryLow);
            }

            if (suggestion != null) notes.Add($"KI kennt keine Genres: {suggestion.Reason}");
        }

        return new Result([], null, Note(null));

        Result Done(IEnumerable<string> keys, string source, GenreConfidence confidence) =>
            new(GenreRules.Finish(keys, settings), GenreOrigin.Format(source, confidence), Note(null));

        string? Note(string? extra)
        {
            if (extra != null) notes.Add(extra);
            return notes.Count > 0 ? string.Join(" · ", notes) : null;
        }

        // Claude keeps the genres that fit; null = all dropped. Without the AI the genres stay unconfirmed.
        async Task<(List<string> Genres, bool Confirmed)?> CheckAsync(List<string> genres, string source)
        {
            if (!settings.AiGenreCheck || !ai.IsConfigured) return (genres, false);
            var verdict = await ai.CheckGenresAsync(song, genres, GenreOrigin.SourceLabel(source), settings, ct);
            if (verdict == null) return (genres, false);

            var dropped = genres.Except(verdict.Genres).ToList();
            if (dropped.Count > 0)
                notes.Add($"KI hat {string.Join(", ", dropped)} aus {GenreOrigin.SourceLabel(source)} verworfen: {verdict.Reason}");
            return verdict.Genres.Count > 0 ? (verdict.Genres, true) : null;
        }
    }

    private static Dictionary<string, double> LastFmVotes(IEnumerable<KeyValuePair<string, int>> tags, HashSet<string> known)
    {
        var votes = new Dictionary<string, double>(StringComparer.Ordinal);
        GenreRules.Add(votes, tags);
        foreach (var key in votes.Keys.Where(k => !known.Contains(k)).ToList())
            votes.Remove(key);
        return votes;
    }
}
