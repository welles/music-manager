using System.Net;
using Microsoft.Extensions.Logging;

namespace MusicManager.Core.Services;

public sealed record CoverImage(byte[] Data, string MimeType, string Source);

/// <summary>Front covers from the Cover Art Archive (release, then release group).</summary>
public sealed class CoverArtService(HttpClient http, ILogger<CoverArtService> logger)
{
    public async Task<CoverImage?> GetFrontAsync(string releaseId, string? releaseGroupId, int size, CancellationToken ct)
    {
        var thumb = size <= 250 ? "250" : size <= 500 ? "500" : "1200";

        var cover = await TryGet($"https://coverartarchive.org/release/{releaseId}/front-{thumb}", "Cover Art Archive (Release)", ct);
        if (cover == null && releaseGroupId != null)
            cover = await TryGet($"https://coverartarchive.org/release-group/{releaseGroupId}/front-{thumb}", "Cover Art Archive (Release-Gruppe)", ct);
        return cover;
    }

    /// <summary>Downloads a cover from any URL (e.g. Spotify/Deezer).</summary>
    public Task<CoverImage?> DownloadAsync(string url, string source, CancellationToken ct) => TryGet(url, source, ct);

    private async Task<CoverImage?> TryGet(string url, string source, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
            var data = await response.Content.ReadAsByteArrayAsync(ct);
            var mime = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            return new CoverImage(data, mime, source);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Cover download failed: {Url}", url);
            return null;
        }
    }
}
