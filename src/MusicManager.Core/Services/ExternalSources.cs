using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MusicManager.Core.Configuration;
using MusicManager.Core.Model;
using MusicManager.Core.Rules;

namespace MusicManager.Core.Services;

/// <summary>Spotify Web API (client credentials, catalog search only).</summary>
public sealed class SpotifyClient(HttpClient http, IOptions<MusicManagerOptions> options, ILogger<SpotifyClient> logger)
{
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTime _tokenExpires;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.Value.SpotifyClientId) && !string.IsNullOrWhiteSpace(options.Value.SpotifyClientSecret);

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token != null && DateTime.UtcNow < _tokenExpires) return _token;

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }),
            };
            var basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{options.Value.SpotifyClientId}:{options.Value.SpotifyClientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            _token = doc.RootElement.GetProperty("access_token").GetString();
            _tokenExpires = DateTime.UtcNow.AddSeconds(doc.RootElement.GetProperty("expires_in").GetInt32() - 60);
            return _token!;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public async Task<List<ExternalMatch>> SearchAsync(string? artist, string title, CancellationToken ct)
    {
        if (!IsConfigured) return [];

        // Field filters first, plain text as fallback (the search returns at most 10 results).
        var queries = new List<string> { string.IsNullOrWhiteSpace(artist) ? $"track:\"{title}\"" : $"track:\"{title}\" artist:\"{artist}\"" };
        queries.Add($"{artist} {title}".Trim());

        foreach (var q in queries)
        {
            var url = $"https://api.spotify.com/v1/search?type=track&limit=10&q={Uri.EscapeDataString(q)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Spotify search failed: {Status}", response.StatusCode);
                throw new HttpRequestException($"Spotify: HTTP {(int)response.StatusCode}");
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var matches = doc.RootElement.GetProperty("tracks").GetProperty("items").EnumerateArray().Select(Map).ToList();
            if (matches.Count > 0) return matches;
        }

        return [];
    }

    /// <summary>Loads one track by its Spotify ID (e.g. from a URL in the file's tags).</summary>
    public async Task<ExternalMatch?> GetTrackAsync(string id, CancellationToken ct)
    {
        if (!IsConfigured) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.spotify.com/v1/tracks/{Uri.EscapeDataString(id)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return Map(doc.RootElement);
    }

    /// <summary>Image URL of the artist with exactly this name (normalized), or null.</summary>
    public async Task<string?> FindArtistImageAsync(string name, CancellationToken ct)
    {
        if (!IsConfigured) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.spotify.com/v1/search?type=artist&limit=5&q={Uri.EscapeDataString(name)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        foreach (var a in doc.RootElement.GetProperty("artists").GetProperty("items").EnumerateArray())
        {
            if (TextUtil.Normalize(a.GetProperty("name").GetString()) != TextUtil.Normalize(name)) continue;
            if (a.TryGetProperty("images", out var images) && images.GetArrayLength() > 0)
                return images[0].GetProperty("url").GetString();
        }

        return null;
    }

    private static ExternalMatch Map(JsonElement t)
    {
        var album = t.GetProperty("album");
        return new ExternalMatch
        {
            Source = "spotify",
            Id = t.GetProperty("id").GetString()!,
            Url = t.TryGetProperty("external_urls", out var u) && u.TryGetProperty("spotify", out var s) ? s.GetString() : null,
            Title = t.GetProperty("name").GetString() ?? "",
            Artists = t.GetProperty("artists").EnumerateArray().Select(a => a.GetProperty("name").GetString() ?? "").ToList(),
            Album = album.GetProperty("name").GetString(),
            AlbumArtists = album.GetProperty("artists").EnumerateArray().Select(a => a.GetProperty("name").GetString() ?? "").ToList(),
            AlbumType = album.TryGetProperty("album_type", out var at) ? at.GetString() : null,
            ReleaseDate = album.TryGetProperty("release_date", out var rd) ? rd.GetString() : null,
            TrackNumber = t.TryGetProperty("track_number", out var tn) ? tn.GetInt32() : null,
            TrackCount = album.TryGetProperty("total_tracks", out var tt) ? tt.GetInt32() : null,
            DiscNumber = t.TryGetProperty("disc_number", out var dn) ? dn.GetInt32() : null,
            DurationSeconds = t.TryGetProperty("duration_ms", out var d) ? d.GetInt32() / 1000.0 : null,
            Isrc = t.TryGetProperty("external_ids", out var ids) && ids.TryGetProperty("isrc", out var isrc) ? isrc.GetString() : null,
            CoverUrl = album.TryGetProperty("images", out var imgs) && imgs.GetArrayLength() > 0 ? imgs[0].GetProperty("url").GetString() : null,
        };
    }
}

/// <summary>Deezer public API (no key). Track details add roles (Main/Featured), label and genres.</summary>
public sealed class DeezerClient(HttpClient http, ILogger<DeezerClient> logger)
{
    public async Task<List<ExternalMatch>> SearchAsync(string? artist, string title, CancellationToken ct)
    {
        var url = $"https://api.deezer.com/search?limit=10&q={Uri.EscapeDataString($"{artist} {title}".Trim())}";
        using var doc = JsonDocument.Parse(await http.GetStringAsync(url, ct));
        if (doc.RootElement.TryGetProperty("error", out var err))
        {
            logger.LogWarning("Deezer error: {Error}", err.ToString());
            throw new HttpRequestException("Deezer: " + err);
        }

        return doc.RootElement.GetProperty("data").EnumerateArray().Select(t => new ExternalMatch
        {
            Source = "deezer",
            Id = t.GetProperty("id").GetInt64().ToString(),
            Url = t.TryGetProperty("link", out var l) ? l.GetString() : null,
            Title = t.GetProperty("title").GetString() ?? "",
            Artists = [t.GetProperty("artist").GetProperty("name").GetString() ?? ""],
            Album = t.GetProperty("album").GetProperty("title").GetString(),
            DurationSeconds = t.TryGetProperty("duration", out var d) ? d.GetInt32() : null,
            Isrc = t.TryGetProperty("isrc", out var i) ? i.GetString() : null,
            CoverUrl = t.GetProperty("album").TryGetProperty("cover_xl", out var c) ? c.GetString() : null,
        }).ToList();
    }

    /// <summary>Image URL of the artist with exactly this name (normalized), or null. Deezer's empty placeholder is ignored.</summary>
    public async Task<string?> FindArtistImageAsync(string name, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.deezer.com/search/artist?limit=5&q={Uri.EscapeDataString(name)}", ct));
        if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
        foreach (var a in data.EnumerateArray())
        {
            if (TextUtil.Normalize(a.GetProperty("name").GetString()) != TextUtil.Normalize(name)) continue;
            var url = a.TryGetProperty("picture_xl", out var p) ? p.GetString() : null;
            // Artists without picture get a generic image whose path has an empty hash ("/artist//").
            return url == null || url.Contains("/artist//") ? null : url;
        }

        return null;
    }

    /// <summary>Completes a search hit with track position, contributors and album data.</summary>
    public async Task<ExternalMatch> DetailsAsync(ExternalMatch m, CancellationToken ct) =>
        await GetTrackAsync(m.Id, ct) is { } full ? WithScore(full, m.Score) : m;

    private static ExternalMatch WithScore(ExternalMatch m, double score)
    {
        m.Score = score;
        return m;
    }

    /// <summary>Loads one track by its Deezer ID, including contributors and album data.</summary>
    public async Task<ExternalMatch?> GetTrackAsync(string id, CancellationToken ct)
    {
        using var track = JsonDocument.Parse(await http.GetStringAsync($"https://api.deezer.com/track/{Uri.EscapeDataString(id)}", ct));
        var t = track.RootElement;
        if (t.TryGetProperty("error", out _)) return null;
        var contributors = t.TryGetProperty("contributors", out var cs)
            ? cs.EnumerateArray().Select(c => (Name: c.GetProperty("name").GetString() ?? "", Role: c.TryGetProperty("role", out var r) ? r.GetString() : null)).ToList()
            : [];
        var albumId = t.GetProperty("album").GetProperty("id").GetInt64();
        using var album = JsonDocument.Parse(await http.GetStringAsync($"https://api.deezer.com/album/{albumId}", ct));
        var a = album.RootElement;

        var main = contributors.Where(c => c.Role != "Featured").Select(c => c.Name).ToList();
        var tAlbum = t.GetProperty("album");
        return new ExternalMatch
        {
            Source = "deezer",
            Id = id,
            Url = t.TryGetProperty("link", out var link) ? link.GetString() : null,
            Title = t.GetProperty("title").GetString() ?? "",
            Artists = main.Count > 0 ? main : [t.GetProperty("artist").GetProperty("name").GetString() ?? ""],
            FeaturedArtists = contributors.Where(c => c.Role == "Featured").Select(c => c.Name).ToList(),
            Album = tAlbum.GetProperty("title").GetString(),
            AlbumArtists = a.TryGetProperty("artist", out var aa) ? [aa.GetProperty("name").GetString() ?? ""] : [],
            AlbumType = a.TryGetProperty("record_type", out var rt) ? rt.GetString() : null,
            ReleaseDate = a.TryGetProperty("release_date", out var rd) ? rd.GetString() : null,
            TrackNumber = t.TryGetProperty("track_position", out var tp) ? tp.GetInt32() : null,
            TrackCount = a.TryGetProperty("nb_tracks", out var nb) ? nb.GetInt32() : null,
            DiscNumber = t.TryGetProperty("disk_number", out var dn) ? dn.GetInt32() : null,
            DurationSeconds = t.TryGetProperty("duration", out var du) ? du.GetInt32() : null,
            Isrc = t.TryGetProperty("isrc", out var isrc) ? isrc.GetString() : null,
            Label = a.TryGetProperty("label", out var lb) ? lb.GetString() : null,
            Genres = a.TryGetProperty("genres", out var g) && g.TryGetProperty("data", out var gd)
                ? gd.EnumerateArray().Select(x => x.GetProperty("name").GetString() ?? "").ToList()
                : [],
            CoverUrl = tAlbum.TryGetProperty("cover_xl", out var cover) ? cover.GetString() : null,
        };
    }
}

/// <summary>What YouTube says about a video: no artist/title fields, only how the uploader described it.</summary>
public sealed record YouTubeVideo(string Id, string Title, string Channel, string? PublishedAt, string Description, double? DurationSeconds,
    string? ThumbnailUrl);

/// <summary>YouTube Data API v3 (API key): one video by its ID, for a YouTube link in a file's tags.</summary>
public sealed class YouTubeClient(HttpClient http, IOptions<MusicManagerOptions> options)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.YouTubeApiKey);

    /// <summary>The video, or null when YouTube does not know it (deleted, private).</summary>
    public async Task<YouTubeVideo?> GetVideoAsync(string id, CancellationToken ct)
    {
        var url = $"https://www.googleapis.com/youtube/v3/videos?part=snippet,contentDetails&id={Uri.EscapeDataString(id)}" +
                  $"&key={Uri.EscapeDataString(options.Value.YouTubeApiKey ?? "")}";
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Google explains the problem (key invalid, API not enabled, quota) in error.message.
            var body = await response.Content.ReadAsStringAsync(ct);
            string? message = null;
            try
            {
                using var error = JsonDocument.Parse(body);
                message = error.RootElement.GetProperty("error").GetProperty("message").GetString();
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
            }

            throw new HttpRequestException($"YouTube antwortet {(int)response.StatusCode}: {message ?? response.ReasonPhrase}");
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var items = doc.RootElement.GetProperty("items");
        if (items.GetArrayLength() == 0) return null;
        var item = items[0];
        var snippet = item.GetProperty("snippet");
        string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() : null;

        string? thumbnail = null;
        if (snippet.TryGetProperty("thumbnails", out var thumbs))
        {
            foreach (var size in new[] { "maxres", "standard", "high", "medium", "default" })
            {
                if (thumbs.TryGetProperty(size, out var t) && Text(t, "url") is { } u)
                {
                    thumbnail = u;
                    break;
                }
            }
        }

        return new YouTubeVideo(id, Text(snippet, "title") ?? "", Text(snippet, "channelTitle") ?? "", Text(snippet, "publishedAt"),
            Text(snippet, "description") ?? "",
            item.TryGetProperty("contentDetails", out var details) && Text(details, "duration") is { } d ? ParseDuration(d) : null,
            thumbnail);
    }

    /// <summary>ISO 8601 duration as YouTube gives it ("PT1H26M30S") in seconds.</summary>
    public static double? ParseDuration(string iso)
    {
        try
        {
            return System.Xml.XmlConvert.ToTimeSpan(iso).TotalSeconds;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>Searches the configured external sources and rates their hits against the file.</summary>
public sealed class ExternalSourceService(
    SpotifyClient spotify,
    DeezerClient deezer,
    YouTubeClient youtube,
    Ai.AiService ai,
    ILogger<ExternalSourceService> logger)
{
    public bool SpotifyConfigured => spotify.IsConfigured;

    /// <summary>
    /// Loads the tracks linked in the file's tags; they count as exact matches. A YouTube video has no track data,
    /// so the AI reads artist, title and album from its title, channel and description (<paramref name="fileTags"/> as a hint).
    /// </summary>
    public async Task<(List<ExternalMatch> Matches, List<string> Messages)> FromLinksAsync(IEnumerable<TagField> fields, TagSet? fileTags,
        AppSettings settings, CancellationToken ct)
    {
        var matches = new List<ExternalMatch>();
        var messages = new List<string>();
        foreach (var link in ExternalLinks.Find(fields))
        {
            try
            {
                if (link.Source == "youtube")
                {
                    var (video, message) = await FromYouTubeAsync(link, fileTags, settings, ct);
                    messages.Add(message);
                    if (video != null) matches.Add(video);
                    continue;
                }

                var match = link.Source == "spotify" ? await spotify.GetTrackAsync(link.Id, ct) : await deezer.GetTrackAsync(link.Id, ct);
                if (match == null)
                {
                    messages.Add($"{link.Source}-Link in {link.Field} nicht gefunden ({link.Id})");
                    continue;
                }

                match.Score = 1;
                match.FromTagLink = true;
                matches.Add(match);
                messages.Add($"{link.Source}-Link in {link.Field}: \"{match.Title}\" (\"{match.Album}\")");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                messages.Add($"{link.Source}-Link in {link.Field} konnte nicht geladen werden: {ex.Message}");
            }
        }

        return (matches, messages);
    }

    private async Task<(ExternalMatch? Match, string Message)> FromYouTubeAsync(ExternalLinks.Link link, TagSet? fileTags, AppSettings settings,
        CancellationToken ct)
    {
        if (!youtube.IsConfigured)
            return (null, $"YouTube-Link in {link.Field} – ohne YouTube-API-Schlüssel nicht abrufbar");
        if (!ai.IsConfigured)
            return (null, $"YouTube-Link in {link.Field} – ohne KI lassen sich Künstler und Titel nicht aus dem Video lesen");

        var video = await youtube.GetVideoAsync(link.Id, ct);
        if (video == null) return (null, $"YouTube-Video in {link.Field} nicht gefunden ({link.Id}), gelöscht oder privat");

        var tags = await ai.ReadYouTubeAsync(video, fileTags, settings, ct);
        if (tags == null) return (null, $"YouTube-Video \"{video.Title}\" ({video.Channel}): KI konnte Künstler und Titel nicht bestimmen");

        var match = ToMatch(video, tags);
        return (match, $"YouTube-Video \"{video.Title}\" ({video.Channel}) → {string.Join(", ", match.Artists)} – {match.Title} " +
                       $"(KI {tags.Confidence:P0}: {tags.Notes})");
    }

    /// <summary>A YouTube video as a suggestion: no album named = a single named after the title; uploaded = released.</summary>
    public static ExternalMatch ToMatch(YouTubeVideo video, Ai.YouTubeTags tags) => new()
    {
        Source = "youtube",
        Id = video.Id,
        Url = $"https://www.youtube.com/watch?v={video.Id}",
        Title = tags.Title,
        Artists = tags.Artists,
        FeaturedArtists = tags.Featured,
        Album = tags.Album ?? tags.Title,
        AlbumArtists = tags.Artists,
        AlbumType = tags.Album == null ? "single" : null,
        ReleaseDate = tags.Date ?? (video.PublishedAt is { Length: >= 10 } p ? p[..10] : null),
        DurationSeconds = video.DurationSeconds,
        CoverUrl = video.ThumbnailUrl,
        // The video is certain, how it was read is not: a perfect Spotify/Deezer hit (1.0) ranks above it.
        Score = 0.99,
        FromTagLink = true,
    };

    public async Task<(List<ExternalMatch> Matches, List<string> Messages)> SearchAsync(TagSet tags, double? duration, AppSettings settings,
        CancellationToken ct)
    {
        var matches = new List<ExternalMatch>();
        var messages = new List<string>();
        if (string.IsNullOrWhiteSpace(tags.Title)) return (matches, messages);

        var (plainTitle, _) = TaggingRules.ExtractFeatures(tags.Title);
        var artist = tags.Artists.FirstOrDefault() ?? tags.Artist;

        async Task Run(string name, bool enabled, Func<Task<List<ExternalMatch>>> search, Func<ExternalMatch, Task<ExternalMatch>>? details = null)
        {
            if (!enabled) return;
            try
            {
                var found = (await search()).Select(m => { m.Score = Rate(m, plainTitle, artist, duration); return m; })
                    .Where(m => m.Score >= settings.ExternalMinScore)
                    .OrderByDescending(m => m.Score).Take(3).ToList();
                if (details != null)
                    found = [.. await Task.WhenAll(found.Take(2).Select(details)), .. found.Skip(2)];
                matches.AddRange(found);
                messages.Add($"{name}: {found.Count} passende Treffer");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "{Source} search failed", name);
                messages.Add($"{name}: Suche fehlgeschlagen ({ex.Message})");
            }
        }

        await Run("Spotify", settings.SpotifyEnabled && spotify.IsConfigured, () => spotify.SearchAsync(artist, plainTitle, ct));
        await Run("Deezer", settings.DeezerEnabled, () => deezer.SearchAsync(artist, plainTitle, ct), m => deezer.DetailsAsync(m, ct));
        // On equal scores Deezer comes first: it distinguishes featured artists and adds label and genres.
        return (matches.OrderByDescending(m => m.Score).ThenBy(m => m.Source == "deezer" ? 0 : 1).ToList(), messages);
    }

    /// <summary>Similarity of a hit to the file: title 50 %, artist 30 %, duration 20 %.</summary>
    public static double Rate(ExternalMatch m, string title, string? artist, double? duration)
    {
        var (matchTitle, _) = TaggingRules.ExtractFeatures(m.Title);
        var titleScore = Math.Max(TextUtil.Similarity(title, matchTitle), TextUtil.Similarity(title, m.Title));
        var artistScore = string.IsNullOrWhiteSpace(artist) ? 0.5 : m.Artists.Select(a => TextUtil.Similarity(artist, a)).DefaultIfEmpty(0).Max();
        var durationScore = duration == null || m.DurationSeconds == null
            ? 0.5
            : Math.Clamp(1 - (Math.Abs(duration.Value - m.DurationSeconds.Value) - 3) / 17, 0, 1);
        return Math.Round(0.5 * titleScore + 0.3 * artistScore + 0.2 * durationScore, 3);
    }

    /// <summary>Tags from an external hit (the own rules are applied afterwards).</summary>
    public static TagSet ToTagSet(ExternalMatch m)
    {
        static string JoinList(IReadOnlyList<string> items) =>
            items.Count <= 1 ? string.Concat(items) : $"{string.Join(", ", items.Take(items.Count - 1))} & {items[^1]}";

        var display = JoinList(m.Artists);
        if (m.FeaturedArtists.Count > 0) display += $" feat. {JoinList(m.FeaturedArtists)}";
        return new TagSet
        {
            Title = m.Title,
            Artist = display,
            Artists = [.. m.Artists, .. m.FeaturedArtists],
            Album = m.Album,
            AlbumArtist = m.AlbumArtists.Count > 0 ? JoinList(m.AlbumArtists) : null,
            AlbumArtists = [.. m.AlbumArtists],
            TrackNumber = m.TrackNumber,
            TrackCount = m.TrackCount,
            DiscNumber = m.DiscNumber,
            Date = m.ReleaseDate,
            Isrc = m.Isrc,
            Label = m.Label,
            Genres = [.. m.Genres],
        };
    }
}

/// <summary>Spotify/Deezer track and YouTube video links found in a file's tags (e.g. written by spotDL or yt-dlp).</summary>
public static partial class ExternalLinks
{
    public sealed record Link(string Source, string Id, string Field);

    public static List<Link> Find(IEnumerable<TagField> fields)
    {
        var links = new List<Link>();
        foreach (var field in fields)
        {
            foreach (var value in field.Values)
            {
                foreach (System.Text.RegularExpressions.Match m in SpotifyLink().Matches(value))
                    links.Add(new Link("spotify", m.Groups["id"].Value, field.Name));
                foreach (System.Text.RegularExpressions.Match m in DeezerLink().Matches(value))
                    links.Add(new Link("deezer", m.Groups["id"].Value, field.Name));
                foreach (System.Text.RegularExpressions.Match m in YouTubeLink().Matches(value))
                    links.Add(new Link("youtube", m.Groups["id"].Value, field.Name));
            }
        }

        return links.DistinctBy(l => (l.Source, l.Id)).ToList();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:open\.spotify\.com/(?:intl-[a-z]+/)?track/|spotify:track:)(?<id>[A-Za-z0-9]{22})")]
    private static partial System.Text.RegularExpressions.Regex SpotifyLink();

    [System.Text.RegularExpressions.GeneratedRegex(@"deezer\.com/(?:[a-z]{2}/)?track/(?<id>\d+)")]
    private static partial System.Text.RegularExpressions.Regex DeezerLink();

    // youtube.com/watch?v=…, music.youtube.com/watch?v=…, youtu.be/…, youtube.com/shorts/… – the ID is 11 characters.
    [System.Text.RegularExpressions.GeneratedRegex(@"(?:youtube\.com/(?:watch\?(?:[^\s#]*?&)?v=|shorts/|embed/|live/)|youtu\.be/)(?<id>[A-Za-z0-9_-]{11})(?![A-Za-z0-9_-])")]
    private static partial System.Text.RegularExpressions.Regex YouTubeLink();
}
