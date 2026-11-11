using MusicManager.Core.Catalog;
using MusicManager.Core.Pipeline;

namespace MusicManager.Web.Subsonic;

// System endpoints: ping, license, extensions, music folders, user, scan.
public static partial class SubsonicEndpoints
{
    /// <summary>The single music folder (the library).</summary>
    public const int MusicFolderId = 1;

    private static void MapSystem()
    {
        Add("ping", _ => Task.FromResult(Ok()));

        Add("getLicense", _ => Task.FromResult(Ok("license", new { valid = true })));

        Add("getOpenSubsonicExtensions", _ => Task.FromResult(Ok("openSubsonicExtensions", new object[]
        {
            new { name = "apiKeyAuthentication", versions = new[] { 1 } },
            new { name = "formPost", versions = new[] { 1 } },
        })));

        Add("getMusicFolders", _ => Task.FromResult(Ok("musicFolders", new
        {
            musicFolder = new[] { new { id = MusicFolderId, name = "Music" } },
        })));

        // Single user (the owner); the API key stands for it.
        Add("getUser", r => Task.FromResult(Ok("user", new
        {
            username = r.Get("username") is { Length: > 0 } u ? u : "owner",
            scrobblingEnabled = true,
            adminRole = true,
            settingsRole = true,
            downloadRole = true,
            uploadRole = false,
            playlistRole = true,
            coverArtRole = true,
            commentRole = false,
            podcastRole = false,
            streamRole = true,
            jukeboxRole = false,
            shareRole = false,
            videoConversionRole = false,
            folder = new[] { MusicFolderId },
        })));

        Add("getScanStatus", async r =>
        {
            var catalog = await r.Service<MediaCatalog>().GetAsync(r.Aborted);
            return Ok("scanStatus", new { scanning = r.Service<LibraryIndexer>().Scanning, count = catalog.Songs.Count });
        });

        Add("startScan", async r =>
        {
            var indexer = r.Service<LibraryIndexer>();
            indexer.ScanNow();
            var catalog = await r.Service<MediaCatalog>().GetAsync(r.Aborted);
            return Ok("scanStatus", new { scanning = true, count = catalog.Songs.Count });
        });
    }
}
