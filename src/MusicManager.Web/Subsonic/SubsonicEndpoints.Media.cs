using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MusicManager.Core.Catalog;
using MusicManager.Core.Configuration;
using MusicManager.Core.Pipeline;
using MusicManager.Core.Services;

namespace MusicManager.Web.Subsonic;

// Media: stream, download and cover art (album folder cover, embedded cover, artist image).
public static partial class SubsonicEndpoints
{
    private static void MapMedia()
    {
        // No transcoding: maxBitRate and format are ignored, the original file is served (with ranges).
        Add("stream", r => ServeSongAsync(r, download: false));
        Add("download", r => ServeSongAsync(r, download: true));
        Add("getCoverArt", GetCoverArtAsync);
    }

    private static async Task<IResult> ServeSongAsync(SubsonicRequest r, bool download)
    {
        var catalog = await Catalog(r);
        var song = catalog.Song(r.Require("id")) ?? throw SubsonicException.NotFound("Song");
        var path = r.Service<Storage>().LibraryPath(song.RelativePath);
        if (!File.Exists(path)) throw SubsonicException.NotFound("File");
        return Results.File(path, SubsonicMapper.ContentType(path), download ? Path.GetFileName(path) : null, enableRangeProcessing: true);
    }

    private static async Task<IResult> GetCoverArtAsync(SubsonicRequest r)
    {
        var id = r.Require("id");
        var requested = r.GetInt("size");
        int? size = requested > 0 ? Math.Min(requested.Value, 2000) : null;
        var catalog = await Catalog(r);
        var storage = r.Service<Storage>();

        // Source image and a stamp that changes whenever the image may have changed (cache key).
        Func<Task<byte[]?>>? load = null;
        string? stamp = null;
        if (id.StartsWith(CatalogIds.ArtistPrefix, StringComparison.Ordinal) && catalog.Artist(id) is { } artist)
        {
            var file = await r.Service<ArtistImageService>().GetImagePathAsync(artist.Name, artist.MbArtistId, r.Aborted);
            if (file != null)
            {
                stamp = $"{file}|{File.GetLastWriteTimeUtc(file).Ticks}";
                load = async () => await File.ReadAllBytesAsync(file, r.Aborted);
            }
        }
        else
        {
            var song = id.StartsWith(CatalogIds.SongPrefix, StringComparison.Ordinal) ? catalog.Song(id) : null;
            var album = song?.Album ?? catalog.Album(id);
            song ??= album?.CoverSong;
            if (song is { HasCover: false }) song = song.Album.CoverSong;

            // A cover file in the album's own folder (usually larger) goes before the embedded cover.
            if (album?.Folder is { } folder && AlbumCoverService.Find(storage.LibraryPath(folder)) is { } coverFile)
            {
                var info = new FileInfo(coverFile);
                stamp = $"{coverFile}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                load = async () => File.Exists(coverFile) ? await File.ReadAllBytesAsync(coverFile, r.Aborted) : null;
            }
            else if (song != null)
            {
                var path = storage.LibraryPath(song.RelativePath);
                stamp = $"{song.RelativePath}|{song.Size}|{song.FileModifiedUtc.Ticks}";
                load = () => Task.FromResult(File.Exists(path) ? TagIo.ReadEmbeddedCover(path) : null);
            }
        }

        if (load == null) throw SubsonicException.NotFound("Cover art");
        r.Http.Response.Headers.CacheControl = "private, max-age=3600";

        if (size == null)
        {
            var original = await load() ?? throw SubsonicException.NotFound("Cover art");
            return Results.File(original, ImageType(original));
        }

        var cacheDir = Path.Combine(Path.GetFullPath(r.Service<IOptions<MusicManagerOptions>>().Value.DataPath), "cache", "covers");
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{id}|{stamp}|{size}")))[..32];
        var cached = Path.Combine(cacheDir, $"{key}.jpg");
        if (File.Exists(cached)) return Results.File(cached, "image/jpeg");

        var image = await load() ?? throw SubsonicException.NotFound("Cover art");
        var scaled = await r.Service<AudioTools>().ResizeImageAsync(image, size.Value, r.Aborted);
        if (scaled == null) return Results.File(image, ImageType(image));

        Directory.CreateDirectory(cacheDir);
        var tmp = $"{cached}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(tmp, scaled, r.Aborted);
        File.Move(tmp, cached, overwrite: true);
        return Results.File(scaled, "image/jpeg");
    }

    private static string ImageType(byte[] data) => data.Length > 3 && data[0] == 0x89 && data[1] == 0x50 ? "image/png" : "image/jpeg";
}
