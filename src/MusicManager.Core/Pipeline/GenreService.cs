using MusicManager.Core.Configuration;
using MusicManager.Core.Model;
using MusicManager.Core.Rules;
using MusicManager.Core.Services;

namespace MusicManager.Core.Pipeline;

/// <summary>
/// Finds the genres of a track. Sources in order, the first one with a result wins:
/// <list type="number">
/// <item>MusicBrainz recording (double weight) plus release and release group,</item>
/// <item>Last.fm tags of the track, then of the album – only names MusicBrainz knows as genres,</item>
/// <item>MusicBrainz genres of the credited artists,</item>
/// <item>the genres already in the file.</item>
/// </list>
/// Spelling is unified at the end (<see cref="GenreRules.Finish"/>).
/// </summary>
public sealed class GenreService(MusicBrainzService musicBrainz, LastFmClient lastFm)
{
    public sealed record Request(
        string? RecordingId,
        IReadOnlyDictionary<string, int> ReleaseGenres,
        IReadOnlyList<IReadOnlyDictionary<string, int>> ArtistGenres,
        string? Artist,
        string? Title,
        string? AlbumArtist,
        string? Album,
        IReadOnlyList<string> Existing);

    public sealed record Result(List<string> Genres, string? Source);

    public async Task<Result> ResolveAsync(Request r, AppSettings settings, CancellationToken ct)
    {
        if (!settings.UseGenres) return new Result([], null);

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
        if (picked.Count > 0) return Finish(picked, "MusicBrainz", settings);

        // 2. Last.fm: track, then album, filtered to real genres.
        if (settings.UseLastFmGenres && lastFm.Enabled && !string.IsNullOrWhiteSpace(r.Artist))
        {
            var known = await musicBrainz.GetGenreNamesAsync(ct);
            if (!string.IsNullOrWhiteSpace(r.Title))
            {
                picked = GenreRules.Select(LastFmVotes(await lastFm.GetTrackTagsAsync(r.Artist, r.Title, ct), known), settings);
                if (picked.Count > 0) return Finish(picked, "Last.fm (Titel)", settings);
            }

            if (!string.IsNullOrWhiteSpace(r.Album))
            {
                picked = GenreRules.Select(LastFmVotes(await lastFm.GetAlbumTagsAsync(r.AlbumArtist ?? r.Artist, r.Album, ct), known), settings);
                if (picked.Count > 0) return Finish(picked, "Last.fm (Album)", settings);
            }
        }

        // 3. MusicBrainz artist genres – broad, so only when nothing more specific exists.
        if (settings.UseArtistGenres && r.ArtistGenres.Count > 0)
        {
            votes.Clear();
            foreach (var a in r.ArtistGenres) GenreRules.Add(votes, a);
            picked = GenreRules.Select(votes, settings);
            if (picked.Count > 0) return Finish(picked, "MusicBrainz (Künstler)", settings);
        }

        // 4. Keep what the file already has.
        if (settings.KeepOriginalGenres && r.Existing.Count > 0)
            return Finish(r.Existing.Take(settings.MaxGenres), "vorhandene Tags", settings);

        return new Result([], null);
    }

    private static Dictionary<string, double> LastFmVotes(IEnumerable<KeyValuePair<string, int>> tags, HashSet<string> known)
    {
        var votes = new Dictionary<string, double>(StringComparer.Ordinal);
        GenreRules.Add(votes, tags);
        foreach (var key in votes.Keys.Where(k => !known.Contains(k)).ToList())
            votes.Remove(key);
        return votes;
    }

    private static Result Finish(IEnumerable<string> keys, string source, AppSettings settings) =>
        new(GenreRules.Finish(keys, settings), source);
}
