using Microsoft.Extensions.Logging;
using MusicManager.Core.Catalog;
using MusicManager.Core.Model;
using MusicManager.Core.Rules;
using MusicManager.Core.Services;

namespace MusicManager.Core.Pipeline;

/// <summary>State of the run that loads missing album folder covers.</summary>
public sealed record AlbumCoverState(bool Running, int Progress, int Total, int Written, int NotFound);

/// <summary>
/// Album covers as files in the album folder (cover.jpg), in a larger size than the cover embedded into the
/// audio files. The Subsonic API prefers them, and other players read them too. A file is only written into a
/// folder that holds exactly one album, and an existing cover file is never overwritten, so own images stay.
/// </summary>
public sealed class AlbumCoverService(
    MediaCatalog catalog,
    CoverArtService coverArt,
    DeezerClient deezer,
    SpotifyClient spotify,
    AudioTools audio,
    Storage storage,
    SettingsService settingsService,
    PipelineNotifier notifier,
    ILogger<AlbumCoverService> logger)
{
    /// <summary>Longest side of a written cover; the largest thumbnail the Cover Art Archive offers.</summary>
    public const int Size = 1200;

    public const string FileName = "cover.jpg";

    // In the order they are looked for.
    private static readonly string[] Names = ["cover", "folder", "front", "albumart"];
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png"];

    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _progress, _total, _written, _notFound;

    public AlbumCoverState State => new(_lock.CurrentCount == 0, _progress, _total, _written, _notFound);

    /// <summary>True for file names that count as the cover of their folder (cover, folder, front, albumart; JPEG or PNG).</summary>
    public static bool IsCoverFile(string fileName) =>
        Names.Contains(Path.GetFileNameWithoutExtension(fileName), StringComparer.OrdinalIgnoreCase)
        && Extensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    /// <summary>The cover file of a folder, or null.</summary>
    public static string? Find(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        return Directory.EnumerateFiles(folder)
            .Where(f => IsCoverFile(Path.GetFileName(f)))
            .OrderBy(f => Array.FindIndex(Names, n => n.Equals(Path.GetFileNameWithoutExtension(f), StringComparison.OrdinalIgnoreCase)))
            .ThenBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>
    /// Writes the folder cover for a song that was just filed, unless its folder has one. <paramref name="image"/>
    /// is the cover in its original size; without it the cover comes from the Cover Art Archive (if a release is
    /// given). Never throws: the song is filed either way.
    /// </summary>
    public async Task EnsureForSongAsync(string relativePath, byte[]? image, string? mbReleaseId, string? mbReleaseGroupId, CancellationToken ct)
    {
        try
        {
            if (!(await settingsService.GetAsync(ct)).WriteCoverJpg) return;
            var folder = Path.GetDirectoryName(relativePath);
            if (string.IsNullOrEmpty(folder) || Find(storage.LibraryPath(folder)) != null) return;
            if (image == null && mbReleaseId == null) return;

            // Only when the folder is this album's alone, as the catalog sees it now.
            var song = (await catalog.GetAsync(ct)).Songs.FirstOrDefault(s => s.RelativePath == relativePath);
            if (song?.Album.Folder != folder) return;

            if (image != null) await WriteAsync(folder, image, resize: true, ct);
            else await FetchAsync(folder, mbReleaseId!, mbReleaseGroupId, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Folder cover for {Path} failed", relativePath);
        }
    }

    /// <summary>
    /// Loads the cover of every album that has its own folder without a cover file: from the Cover Art Archive,
    /// otherwise (no MusicBrainz release, or no cover there) from Deezer or Spotify.
    /// Runs in the background, one run at a time; false when one is already running.
    /// </summary>
    public bool StartBackfill()
    {
        if (!_lock.Wait(0)) return false;
        _ = Task.Run(async () =>
        {
            try
            {
                await BackfillAsync(TimeSpan.FromMilliseconds(250), CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Loading album covers failed");
            }
            finally
            {
                _lock.Release();
                notifier.Notify();
            }
        });
        return true;
    }

    internal async Task BackfillAsync(TimeSpan pause, CancellationToken ct)
    {
        _progress = _written = _notFound = 0;
        var albums = (await catalog.GetAsync(ct)).Albums
            .Where(a => a.Folder != null && Find(storage.LibraryPath(a.Folder)) == null).ToList();
        _total = albums.Count;
        notifier.Notify();

        foreach (var album in albums)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var written = album.MbReleaseId != null && await FetchAsync(album.Folder!, album.MbReleaseId, ReleaseGroupOf(album), ct);
                if (written || await FetchExternalAsync(album, ct)) _written++;
                else _notFound++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Folder cover for {Folder} failed", album.Folder);
                _notFound++;
            }

            _progress++;
            notifier.Notify();
            await Task.Delay(pause, ct);
        }

        logger.LogInformation("Album covers: {Written} written, {NotFound} not found", _written, _notFound);
    }

    /// <summary>
    /// Cover from Deezer (1000 px), then Spotify (640 px): the album of a hit for the album's first song, when
    /// the hit fits the song and its album has the same name.
    /// </summary>
    private async Task<bool> FetchExternalAsync(CatalogAlbum album, CancellationToken ct)
    {
        if (album.Name == CatalogSnapshot.UnknownAlbum || album.Songs.Count == 0) return false;
        var settings = await settingsService.GetAsync(ct);
        var song = album.Songs[0];
        var (title, _) = TaggingRules.ExtractFeatures(song.Title);
        var artist = song.Artists.FirstOrDefault()?.Name;
        double? duration = song.Duration > 0 ? song.Duration : null;

        var searches = new List<(string Name, Func<Task<List<ExternalMatch>>> Run)>();
        if (settings.DeezerEnabled) searches.Add(("Deezer", () => deezer.SearchAsync(artist, title, ct)));
        if (settings.SpotifyEnabled && spotify.IsConfigured) searches.Add(("Spotify", () => spotify.SearchAsync(artist, title, ct)));

        foreach (var (name, run) in searches)
        {
            try
            {
                var match = (await run())
                    .Where(m => m.CoverUrl != null && TextUtil.Similarity(m.Album, album.Name) >= 0.85)
                    .Select(m => (Match: m, Score: ExternalSourceService.Rate(m, title, artist, duration)))
                    .Where(x => x.Score >= settings.ExternalMinScore)
                    .OrderByDescending(x => x.Score).Select(x => x.Match).FirstOrDefault();
                if (match != null && await coverArt.DownloadAsync(match.CoverUrl!, name, ct) is { } cover
                                  && await WriteAsync(album.Folder!, cover.Data, resize: !IsJpeg(cover.Data), ct))
                    return true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "{Source} cover search for {Folder} failed", name, album.Folder);
            }
        }

        return false;
    }

    /// <summary>The release group is not in the index; it is read from the tags of an album's song.</summary>
    private string? ReleaseGroupOf(CatalogAlbum album)
    {
        foreach (var song in album.Songs.Take(3))
        {
            try
            {
                var path = storage.LibraryPath(song.RelativePath);
                if (File.Exists(path) && TagIo.Read(path).MbReleaseGroupId is { Length: > 0 } id) return id;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not read the tags of {Path}", song.RelativePath);
            }
        }

        return null;
    }

    private async Task<bool> FetchAsync(string relativeFolder, string mbReleaseId, string? mbReleaseGroupId, CancellationToken ct)
    {
        var cover = await coverArt.GetFrontAsync(mbReleaseId, mbReleaseGroupId, Size, ct);
        // The thumbnails of the sources are JPEGs of at most this size already; anything else is converted.
        return cover != null && await WriteAsync(relativeFolder, cover.Data, resize: !IsJpeg(cover.Data), ct);
    }

    private async Task<bool> WriteAsync(string relativeFolder, byte[] image, bool resize, CancellationToken ct)
    {
        var folder = storage.LibraryPath(relativeFolder);
        if (!Directory.Exists(folder) || Find(folder) != null) return false;
        var jpeg = resize ? await audio.ResizeImageAsync(image, Size, ct) : image;
        if (jpeg == null) return false;

        var target = Path.Combine(folder, FileName);
        var tmp = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tmp, jpeg, ct);
            File.Move(tmp, target);
        }
        finally
        {
            File.Delete(tmp);
        }

        logger.LogInformation("Wrote {Path}", target);
        return true;
    }

    private static bool IsJpeg(byte[] data) => data.Length > 2 && data[0] == 0xFF && data[1] == 0xD8;
}
