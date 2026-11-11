using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MusicManager.Core.Configuration;

namespace MusicManager.Core.Services;

/// <summary>
/// Top tags of tracks and albums from Last.fm (genre fallback). Tags are folksonomy – the caller keeps only
/// real genres. Without an API key every call returns nothing.
/// </summary>
public sealed class LastFmClient(HttpClient http, IOptions<MusicManagerOptions> options, ILogger<LastFmClient> logger)
{
    private const string BaseUrl = "https://ws.audioscrobbler.com/2.0/";

    private static readonly ConcurrentDictionary<string, List<KeyValuePair<string, int>>> Cache = new();
    private static readonly SemaphoreSlim Throttle = new(1, 1);
    private static DateTime _last = DateTime.MinValue;

    public bool Enabled => !string.IsNullOrWhiteSpace(options.Value.LastFmApiKey);

    public Task<List<KeyValuePair<string, int>>> GetTrackTagsAsync(string artist, string track, CancellationToken ct) =>
        GetTagsAsync(new() { ["method"] = "track.gettoptags", ["artist"] = artist, ["track"] = track }, ct);

    public Task<List<KeyValuePair<string, int>>> GetAlbumTagsAsync(string artist, string album, CancellationToken ct) =>
        GetTagsAsync(new() { ["method"] = "album.gettoptags", ["artist"] = artist, ["album"] = album }, ct);

    /// <summary>Artist tags by MusicBrainz ID only – names are ambiguous ("August Moon" finds a death metal band).</summary>
    public Task<List<KeyValuePair<string, int>>> GetArtistTagsAsync(string mbid, CancellationToken ct) =>
        GetTagsAsync(new() { ["method"] = "artist.gettoptags", ["mbid"] = mbid }, ct);

    private async Task<List<KeyValuePair<string, int>>> GetTagsAsync(Dictionary<string, string> query, CancellationToken ct)
    {
        if (!Enabled) return [];
        var cacheKey = string.Join("|", query.Values).ToLowerInvariant();
        if (Cache.TryGetValue(cacheKey, out var cached)) return cached;

        query["autocorrect"] = "1";
        query["format"] = "json";
        query["api_key"] = options.Value.LastFmApiKey!;
        var url = BaseUrl + "?" + string.Join("&", query.Select(q => $"{q.Key}={Uri.EscapeDataString(q.Value)}"));

        try
        {
            // Last.fm allows about five requests per second.
            await Throttle.WaitAsync(ct);
            try
            {
                var wait = _last.AddMilliseconds(250) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                _last = DateTime.UtcNow;
            }
            finally
            {
                Throttle.Release();
            }

            using var response = await http.GetAsync(url, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var result = Parse(doc.RootElement);
            Cache[cacheKey] = result;
            return result;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Last.fm {Method} failed: {Message}", query["method"], ex.Message);
            return [];
        }
    }

    /// <summary>{"toptags":{"tag":[{"name":"drum and bass","count":100}, …]}}; errors ({"error":6}) give nothing.</summary>
    public static List<KeyValuePair<string, int>> Parse(JsonElement root)
    {
        if (!root.TryGetProperty("toptags", out var top) || !top.TryGetProperty("tag", out var tags)) return [];
        var list = tags.ValueKind == JsonValueKind.Array ? tags.EnumerateArray().ToList() : [tags];
        return list
            .Select(t => new KeyValuePair<string, int>(
                t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                t.TryGetProperty("count", out var c) ? c.ValueKind == JsonValueKind.Number ? c.GetInt32() : int.TryParse(c.GetString(), out var i) ? i : 0 : 0))
            .Where(t => t.Key.Length > 0 && t.Value > 0)
            .ToList();
    }
}
