using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MusicManager.Core.Configuration;

namespace MusicManager.Core.Services;

public sealed record AcoustIdMatch(string RecordingId, double Score);

/// <summary>AcoustID web service: fingerprint → MusicBrainz recording IDs.</summary>
public sealed class AcoustIdClient(HttpClient http, IOptions<MusicManagerOptions> options, ILogger<AcoustIdClient> logger)
{
    private static readonly SemaphoreSlim RateLimit = new(1, 1);
    private static DateTime _lastRequest = DateTime.MinValue;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.AcoustIdApiKey);

    public async Task<List<AcoustIdMatch>> LookupAsync(Fingerprint fp, CancellationToken ct)
    {
        if (!IsConfigured)
            return [];

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

            var matches = new Dictionary<string, double>();
            foreach (var result in doc.RootElement.GetProperty("results").EnumerateArray())
            {
                var score = result.GetProperty("score").GetDouble();
                if (!result.TryGetProperty("recordings", out var recs)) continue;
                foreach (var rec in recs.EnumerateArray())
                {
                    var id = rec.GetProperty("id").GetString()!;
                    matches[id] = Math.Max(matches.GetValueOrDefault(id), score);
                }
            }

            return matches.Select(m => new AcoustIdMatch(m.Key, m.Value)).OrderByDescending(m => m.Score).ToList();
        }
        finally
        {
            RateLimit.Release();
        }
    }
}
