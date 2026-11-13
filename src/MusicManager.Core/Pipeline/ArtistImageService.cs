using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MusicManager.Core.Configuration;
using MusicManager.Core.Data;
using MusicManager.Core.Rules;
using MusicManager.Core.Services;

namespace MusicManager.Core.Pipeline;

/// <summary>
/// Artist images: Wikidata (exact, via the MusicBrainz artist), then Deezer, then Spotify (exact name
/// match only). Images are fetched in the background and cached in the data folder, from where the web app
/// and the Subsonic API serve them.
/// </summary>
public sealed class ArtistImageService(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settingsService,
    MusicBrainzService musicBrainz,
    DeezerClient deezer,
    SpotifyClient spotify,
    CoverArtService downloader,
    AudioTools audio,
    Storage storage,
    IOptions<MusicManagerOptions> options,
    IHttpClientFactory httpFactory,
    ILogger<ArtistImageService> logger) : BackgroundService
{
    /// <summary>Artists without an image are looked up again after this time.</summary>
    private static readonly TimeSpan RetryNotFound = TimeSpan.FromDays(30);

    private readonly Channel<(string Name, string? MbId)> _queue = Channel.CreateUnbounded<(string, string?)>();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _queued = new();

    private string DataRoot => Path.GetFullPath(options.Value.DataPath);
    private string ImageDir => Path.Combine(DataRoot, "artists");

    public static string KeyOf(string name) => TextUtil.Normalize(name);

    /// <summary>Path of the cached image, or null (then a lookup is queued).</summary>
    public async Task<string?> GetImagePathAsync(string name, string? mbArtistId, CancellationToken ct)
    {
        var key = KeyOf(name);
        if (key.Length == 0) return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entry = await db.ArtistImages.AsNoTracking().FirstOrDefaultAsync(a => a.Key == key, ct);
        if (entry?.FilePath != null && File.Exists(Path.Combine(DataRoot, entry.FilePath)))
            return Path.Combine(DataRoot, entry.FilePath);

        if (entry == null || entry.FetchedAt < DateTimeOffset.UtcNow - RetryNotFound)
            Enqueue(name, mbArtistId);
        return null;
    }

    public void Enqueue(string name, string? mbArtistId)
    {
        if (_queued.TryAdd(KeyOf(name), 0))
            _queue.Writer.TryWrite((name, mbArtistId));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (name, mbId) in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                if ((await settingsService.GetAsync(stoppingToken)).ArtistImagesEnabled)
                    await FetchAsync(name, mbId, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Artist image for {Artist} failed", name);
            }
            finally
            {
                _queued.TryRemove(KeyOf(name), out _);
            }
        }
    }

    private async Task FetchAsync(string name, string? mbId, CancellationToken ct)
    {
        string? url = null, source = null;

        if (Guid.TryParse(mbId, out _))
        {
            var qid = await musicBrainz.GetWikidataIdAsync(mbId!, ct);
            if (qid.Length > 0 && await WikidataImageAsync(qid, ct) is { } wiki)
                (url, source) = (wiki, "wikidata");
        }

        if (url == null && await deezer.FindArtistImageAsync(name, ct) is { } dz)
            (url, source) = (dz, "deezer");
        if (url == null && await spotify.FindArtistImageAsync(name, ct) is { } sp)
            (url, source) = (sp, "spotify");

        string? relative = null;
        if (url != null && await downloader.DownloadAsync(url, source!, ct) is { } image
            && await audio.ResizeImageAsync(image.Data, 600, ct) is { } jpeg)
        {
            Directory.CreateDirectory(ImageDir);
            relative = Path.Combine("artists", $"{Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(KeyOf(name))))[..16]}.jpg");
            await File.WriteAllBytesAsync(Path.Combine(DataRoot, relative), jpeg, ct);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var key = KeyOf(name);
        var entry = await db.ArtistImages.FirstOrDefaultAsync(a => a.Key == key, ct);
        if (entry == null)
        {
            entry = new ArtistImage { Key = key, Name = name };
            db.ArtistImages.Add(entry);
        }

        entry.MbArtistId = mbId ?? entry.MbArtistId;
        entry.Source = source;
        entry.FilePath = relative;
        entry.FetchedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Artist image for {Artist}: {Source}", name, source ?? "not found");
    }

    /// <summary>Image (P18) of a Wikidata item, as a Commons thumbnail URL.</summary>
    private async Task<string?> WikidataImageAsync(string qid, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("wikimedia");
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://www.wikidata.org/wiki/Special:EntityData/{qid}.json", ct));
        if (!doc.RootElement.GetProperty("entities").TryGetProperty(qid, out var entity)
            || !entity.TryGetProperty("claims", out var claims) || !claims.TryGetProperty("P18", out var p18))
            return null;
        var file = p18[0].GetProperty("mainsnak").GetProperty("datavalue").GetProperty("value").GetString();
        return file == null ? null : $"https://commons.wikimedia.org/wiki/Special:FilePath/{Uri.EscapeDataString(file)}?width=800";
    }
}
