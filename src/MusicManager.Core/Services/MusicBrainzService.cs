using System.Collections.Concurrent;
using System.Reflection;
using MetaBrainz.MusicBrainz;
using MetaBrainz.MusicBrainz.Interfaces.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MusicManager.Core.Configuration;
using MusicManager.Core.Model;

namespace MusicManager.Core.Services;

/// <summary>
/// MusicBrainz access via MetaBrainz.MusicBrainz. All requests are serialized; the library
/// itself enforces the rate limit (one request per second). Results are cached in memory.
/// </summary>
public sealed class MusicBrainzService : IDisposable
{
    private readonly Query _query;
    private readonly ILogger<MusicBrainzService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<string, object> _cache = new();

    public MusicBrainzService(IOptions<MusicManagerOptions> options, ILogger<MusicBrainzService> logger)
    {
        _logger = logger;
        var o = options.Value;
        if (!string.IsNullOrWhiteSpace(o.MusicBrainzServer))
        {
            var uri = new Uri(o.MusicBrainzServer.Contains("://") ? o.MusicBrainzServer : "https://" + o.MusicBrainzServer);
            Query.DefaultServer = uri.Host;
            Query.DefaultPort = uri.Port;
            Query.DefaultUrlScheme = uri.Scheme;
        }

        // Slightly above the documented 1 request/second to stay clear of 503 responses.
        Query.DelayBetweenRequests = 1.2;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        _query = new Query("MusicManager", version, o.MusicBrainzContact);
    }

    private async Task<T> Run<T>(string cacheKey, Func<Task<T>> fn, CancellationToken ct) where T : class
    {
        if (_cache.TryGetValue(cacheKey, out var cached))
            return (T)cached;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(cacheKey, out cached))
                return (T)cached;

            Exception? last = null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    var result = await fn();
                    _cache[cacheKey] = result;
                    return result;
                }
                catch (Exception ex) when (IsTransient(ex) && !ct.IsCancellationRequested)
                {
                    // 503 = rate limit / server busy; back off and try again.
                    last = ex;
                    _logger.LogWarning("MusicBrainz request {Key} failed (attempt {Attempt}): {Message}", cacheKey, attempt + 1, ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(5 * (attempt + 1)), ct);
                }
            }

            throw last!;
        }
        finally
        {
            _lock.Release();
        }
    }

    // --- Recordings ----------------------------------------------------------------------

    /// <summary>Upper limit of releases per recording (browse requests of 100 each).</summary>
    private const int MaxBrowsedReleases = 300;

    public async Task<RecordingMatch> GetRecordingAsync(string recordingId, double matchScore, string source, CancellationToken ct)
    {
        var rec = await Run($"rec:{recordingId}", async () =>
        {
            var r = await _query.LookupRecordingAsync(Guid.Parse(recordingId),
                Include.Releases | Include.ReleaseGroups | Include.Media | Include.ArtistCredits, cancellationToken: ct);
            var match = MapRecording(r, 0, "");

            // The lookup returns at most 25 releases; popular songs appear on many more, and the
            // original album may be missing. Browse the rest (without track positions).
            if (match.Releases.Count >= 25)
            {
                var known = match.Releases.Select(x => x.ReleaseId).ToHashSet();
                for (var offset = 0; offset < MaxBrowsedReleases; offset += 100)
                {
                    var page = await _query.BrowseRecordingReleasesAsync(r.Id, 100, offset, Include.ReleaseGroups | Include.Media | Include.ArtistCredits,
                        cancellationToken: ct);
                    foreach (var rel in page.Results.Where(x => known.Add(x.Id.ToString())))
                        match.Releases.Add(MapAppearance(rel));
                    if (offset + 100 >= page.TotalResults) break;
                }
            }

            return match;
        }, ct);
        return WithScore(rec, matchScore, source);
    }

    private static RecordingMatch WithScore(RecordingMatch r, double score, string source) => new()
    {
        RecordingId = r.RecordingId, Title = r.Title, Credits = r.Credits, LengthSeconds = r.LengthSeconds,
        Releases = r.Releases, MatchScore = score, Source = source, IsVideo = r.IsVideo,
    };

    private static RecordingMatch MapRecording(IRecording r, double score, string source) => new()
    {
        RecordingId = r.Id.ToString(),
        Title = r.Title ?? "",
        IsVideo = r.Video,
        Credits = MapCredits(r.ArtistCredit),
        LengthSeconds = r.Length?.TotalSeconds,
        MatchScore = score,
        Source = source,
        Releases = (r.Releases ?? []).Select(MapAppearance).ToList(),
    };

    private static ReleaseAppearance MapAppearance(IRelease rel)
    {
        var medium = rel.Media?.FirstOrDefault(m => m.Tracks?.Count > 0) ?? rel.Media?.FirstOrDefault();
        var track = medium?.Tracks?.FirstOrDefault();
        return new ReleaseAppearance
        {
                ReleaseId = rel.Id.ToString(),
                Title = rel.Title ?? "",
                ReleaseGroupId = rel.ReleaseGroup?.Id.ToString(),
                PrimaryType = rel.ReleaseGroup?.PrimaryType,
                SecondaryTypes = rel.ReleaseGroup?.SecondaryTypes?.ToList() ?? [],
                Status = rel.Status,
                Country = rel.Country,
                Date = rel.Date?.ToString(),
                Formats = rel.Media?.Select(m => m.Format).OfType<string>().Distinct().ToList() ?? [],
                ArtistString = rel.ArtistCredit == null ? null : CreditFormatter.Join(MapCredits(rel.ArtistCredit)),
                TrackPosition = track?.Position,
                TrackCount = medium?.TrackCount,
                MediumPosition = medium?.Position,
        };
    }

    /// <summary>Server busy, rate limited or network problems – worth trying again later.</summary>
    public static bool IsTransient(Exception? ex)
    {
        for (; ex != null; ex = ex.InnerException)
        {
            if (ex is HttpRequestException or TaskCanceledException) return true;
            if (ex is MetaBrainz.Common.HttpError { Status: System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.TooManyRequests
                    or System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.GatewayTimeout })
                return true;
        }

        return false;
    }

    /// <summary>Text search for recordings (fallback when the fingerprint gives no result).</summary>
    public Task<List<RecordingSearchHit>> SearchRecordingsAsync(string? artist, string title, string? album, int limit, CancellationToken ct)
    {
        var q = $"recording:\"{Escape(title)}\"";
        if (!string.IsNullOrWhiteSpace(artist))
            q += $" AND artist:\"{Escape(artist)}\"";
        if (!string.IsNullOrWhiteSpace(album))
            q += $" AND release:\"{Escape(album)}\"";
        return Run($"search-rec:{q}:{limit}", async () =>
        {
            var results = await _query.FindRecordingsAsync(q, limit, cancellationToken: ct);
            return results.Results.Select(r => new RecordingSearchHit(
                r.Item.Id.ToString(), r.Score, r.Item.Title ?? "", r.Item.Length?.TotalSeconds, r.Item.Video, r.Item.Releases?.Count ?? 0)).ToList();
        }, ct);
    }

    /// <summary>Recordings with this ISRC (via the search index).</summary>
    public Task<List<string>> SearchIsrcAsync(string isrc, CancellationToken ct) =>
        Run($"isrc:{isrc}", async () =>
        {
            var results = await _query.FindRecordingsAsync($"isrc:{isrc}", 10, cancellationToken: ct);
            return results.Results.Where(r => r.Score >= 95).Select(r => r.Item.Id.ToString()).ToList();
        }, ct);

    /// <summary>Release search for the manual search in the frontend.</summary>
    public Task<List<ReleaseAppearance>> SearchReleasesAsync(string query, int limit, CancellationToken ct) =>
        Run($"search-rel:{query}", async () =>
        {
            var results = await _query.FindReleasesAsync(query, limit, cancellationToken: ct);
            return results.Results.Select(r => new ReleaseAppearance
            {
                ReleaseId = r.Item.Id.ToString(),
                Title = r.Item.Title ?? "",
                ReleaseGroupId = r.Item.ReleaseGroup?.Id.ToString(),
                PrimaryType = r.Item.ReleaseGroup?.PrimaryType,
                SecondaryTypes = r.Item.ReleaseGroup?.SecondaryTypes?.ToList() ?? [],
                Status = r.Item.Status,
                Country = r.Item.Country,
                Date = r.Item.Date?.ToString(),
                Formats = r.Item.Media?.Select(m => m.Format).OfType<string>().Distinct().ToList() ?? [],
                ArtistString = r.Item.ArtistCredit == null ? null : CreditFormatter.Join(MapCredits(r.Item.ArtistCredit)),
                TrackCount = r.Item.Media?.Sum(m => m.TrackCount),
            }).ToList();
        }, ct);

    // --- Releases ------------------------------------------------------------------------

    public Task<ReleaseDetail> GetReleaseAsync(string releaseId, CancellationToken ct) =>
        Run($"rel:{releaseId}", async () =>
        {
            var r = await _query.LookupReleaseAsync(Guid.Parse(releaseId),
                Include.Recordings | Include.ArtistCredits | Include.ReleaseGroups | Include.Media | Include.Labels
                | Include.Genres | Include.Isrcs | Include.RecordingLevelRelationships | Include.ArtistRelationships, ct);

            var genres = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            AddGenres(genres, r.Genres);
            string? originalDate = null;
            if (r.ReleaseGroup != null)
            {
                var rg = await _query.LookupReleaseGroupAsync(r.ReleaseGroup.Id, Include.Genres, cancellationToken: ct);
                AddGenres(genres, rg.Genres);
                originalDate = rg.FirstReleaseDate?.ToString();
            }

            return new ReleaseDetail
            {
                ReleaseId = r.Id.ToString(),
                Title = r.Title ?? "",
                ReleaseGroupId = r.ReleaseGroup?.Id.ToString(),
                PrimaryType = r.ReleaseGroup?.PrimaryType,
                SecondaryTypes = r.ReleaseGroup?.SecondaryTypes?.ToList() ?? [],
                Status = r.Status,
                Country = r.Country,
                Date = r.Date?.ToString(),
                OriginalDate = string.IsNullOrEmpty(originalDate) ? null : originalDate,
                Label = r.LabelInfo?.Select(l => l.Label?.Name).FirstOrDefault(n => n != null),
                HasFrontCover = r.CoverArtArchive?.Front ?? false,
                Credits = MapCredits(r.ArtistCredit),
                Genres = genres,
                Media = (r.Media ?? []).Select(m => new MediumDetail
                {
                    Position = m.Position,
                    Format = m.Format,
                    Tracks = (m.Tracks ?? []).Where(t => t.Recording != null).Select(t => new TrackDetail
                    {
                        TrackId = t.Id.ToString(),
                        RecordingId = t.Recording!.Id.ToString(),
                        Position = t.Position ?? 0,
                        Title = t.Title ?? t.Recording.Title ?? "",
                        LengthSeconds = (t.Length ?? t.Recording.Length)?.TotalSeconds,
                        Credits = MapCredits(t.ArtistCredit ?? t.Recording.ArtistCredit),
                        Isrcs = t.Recording.Isrcs?.ToList() ?? [],
                        Remixers = (t.Recording.Relationships ?? [])
                            .Where(rel => rel.Type == "remixer" && rel.Artist != null)
                            .Select(rel => new CreditPart(rel.Artist!.Id.ToString(), rel.Artist.Name ?? "", rel.TargetCredit ?? rel.Artist.Name ?? "", ""))
                            .ToList(),
                    }).ToList(),
                }).ToList(),
            };
        }, ct);

    // --- Artists -------------------------------------------------------------------------

    public Task<ArtistInfo> GetArtistAsync(string artistId, CancellationToken ct) =>
        Run($"artist:{artistId}", async () =>
        {
            var a = await _query.LookupArtistAsync(Guid.Parse(artistId), Include.Aliases | Include.Genres, cancellationToken: ct);
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Like Picard: only the primary alias of a locale counts, never legal names or search hints
            // (otherwise "JAY-Z" becomes "Shawn Corey Carter").
            foreach (var alias in (a.Aliases ?? []).Where(x => x is { Primary: true, Locale: not null, Name: not null }
                         && x.Type is not ("Legal name" or "Search hint")))
                aliases.TryAdd(alias.Locale!, alias.Name!);

            var genres = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            AddGenres(genres, a.Genres);
            return new ArtistInfo { ArtistId = artistId, Name = a.Name ?? "", LocaleAliases = aliases, Genres = genres };
        }, ct);

    // --- Helpers -------------------------------------------------------------------------

    private static List<CreditPart> MapCredits(IEnumerable<INameCredit>? credits) =>
        (credits ?? []).Select(c => new CreditPart(
            c.Artist?.Id.ToString() ?? "",
            c.Artist?.Name ?? c.Name ?? "",
            c.Name ?? c.Artist?.Name ?? "",
            c.JoinPhrase ?? "")).ToList();

    private static void AddGenres(Dictionary<string, int> target, IEnumerable<IGenre>? genres)
    {
        foreach (var g in genres ?? [])
        {
            if (g.Name == null) continue;
            target[g.Name] = target.GetValueOrDefault(g.Name) + g.VoteCount;
        }
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public void Dispose()
    {
        _query.Dispose();
        _lock.Dispose();
    }
}
