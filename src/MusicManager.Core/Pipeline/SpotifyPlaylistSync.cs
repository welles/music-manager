using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MusicManager.Core.Configuration;
using MusicManager.Core.Data;
using MusicManager.Core.Services;

namespace MusicManager.Core.Pipeline;

/// <summary>State of the Spotify sync for the playlists page.</summary>
public sealed record SpotifySyncState(bool Running, DateTimeOffset? LastRun, string? Current);

/// <summary>Last update of the playlist of missing tracks: how many it holds, or why it failed.</summary>
public sealed record MissingPlaylistState(int? Count, string? Error, DateTimeOffset At);

/// <summary>
/// Keeps the chosen Spotify playlists in step: every <see cref="Interval"/> and on request. A tracklist is only read again
/// when Spotify's snapshot changed; the playlist picture is fetched when it changed (shown with a Spotify badge, see
/// <see cref="CoverPath"/>). Which tracks are in the library is worked out on reading, not here.
/// </summary>
public sealed class SpotifyPlaylistSync(
    IDbContextFactory<AppDbContext> dbFactory,
    SpotifyUserClient spotify,
    CoverArtService covers,
    Catalog.MediaCatalog catalog,
    PipelineNotifier notifier,
    IOptions<MusicManagerOptions> options,
    ILogger<SpotifyPlaylistSync> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly SemaphoreSlim _running = new(1, 1);

    public SpotifySyncState State { get; private set; } = new(false, null, null);

    public MissingPlaylistState? MissingState { get; private set; }

    public const string MissingPlaylistName = "Fehlt im Music Manager";

    /// <summary>Raised after playlists or their tracks changed (pages refresh, clients get the new content).</summary>
    public event Action? Changed;

    public string CoverDir => Path.Combine(Path.GetFullPath(options.Value.DataPath), "cache", "spotify-covers");

    /// <summary>The playlist's picture as Spotify has it (without badge); may not exist.</summary>
    public string CoverPath(int playlistId) => Path.Combine(CoverDir, $"{playlistId}.img");

    /// <summary>Starts a sync of all playlists soon (the "sync now" button, a new playlist).</summary>
    public void Request()
    {
        if (_signal.CurrentCount == 0) _signal.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Once right after the start (e.g. after an update), then every interval or when asked.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Spotify playlist sync failed");
            }

            await _signal.WaitAsync(Interval, ct);
        }
    }

    /// <summary>Syncs every chosen playlist (or only <paramref name="onlyId"/>); one failing does not stop the others.</summary>
    public async Task RunAsync(CancellationToken ct, int? onlyId = null)
    {
        if (!spotify.IsConfigured || await spotify.AccountAsync(ct) == null) return;
        await _running.WaitAsync(ct);
        try
        {
            List<int> ids;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
                ids = await db.SpotifyPlaylists.Where(p => onlyId == null || p.Id == onlyId).Select(p => p.Id).ToListAsync(ct);

            foreach (var id in ids)
            {
                await SyncOneAsync(id, ct);
                Changed?.Invoke();
                notifier.Notify();
            }

            await UpdateMissingPlaylistAsync(ct);
            State = new SpotifySyncState(false, DateTimeOffset.UtcNow, null);
        }
        finally
        {
            State = State with { Running = false, Current = null };
            _running.Release();
            notifier.Notify();
        }
    }

    private async Task SyncOneAsync(int id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var playlist = await db.SpotifyPlaylists.Include(p => p.Tracks).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (playlist == null) return;
        State = new SpotifySyncState(true, State.LastRun, playlist.Name);
        notifier.Notify();
        try
        {
            if (playlist.SpotifyId == SpotifyUserClient.LikedSongsId)
            {
                await SyncLikedSongsAsync(db, playlist, ct);
            }
            else
            {
                await SyncPlaylistAsync(db, playlist, ct);
            }

            playlist.SyncedAt = DateTimeOffset.UtcNow;
            playlist.SyncError = null;
        }
        catch (SpotifyException ex) when (playlist.SpotifyId == SpotifyUserClient.LikedSongsId && ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            playlist.SyncError = "Spotify hat das Lesen der Lieblingssongs nicht erlaubt. Bitte einmal neu mit Spotify verbinden.";
            logger.LogWarning("Spotify liked songs: {Error}", ex.Message);
        }
        catch (SpotifyException ex)
        {
            playlist.SyncError = ex.Status switch
            {
                HttpStatusCode.Forbidden =>
                    "Spotify gibt die Titel dieser Playlist nicht heraus, weil sie nicht dir gehört. Spiegele sie mit Soundiiz in eine eigene Playlist und füge die Kopie hinzu.",
                HttpStatusCode.NotFound => "Die Playlist gibt es auf Spotify nicht mehr (oder sie ist privat und nicht deine).",
                _ => ex.Message,
            };
            logger.LogWarning("Spotify playlist {Name}: {Error}", playlist.Name, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Network trouble: tried again at the next run, the other playlists go on.
            playlist.SyncError = $"Spotify nicht erreichbar: {ex.Message}";
            logger.LogWarning(ex, "Spotify playlist {Name} could not be synced", playlist.Name);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task SyncPlaylistAsync(AppDbContext db, SpotifyPlaylist playlist, CancellationToken ct)
    {
        var info = await spotify.PlaylistAsync(playlist.SpotifyId, ct);
        playlist.Name = info.Name;
        playlist.Description = info.Description;
        playlist.OwnerId = info.OwnerId;
        playlist.OwnerName = info.OwnerName;
        playlist.Collaborative = info.Collaborative;

        if (info.SnapshotId == null || info.SnapshotId != playlist.SnapshotId || playlist.Tracks.Count == 0)
        {
            SetTracks(db, playlist, await spotify.PlaylistTracksAsync(playlist.SpotifyId, ct));
            playlist.SnapshotId = info.SnapshotId;
        }

        if (info.ImageUrl != playlist.ImageUrl || !File.Exists(CoverPath(playlist.Id)))
        {
            await SaveCoverAsync(playlist.Id, info.ImageUrl, ct);
            playlist.ImageUrl = info.ImageUrl;
        }
    }

    /// <summary>
    /// The liked songs have no snapshot to compare: they are read on every run and stored only when the list changed.
    /// They have no picture either; the web app shows its own cover for them.
    /// </summary>
    private async Task SyncLikedSongsAsync(AppDbContext db, SpotifyPlaylist playlist, CancellationToken ct)
    {
        var account = await spotify.AccountAsync(ct);
        playlist.Name = SpotifyUserClient.LikedSongsName;
        playlist.OwnerId = account?.UserId;
        playlist.OwnerName = account?.DisplayName;
        var tracks = await spotify.LikedTracksAsync(ct);
        var stored = playlist.Tracks.OrderBy(t => t.Position).Select(t => t.SpotifyTrackId);
        if (!stored.SequenceEqual(tracks.Select(t => t.Id))) SetTracks(db, playlist, tracks);
    }

    private void SetTracks(AppDbContext db, SpotifyPlaylist playlist, List<SpotifyTrackInfo> tracks)
    {
        db.SpotifyPlaylistTracks.RemoveRange(playlist.Tracks);
        playlist.Tracks = tracks.Select((t, i) => new SpotifyPlaylistTrack
        {
            Position = i, SpotifyTrackId = t.Id, Title = t.Title, Artists = t.Artists, Album = t.Album, DurationMs = t.DurationMs, Isrc = t.Isrc,
            ImageUrl = t.ImageUrl,
        }).ToList();
        logger.LogInformation("Spotify playlist {Name}: {Count} tracks read", playlist.Name, tracks.Count);
    }

    /// <summary>
    /// Keeps the private Spotify playlist "<see cref="MissingPlaylistName"/>" in step when it is turned on: all tracks of
    /// the kept playlists that are not in the library, each once, by playlist name and position. Tracks that arrived in the
    /// library leave it, new missing ones join; it is created (again) when there is none, and written only when its
    /// content changed. A failure is shown on the playlists page and does not stop the sync.
    /// </summary>
    public async Task UpdateMissingPlaylistAsync(CancellationToken ct)
    {
        var account = await spotify.AccountAsync(ct);
        if (account is not { MissingPlaylistEnabled: true }) return;
        if (!account.CanWritePlaylists)
        {
            MissingState = new MissingPlaylistState(null, "Spotify hat das Anlegen von Playlists nicht erlaubt. Bitte einmal neu mit Spotify verbinden.",
                DateTimeOffset.UtcNow);
            return;
        }

        try
        {
            List<SpotifyPlaylist> playlists;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
                playlists = await db.SpotifyPlaylists.AsNoTracking().Include(p => p.Tracks)
                    .Where(p => p.SpotifyId != account.MissingPlaylistId).OrderBy(p => p.Name).ToListAsync(ct);

            var matcher = Catalog.SpotifyMatcher.For(await catalog.GetAsync(ct));
            var missing = playlists.SelectMany(p => p.Tracks.OrderBy(t => t.Position))
                .Where(t => t.SpotifyTrackId != null && matcher.Match(t) == null)
                .Select(t => t.SpotifyTrackId!)
                .Distinct()
                .ToList();

            var id = account.MissingPlaylistId;
            if (id == null || !await spotify.PlaylistExistsAsync(id, ct))
            {
                id = await spotify.CreatePlaylistAsync(MissingPlaylistName,
                    "Titel aus deinen Spotify-Playlists, die noch nicht in der Music-Manager-Bibliothek sind. Wird vom Music Manager gepflegt.", ct);
                await spotify.SetMissingPlaylistIdAsync(id, ct);
                logger.LogInformation("Spotify playlist of missing tracks created: {Id}", id);
            }

            var current = (await spotify.PlaylistTracksAsync(id, ct)).Select(t => t.Id).ToList();
            if (!current.SequenceEqual(missing))
            {
                await spotify.ReplaceTracksAsync(id, missing, ct);
                logger.LogInformation("Spotify playlist of missing tracks: {Count} tracks", missing.Count);
            }

            MissingState = new MissingPlaylistState(missing.Count, null, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is SpotifyException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            MissingState = new MissingPlaylistState(null, ex.Message, DateTimeOffset.UtcNow);
            logger.LogWarning(ex, "Updating the Spotify playlist of missing tracks failed");
        }
    }

    private async Task SaveCoverAsync(int playlistId, string? url, CancellationToken ct)
    {
        var path = CoverPath(playlistId);
        if (url == null)
        {
            File.Delete(path);
            return;
        }

        if (await covers.DownloadAsync(url, "Spotify", ct) is not { } image) return;
        Directory.CreateDirectory(CoverDir);
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(tmp, image.Data, ct);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Adds a playlist of the owner's Spotify account and syncs it right away. Followed playlists of others are refused:
    /// Spotify does not give out their tracks; mirror them into an own playlist with Soundiiz first.
    /// </summary>
    public async Task<SpotifyPlaylist> AddAsync(SpotifyPlaylistInfo info, CancellationToken ct = default)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            if (await db.SpotifyPlaylists.FirstOrDefaultAsync(p => p.SpotifyId == info.Id, ct) is { } existing) return existing;
            db.SpotifyPlaylists.Add(new SpotifyPlaylist
            {
                SpotifyId = info.Id, Name = info.Name, Description = info.Description, OwnerId = info.OwnerId, OwnerName = info.OwnerName,
                Collaborative = info.Collaborative,
            });
            await db.SaveChangesAsync(ct);
        }

        int id;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            id = await db.SpotifyPlaylists.Where(p => p.SpotifyId == info.Id).Select(p => p.Id).FirstAsync(ct);
        await RunAsync(ct, id);
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            return await db.SpotifyPlaylists.AsNoTracking().FirstAsync(p => p.Id == id, ct);
    }

    /// <summary>Removes a playlist with its tracks and covers; waits for a sync that is running, so none writes to it afterwards.</summary>
    public async Task RemoveAsync(int id, CancellationToken ct = default)
    {
        await _running.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.SpotifyPlaylists.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
            // The picture and the one with the badge made from it (SpotifyCovers in the web app).
            File.Delete(CoverPath(id));
            File.Delete($"{CoverPath(id)}.badge.jpg");
        }
        finally
        {
            _running.Release();
        }

        Changed?.Invoke();
        notifier.Notify();
    }

    /// <summary>Whether Spotify gives out the tracks of a playlist of the owner's list (own or collaborative).</summary>
    public static bool Readable(SpotifyPlaylistInfo info, string ownUserId) => info.OwnerId == ownUserId || info.Collaborative;
}
