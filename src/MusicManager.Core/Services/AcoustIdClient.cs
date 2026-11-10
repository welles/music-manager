using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MusicManager.Core.Configuration;

namespace MusicManager.Core.Services;

public sealed record AcoustIdMatch(string RecordingId, double Score);

/// <summary>One AcoustID track (a cluster of fingerprints) with its linked MusicBrainz recordings.</summary>
public sealed record AcoustIdTrack(string AcoustId, double Score, List<string> RecordingIds);

public sealed record AcoustIdResult(List<AcoustIdTrack> Tracks)
{
    /// <summary>Best score per recording.</summary>
    public List<AcoustIdMatch> Matches => Tracks
        .SelectMany(t => t.RecordingIds.Select(r => (Recording: r, t.Score)))
        .GroupBy(x => x.Recording)
        .Select(g => new AcoustIdMatch(g.Key, g.Max(x => x.Score)))
        .OrderByDescending(m => m.Score)
        .ToList();

    /// <summary>
    /// AcoustID for the file: the best track linked to the given recording, otherwise the best
    /// track above the threshold.
    /// </summary>
    public string? BestAcoustId(string? recordingId, double minScore) =>
        (recordingId == null ? null : Tracks.Where(t => t.RecordingIds.Contains(recordingId)).MaxBy(t => t.Score)?.AcoustId)
        ?? Tracks.Where(t => t.Score >= minScore).MaxBy(t => t.Score)?.AcoustId;
}

/// <summary>AcoustID web service: fingerprint → AcoustID tracks → MusicBrainz recording IDs.</summary>
public sealed class AcoustIdClient(HttpClient http, IOptions<MusicManagerOptions> options, ILogger<AcoustIdClient> logger)
{
    private static readonly SemaphoreSlim RateLimit = new(1, 1);
    private static DateTime _lastRequest = DateTime.MinValue;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.AcoustIdApiKey);

    public async Task<AcoustIdResult> LookupAsync(Fingerprint fp, CancellationToken ct)
    {
        if (!IsConfigured)
            return new AcoustIdResult([]);

        await RateLimit.WaitAsync(ct);
        try
        {
            // AcoustID allows 3 requests per second.
            var wait = _lastRequest.AddMilliseconds(350) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client"] = options.Value.AcoustIdApiKey!,
                ["meta"] = "recordingids",
                ["format"] = "json",
                ["duration"] = ((int)Math.Round(fp.DurationSeconds)).ToString(CultureInfo.InvariantCulture),
                ["fingerprint"] = fp.Value,
            });
            using var response = await http.PostAsync("https://api.acoustid.org/v2/lookup", content, ct);
            _lastRequest = DateTime.UtcNow;
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("status").GetString() != "ok")
            {
                logger.LogWarning("AcoustID error: {Json}", json);
                throw new InvalidOperationException("AcoustID: " + json);
            }

            var tracks = new List<AcoustIdTrack>();
            foreach (var result in doc.RootElement.GetProperty("results").EnumerateArray())
            {
                var recordings = result.TryGetProperty("recordings", out var recs)
                    ? recs.EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToList()
                    : [];
                tracks.Add(new AcoustIdTrack(result.GetProperty("id").GetString()!, result.GetProperty("score").GetDouble(), recordings));
            }

            return new AcoustIdResult(tracks.OrderByDescending(t => t.Score).ToList());
        }
        finally
        {
            RateLimit.Release();
        }
    }
}
