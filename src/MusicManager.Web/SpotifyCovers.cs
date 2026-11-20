using Microsoft.EntityFrameworkCore;
using MusicManager.Core.Data;
using MusicManager.Core.Pipeline;
using MusicManager.Core.Services;

namespace MusicManager.Web;

/// <summary>Covers of the Spotify playlists, for Symfonium (getCoverArt) and the playlists page alike.</summary>
public static class SpotifyCovers
{
    /// <summary>
    /// The playlist's picture from Spotify with a Spotify badge, made once per picture and kept next to it; the generic
    /// Spotify cover when there is no picture, the picture without badge when ffmpeg cannot be used.
    /// </summary>
    public static async Task<string> PathAsync(IServiceProvider services, int playlistId, CancellationToken ct)
    {
        var covers = Path.Combine(services.GetRequiredService<IWebHostEnvironment>().WebRootPath, "playlist-covers");
        // The liked songs have no picture on Spotify: their own cover (with the badge already in it).
        await using (var db = await services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(ct))
        {
            if (await db.SpotifyPlaylists.AnyAsync(p => p.Id == playlistId && p.SpotifyId == SpotifyUserClient.LikedSongsId, ct))
                return Path.Combine(covers, "spotify-liked.png");
        }

        var source = services.GetRequiredService<SpotifyPlaylistSync>().CoverPath(playlistId);
        if (!File.Exists(source)) return Path.Combine(covers, "spotify.png");

        var badged = $"{source}.badge.jpg";
        if (File.Exists(badged) && File.GetLastWriteTimeUtc(badged) >= File.GetLastWriteTimeUtc(source)) return badged;
        var image = await services.GetRequiredService<AudioTools>().AddBadgeAsync(await File.ReadAllBytesAsync(source, ct),
            Path.Combine(covers, "spotify-badge.png"), 600, ct);
        if (image == null) return source;
        var tmp = $"{badged}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(tmp, image, ct);
        File.Move(tmp, badged, overwrite: true);
        return badged;
    }

    public static void MapSpotifyCovers(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/playlists/spotify/{id:int}/cover", async (int id, int? size, ThumbnailCache thumbnails, HttpContext http, CancellationToken ct) =>
        {
            var file = await PathAsync(http.RequestServices, id, ct);
            if (ThumbnailCache.Normalize(size) is { } edge) file = await thumbnails.GetAsync(file, edge, ct) ?? file;
            http.Response.Headers.CacheControl = "private, max-age=300";
            return Results.File(file, file.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "image/jpeg");
        });
}
