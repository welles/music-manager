using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using MusicManager.Core.Data;
using MusicManager.Core.Pipeline;
using MusicManager.Core.Services;

namespace MusicManager.Web;

/// <summary>
/// Audio streaming for the A/B comparison: range requests (seeking) on the original files,
/// non-browser formats are transcoded once and cached. Only files known to the database are served.
/// </summary>
public static class AudioEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new()
    {
        Mappings = { [".m4a"] = "audio/mp4", [".opus"] = "audio/ogg", [".flac"] = "audio/flac" },
    };

    public static void MapAudioEndpoints(this IEndpointRouteBuilder app)
    {
        // New file: still in the inbox, or the original in the trash.
        app.MapGet("/api/audio/file/{id:int}", async (int id, IDbContextFactory<AppDbContext> dbFactory, Storage storage, AudioTools audio,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var file = await db.Files.FindAsync([id], ct);
            var path = file == null ? null : new[] { storage.InboxPath(file.RelativePath), file.TrashPath }.FirstOrDefault(p => p != null && File.Exists(p));
            return path == null ? Results.NotFound() : await Serve(path, audio, ct);
        });

        // Existing file in the library.
        app.MapGet("/api/audio/library/{id:int}", async (int id, IDbContextFactory<AppDbContext> dbFactory, Storage storage, AudioTools audio,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entry = await db.Library.FindAsync([id], ct);
            var path = entry == null ? null : storage.LibraryPath(entry.RelativePath);
            return path == null || !File.Exists(path) ? Results.NotFound() : await Serve(path, audio, ct);
        });

        // Cover of a library file.
        app.MapGet("/api/library/{id:int}/cover", async (int id, IDbContextFactory<AppDbContext> dbFactory, Storage storage, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entry = await db.Library.FindAsync([id], ct);
            var path = entry == null ? null : storage.LibraryPath(entry.RelativePath);
            var data = path != null && File.Exists(path) ? TagIo.ReadEmbeddedCover(path) : null;
            return data == null
                ? Results.Content(Placeholder, "image/svg+xml")
                : Results.File(data, data.Length > 3 && data[0] == 0x89 ? "image/png" : "image/jpeg");
        });

        // Artist image (filled by the artist image service; placeholder until then).
        app.MapGet("/api/artists/image", () => Results.Content(Placeholder, "image/svg+xml"));

        // Loudness of both versions (LUFS) for the volume match.
        app.MapGet("/api/audio/loudness", async (int file, int entry, IDbContextFactory<AppDbContext> dbFactory, Storage storage, AudioTools audio,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var f = await db.Files.FindAsync([file], ct);
            var e = await db.Library.FindAsync([entry], ct);
            var newPath = f == null ? null : new[] { storage.InboxPath(f.RelativePath), f.TrashPath }.FirstOrDefault(p => p != null && File.Exists(p));
            var oldPath = e == null ? null : storage.LibraryPath(e.RelativePath);
            return Results.Ok(new
            {
                @new = newPath == null ? null : await audio.LoudnessAsync(newPath, ct),
                existing = oldPath == null || !File.Exists(oldPath) ? null : await audio.LoudnessAsync(oldPath, ct),
            });
        });
    }

    /// <summary>Neutral note icon for missing covers and artist images.</summary>
    public const string Placeholder =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><rect width=\"24\" height=\"24\" fill=\"#9e9e9e33\"/><path fill=\"#9e9e9e\" d=\"M12 3v10.55A4 4 0 1 0 14 17V7h4V3z\"/></svg>";

    private static async Task<IResult> Serve(string path, AudioTools audio, CancellationToken ct)
    {
        var playable = await audio.BrowserPlayableAsync(path, ct);
        if (!ContentTypes.TryGetContentType(playable, out var contentType)) contentType = "application/octet-stream";
        return Results.File(playable, contentType, enableRangeProcessing: true);
    }
}
