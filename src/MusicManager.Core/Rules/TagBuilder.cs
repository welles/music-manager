using System.Globalization;
using MusicManager.Core.Configuration;
using MusicManager.Core.Model;

namespace MusicManager.Core.Rules;

/// <summary>
/// Builds the final tags for a recording on a MusicBrainz release. Artist lists come from the
/// structured artist credits; names are translated/standardized and forced replacements are
/// applied per credit before the display strings are assembled.
/// </summary>
public static class TagBuilder
{
    public static TagSet Build(ReleaseDetail release, string recordingId, IReadOnlyDictionary<string, ArtistInfo> artists,
        AppSettings settings, ArtistRuleSet rules)
    {
        var (medium, track) = FindTrack(release, recordingId)
                              ?? throw new InvalidOperationException($"Recording {recordingId} ist nicht auf Release {release.ReleaseId}");

        string Name(CreditPart c)
        {
            var name = c.CreditedName;
            if (settings.StandardizeArtists)
            {
                name = c.Name;
                if (!string.IsNullOrWhiteSpace(settings.ArtistLocale)
                    && artists.TryGetValue(c.ArtistId, out var info)
                    && info.LocaleAliases.TryGetValue(settings.ArtistLocale, out var alias))
                {
                    name = alias;
                }
            }

            return rules.ReplaceName(name);
        }

        var t = new TagSet
        {
            Title = track.Title,
            Artist = CreditFormatter.Join(track.Credits, Name),
            Artists = track.Credits.Select(Name).ToList(),
            Album = release.Title,
            AlbumArtist = CreditFormatter.Join(release.Credits, Name),
            AlbumArtists = release.Credits.Select(Name).ToList(),
            FolderArtist = release.Credits.Count > 0 ? Name(release.Credits[0]) : null,
            TrackNumber = track.Position,
            TrackCount = medium.Tracks.Count,
            DiscNumber = medium.Position,
            DiscCount = release.Media.Count,
            // Some releases have no date in MusicBrainz; the first release date is better than none.
            Date = release.Date.NullIfEmpty() ?? release.OriginalDate,
            OriginalDate = release.OriginalDate,
            Isrc = track.Isrcs.FirstOrDefault(),
            Label = release.Label,
            ReleaseCountry = release.Country,
            ReleaseStatus = release.Status?.ToLowerInvariant(),
            ReleaseType = string.Join("; ", new[] { release.PrimaryType }.Concat(release.SecondaryTypes).OfType<string>()).ToLowerInvariant().NullIfEmpty(),
            MbRecordingId = track.RecordingId,
            MbTrackId = track.TrackId,
            MbReleaseId = release.ReleaseId,
            MbReleaseGroupId = release.ReleaseGroupId,
            MbArtistIds = track.Credits.Select(c => c.ArtistId).Where(id => id.Length > 0).ToList(),
            MbAlbumArtistIds = release.Credits.Select(c => c.ArtistId).Where(id => id.Length > 0).ToList(),
            Genres = BuildGenres(release, track, artists, settings),
        };

        if (track.Remixers.Count > 0)
        {
            var remixers = track.Remixers.Select(Name).ToList();
            t.Remixer = string.Join(" & ", remixers);
            foreach (var r in remixers.Where(r => !t.Artists.Contains(r, StringComparer.OrdinalIgnoreCase)))
                t.Artists.Add(r);
        }

        return TaggingRules.Apply(t, rules, settings.ConvertPunctuation);
    }

    public static (MediumDetail Medium, TrackDetail Track)? FindTrack(ReleaseDetail release, string recordingId)
    {
        foreach (var m in release.Media)
        {
            var t = m.Tracks.FirstOrDefault(x => x.RecordingId == recordingId);
            if (t != null) return (m, t);
        }

        return null;
    }

    public static List<string> BuildGenres(ReleaseDetail release, TrackDetail track, IReadOnlyDictionary<string, ArtistInfo> artists,
        AppSettings settings)
    {
        if (!settings.UseGenres) return [];

        var counts = new Dictionary<string, int>(release.Genres, StringComparer.OrdinalIgnoreCase);
        if (settings.UseArtistGenres)
        {
            foreach (var id in track.Credits.Select(c => c.ArtistId).Concat(release.Credits.Select(c => c.ArtistId)).Distinct())
            {
                if (!artists.TryGetValue(id, out var info)) continue;
                foreach (var (g, c) in info.Genres)
                    counts[g] = counts.GetValueOrDefault(g) + c;
            }
        }

        var filtered = counts
            .Where(g => !settings.GenreFilter.Contains(g.Key, StringComparer.OrdinalIgnoreCase))
            .Where(g => g.Value > 0)
            .ToList();
        if (filtered.Count == 0) return [];

        var max = filtered.Max(g => g.Value);
        return filtered
            .Where(g => g.Value * 100 >= max * settings.MinGenreUsagePercent)
            .OrderByDescending(g => g.Value).ThenBy(g => g.Key)
            .Take(settings.MaxGenres)
            .Select(g => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(g.Key))
            .ToList();
    }
}
