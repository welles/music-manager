using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MusicManager.Core.Configuration;
using MusicManager.Core.Data;

namespace MusicManager.Core.Services;

/// <summary>A playlist as Spotify lists it (metadata only).</summary>
public sealed record SpotifyPlaylistInfo(
    string Id, string Name, string? Description, string? OwnerId, string? OwnerName, bool Collaborative, string? ImageUrl, string? SnapshotId,
    int TrackCount);

/// <summary>A track of a playlist as Spotify lists it.</summary>
public sealed record SpotifyTrackInfo(string? Id, string Title, List<string> Artists, string? Album, int? DurationMs, string? Isrc, string? ImageUrl);

/// <summary>Spotify refused or failed; <see cref="Status"/> says how (e.g. 403 for a playlist that is not one's own).</summary>
public sealed class SpotifyException(HttpStatusCode? status, string message) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// The Spotify Web API on behalf of the owner (authorization code login). Since February 2026 apps in development mode get
/// playlist tracks only this way and only for playlists the user owns or collaborates on; playlists of others are mirrored
/// into own ones with Soundiiz. The refresh token is kept in <see cref="SpotifyAccount"/>.
/// </summary>
public sealed class SpotifyUserClient(HttpClient http, IOptions<MusicManagerOptions> options, IDbContextFactory<AppDbContext> dbFactory,
    ILogger<SpotifyUserClient> logger)
{
    /// <summary>
    /// Reading own and collaborative playlists and the liked songs, writing private playlists (the playlist of missing
    /// tracks).
    /// </summary>
    public const string Scopes = "playlist-read-private playlist-read-collaborative playlist-modify-private user-library-read";

    /// <summary>Stands for the liked songs ("Lieblingssongs"), which Spotify keeps as saved tracks, not as a playlist.</summary>
    public const string LikedSongsId = "liked-songs";

    public const string LikedSongsName = "Lieblingssongs";

    // Overridable in tests.
    internal string AccountsBase { get; set; } = "https://accounts.spotify.com";
    internal string ApiBase { get; set; } = "https://api.spotify.com/v1";

    private readonly ConcurrentDictionary<string, DateTimeOffset> _states = new();
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessExpires;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.Value.SpotifyClientId) && !string.IsNullOrWhiteSpace(options.Value.SpotifyClientSecret);

    public async Task<SpotifyAccount?> AccountAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SpotifyAccounts.AsNoTracking().FirstOrDefaultAsync(ct);
    }

    /// <summary>The Spotify login page; <paramref name="redirectUri"/> must be registered with the app in the Spotify dashboard.</summary>
    public string AuthorizeUrl(string redirectUri)
    {
        var state = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        foreach (var old in _states.Where(s => s.Value < DateTimeOffset.UtcNow).Select(s => s.Key)) _states.TryRemove(old, out _);
        _states[state] = DateTimeOffset.UtcNow.AddMinutes(10);
        return $"{AccountsBase}/authorize?response_type=code&client_id={Uri.EscapeDataString(options.Value.SpotifyClientId!)}" +
               $"&scope={Uri.EscapeDataString(Scopes)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&state={state}";
    }

    /// <summary>Completes the login: checks the state, trades the code for tokens and keeps the refresh token.</summary>
    public async Task<SpotifyAccount> CompleteLoginAsync(string code, string state, string redirectUri, CancellationToken ct = default)
    {
        if (!_states.TryRemove(state, out var expires) || expires < DateTimeOffset.UtcNow)
            throw new SpotifyException(null, "Die Anmeldung ist abgelaufen oder kam nicht von hier. Bitte noch einmal verbinden.");

        using var doc = await TokenRequestAsync(new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirectUri }, ct);
        var refresh = doc.RootElement.GetProperty("refresh_token").GetString()!;
        SetAccessToken(doc.RootElement);

        using var me = await GetAsync($"{ApiBase}/me", ct);
        var account = new SpotifyAccount
        {
            UserId = me.RootElement.GetProperty("id").GetString()!,
            DisplayName = Str(me.RootElement, "display_name"),
            RefreshToken = refresh,
            Scopes = Str(doc.RootElement, "scope"),
        };
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // A new login of the same account (e.g. for more permissions) keeps its settings.
        if (await db.SpotifyAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.UserId == account.UserId, ct) is { } previous)
        {
            account.MissingPlaylistEnabled = previous.MissingPlaylistEnabled;
            account.MissingPlaylistId = previous.MissingPlaylistId;
        }

        db.SpotifyAccounts.RemoveRange(db.SpotifyAccounts);
        db.SpotifyAccounts.Add(account);
        await db.SaveChangesAsync(ct);
        return account;
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.SpotifyAccounts.ExecuteDeleteAsync(ct);
        _accessToken = null;
    }

    /// <summary>The owner's playlists: own, collaborative and followed (the last ones without readable tracks).</summary>
    public async Task<List<SpotifyPlaylistInfo>> MyPlaylistsAsync(CancellationToken ct = default)
    {
        var result = new List<SpotifyPlaylistInfo>();
        string? url = $"{ApiBase}/me/playlists?limit=50";
        while (url != null)
        {
            using var page = await GetAsync(url, ct);
            result.AddRange(page.RootElement.GetProperty("items").EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object).Select(ParsePlaylist));
            url = Str(page.RootElement, "next");
        }

        return result;
    }

    public async Task<SpotifyPlaylistInfo> PlaylistAsync(string id, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"{ApiBase}/playlists/{Uri.EscapeDataString(id)}?fields=id,name,description,owner,collaborative,images,snapshot_id,items(total),tracks(total)", ct);
        return ParsePlaylist(doc.RootElement);
    }

    /// <summary>All tracks of a playlist, page by page; local files and podcast episodes are left out.</summary>
    public async Task<List<SpotifyTrackInfo>> PlaylistTracksAsync(string id, CancellationToken ct = default)
    {
        var result = new List<SpotifyTrackInfo>();
        string? url = $"{ApiBase}/playlists/{Uri.EscapeDataString(id)}/items?limit=100";
        while (url != null)
        {
            using var page = await GetAsync(url, ct);
            result.AddRange(ParseTracks(page.RootElement));
            url = Str(page.RootElement, "next");
        }

        return result;
    }

    /// <summary>The liked songs as a playlist entry (for the list to add from); <see cref="SpotifyPlaylistInfo.TrackCount"/> is their number.</summary>
    public async Task<SpotifyPlaylistInfo> LikedSongsAsync(CancellationToken ct = default)
    {
        var account = await AccountAsync(ct);
        using var doc = await GetAsync($"{ApiBase}/me/tracks?limit=1", ct);
        var total = doc.RootElement.TryGetProperty("total", out var t) && t.TryGetInt32(out var n) ? n : 0;
        return new SpotifyPlaylistInfo(LikedSongsId, LikedSongsName, "Deine Lieblingssongs auf Spotify.", account?.UserId, account?.DisplayName,
            false, null, null, total);
    }

    /// <summary>All liked songs, the most recently liked first (as Spotify shows them).</summary>
    public async Task<List<SpotifyTrackInfo>> LikedTracksAsync(CancellationToken ct = default)
    {
        var result = new List<SpotifyTrackInfo>();
        string? url = $"{ApiBase}/me/tracks?limit=50";
        while (url != null)
        {
            using var page = await GetAsync(url, ct);
            result.AddRange(ParseTracks(page.RootElement));
            url = Str(page.RootElement, "next");
        }

        return result;
    }

    /// <summary>Turns the playlist of missing tracks on or off (the playlist itself stays on Spotify).</summary>
    public async Task SetMissingPlaylistAsync(bool enabled, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.SpotifyAccounts.ExecuteUpdateAsync(s => s.SetProperty(a => a.MissingPlaylistEnabled, enabled), ct);
    }

    public async Task SetMissingPlaylistIdAsync(string? id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.SpotifyAccounts.ExecuteUpdateAsync(s => s.SetProperty(a => a.MissingPlaylistId, id), ct);
    }

    /// <summary>Creates a private playlist in the owner's account; returns its Spotify ID.</summary>
    public async Task<string> CreatePlaylistAsync(string name, string description, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Post, $"{ApiBase}/me/playlists", new { name, description, @public = false }, ct);
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>Whether a playlist still exists (it may have been deleted on Spotify).</summary>
    public async Task<bool> PlaylistExistsAsync(string id, CancellationToken ct = default)
    {
        try
        {
            await PlaylistAsync(id, ct);
            return true;
        }
        catch (SpotifyException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <summary>Replaces all tracks of a playlist; Spotify takes at most 100 per request, the rest is added in blocks.</summary>
    public async Task ReplaceTracksAsync(string id, IReadOnlyList<string> trackIds, CancellationToken ct = default)
    {
        var uris = trackIds.Select(t => $"spotify:track:{t}").ToList();
        var url = $"{ApiBase}/playlists/{Uri.EscapeDataString(id)}/items";
        (await SendAsync(HttpMethod.Put, url, new { uris = uris.Take(100).ToArray() }, ct)).Dispose();
        foreach (var block in uris.Skip(100).Chunk(100))
            (await SendAsync(HttpMethod.Post, url, new { uris = block }, ct)).Dispose();
    }

    // ---- Parsing (old and new field names: since February 2026 "items" replaces "tracks", "item" replaces "track") ----

    internal static SpotifyPlaylistInfo ParsePlaylist(JsonElement p)
    {
        var owner = p.TryGetProperty("owner", out var o) && o.ValueKind == JsonValueKind.Object ? o : default;
        var total = 0;
        foreach (var name in new[] { "items", "tracks" })
        {
            if (p.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.Object && t.TryGetProperty("total", out var n) && n.TryGetInt32(out var v))
            {
                total = v;
                break;
            }
        }

        return new SpotifyPlaylistInfo(
            p.GetProperty("id").GetString()!,
            Str(p, "name") ?? "",
            Str(p, "description") is { Length: > 0 } d ? WebUtility.HtmlDecode(d) : null,
            owner.ValueKind == JsonValueKind.Object ? Str(owner, "id") : null,
            owner.ValueKind == JsonValueKind.Object ? Str(owner, "display_name") ?? Str(owner, "id") : null,
            p.TryGetProperty("collaborative", out var c) && c.ValueKind == JsonValueKind.True,
            FirstImage(p),
            Str(p, "snapshot_id"),
            total);
    }

    internal static IEnumerable<SpotifyTrackInfo> ParseTracks(JsonElement page)
    {
        if (!page.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) yield break;
        foreach (var entry in items.EnumerateArray())
        {
            var track = entry.TryGetProperty("item", out var i) && i.ValueKind == JsonValueKind.Object ? i
                : entry.TryGetProperty("track", out var t) && t.ValueKind == JsonValueKind.Object ? t
                : default;
            if (track.ValueKind != JsonValueKind.Object) continue; // removed from Spotify
            if (Str(track, "type") is { } type && type != "track") continue; // podcast episode
            if (track.TryGetProperty("is_local", out var local) && local.ValueKind == JsonValueKind.True) continue; // a file on someone's computer

            var artists = track.TryGetProperty("artists", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => Str(x, "name")).OfType<string>().ToList()
                : [];
            var album = track.TryGetProperty("album", out var al) && al.ValueKind == JsonValueKind.Object ? al : default;
            var isrc = track.TryGetProperty("external_ids", out var ids) && ids.ValueKind == JsonValueKind.Object ? Str(ids, "isrc") : null;
            yield return new SpotifyTrackInfo(
                Str(track, "id"),
                Str(track, "name") ?? "",
                artists,
                album.ValueKind == JsonValueKind.Object ? Str(album, "name") : null,
                track.TryGetProperty("duration_ms", out var ms) && ms.TryGetInt32(out var d) ? d : null,
                isrc?.Trim().ToUpperInvariant(),
                album.ValueKind == JsonValueKind.Object ? FirstImage(album) : null);
        }
    }

    private static string? FirstImage(JsonElement e) =>
        e.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array
            ? images.EnumerateArray().Select(i => Str(i, "url")).FirstOrDefault(u => u != null)
            : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---- HTTP ----

    private Task<JsonDocument> GetAsync(string url, CancellationToken ct) => SendAsync(HttpMethod.Get, url, null, ct);

    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            if (body != null) request.Content = System.Net.Http.Json.JsonContent.Create(body);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(ct));
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                _accessToken = null; // expired early or revoked: one more try with a fresh one
                continue;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                await Task.Delay(wait > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait, ct);
                continue;
            }

            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new SpotifyException(response.StatusCode, $"Spotify antwortete {(int)response.StatusCode}: {ErrorMessage(text)}");
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        }
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e)
                ? e.ValueKind == JsonValueKind.Object ? Str(e, "message") ?? body : e.GetString() ?? body
                : body;
        }
        catch (JsonException)
        {
            return body.Length > 200 ? body[..200] : body;
        }
    }

    private async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_accessToken != null && DateTimeOffset.UtcNow < _accessExpires) return _accessToken;
            var account = await AccountAsync(ct) ?? throw new SpotifyException(null, "Nicht mit Spotify verbunden.");
            using var doc = await TokenRequestAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = account.RefreshToken }, ct);
            SetAccessToken(doc.RootElement);
            // Spotify may hand out a new refresh token; the old one then stops working.
            if (Str(doc.RootElement, "refresh_token") is { } renewed && renewed != account.RefreshToken)
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                await db.SpotifyAccounts.Where(a => a.Id == account.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.RefreshToken, renewed), ct);
            }

            return _accessToken!;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private void SetAccessToken(JsonElement token)
    {
        _accessToken = token.GetProperty("access_token").GetString();
        _accessExpires = DateTimeOffset.UtcNow.AddSeconds(token.GetProperty("expires_in").GetInt32() - 60);
    }

    private async Task<JsonDocument> TokenRequestAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{AccountsBase}/api/token") { Content = new FormUrlEncodedContent(form) };
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Value.SpotifyClientId}:{options.Value.SpotifyClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Spotify token request failed: {Status} {Body}", (int)response.StatusCode, ErrorMessage(body));
            throw new SpotifyException(response.StatusCode, response.StatusCode == HttpStatusCode.BadRequest
                ? "Spotify hat die Anmeldung nicht angenommen (abgelaufen oder widerrufen). Bitte neu verbinden."
                : $"Spotify-Anmeldung fehlgeschlagen ({(int)response.StatusCode}).");
        }

        return JsonDocument.Parse(body);
    }
}
