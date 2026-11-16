using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MusicManager.Core.Configuration;

namespace MusicManager.Core.Services;

/// <summary>A download job of MusicFab.Manager as its API reports it.</summary>
/// <param name="State">Queued, Opening, Analyzing, Selecting, Downloading, Completed, Failed or Cancelled.</param>
/// <param name="Files">Paths in the MusicFab container, below its <c>/output</c> (this inbox).</param>
public sealed record MusicFabJob(Guid Id, string State, string? Title, int? Tracks, IReadOnlyList<string> Files, double? Progress, string? Error)
{
    public bool IsFinished => State is "Completed" or "Failed" or "Cancelled";
}

/// <summary>A request MusicFab.Manager refused or could not be reached for; the message is for the user.</summary>
public sealed class MusicFabException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// MusicFab.Manager's REST API (<c>/api/downloads</c>, <c>X-Api-Key</c>): it opens a URL in MusicFab, selects all tracks
/// and downloads them into the inbox. Without URL and key the client is off. Behind Cloudflare Access it sends the
/// service token of <see cref="MusicManagerOptions.MusicFabAccessClientId"/>; redirects are not followed, since the only
/// one on the API is Access sending an unknown client to its login.
/// </summary>
public sealed class MusicFabClient(HttpClient http, IOptions<MusicManagerOptions> options)
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.Value.MusicFabUrl) && !string.IsNullOrWhiteSpace(options.Value.MusicFabApiKey);

    /// <summary>Asks MusicFab to download a track, album or playlist page.</summary>
    public async Task<MusicFabJob> RequestAsync(string url, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, "api/downloads");
        request.Content = JsonContent.Create(new { url });
        using var response = await SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.BadRequest)
            throw new MusicFabException($"MusicFab nimmt die Adresse nicht an: {await ProblemAsync(response, ct)}", response.StatusCode);
        await EnsureSuccessAsync(response, ct);
        return await ReadJobAsync(response, ct);
    }

    /// <summary>The job's current state; null when MusicFab does not know it (anymore).</summary>
    public async Task<MusicFabJob?> GetAsync(Guid id, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, $"api/downloads/{id}");
        using var response = await SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        return await ReadJobAsync(response, ct);
    }

    /// <summary>Cancels a job that has not started yet; false when it already started (or is gone).</summary>
    public async Task<bool> CancelAsync(Guid id, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Delete, $"api/downloads/{id}");
        using var response = await SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound) return false;
        await EnsureSuccessAsync(response, ct);
        return true;
    }

    public static MusicFabJob Parse(JsonElement e) => new(
        e.GetProperty("id").GetGuid(),
        e.TryGetProperty("state", out var state) ? state.GetString() ?? "" : "",
        Str(e, "title"),
        e.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Number ? tracks.GetInt32() : null,
        e.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array
            ? files.EnumerateArray().Select(f => f.GetString()).OfType<string>().ToList()
            : [],
        e.TryGetProperty("progress", out var progress) && progress.ValueKind == JsonValueKind.Number ? progress.GetDouble() : null,
        Str(e, "error"));

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        if (!IsConfigured) throw new MusicFabException("MusicFab ist nicht eingerichtet (MusicManager__MusicFabUrl, MusicManager__MusicFabApiKey).");
        var request = new HttpRequestMessage(method, new Uri(options.Value.MusicFabUrl!.TrimEnd('/') + "/" + path));
        request.Headers.Add("X-Api-Key", options.Value.MusicFabApiKey);
        if (!string.IsNullOrWhiteSpace(options.Value.MusicFabAccessClientId))
        {
            request.Headers.Add("CF-Access-Client-Id", options.Value.MusicFabAccessClientId);
            request.Headers.Add("CF-Access-Client-Secret", options.Value.MusicFabAccessClientSecret);
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new MusicFabException($"MusicFab ist nicht erreichbar: {ex.Message}");
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                new MusicFabException("MusicFab lehnt den API-Key ab (MusicManager__MusicFabApiKey).", response.StatusCode),
            // MusicFab.Manager answers neither: Access sends unknown clients to its login or refuses a wrong token
            HttpStatusCode.Forbidden or >= HttpStatusCode.Ambiguous and < HttpStatusCode.BadRequest =>
                new MusicFabException(
                    "Cloudflare Access lässt die Anfrage nicht durch: Service-Token fehlt oder ist falsch " +
                    "(MusicManager__MusicFabAccessClientId, MusicManager__MusicFabAccessClientSecret).", response.StatusCode),
            _ => new MusicFabException($"MusicFab antwortet {(int)response.StatusCode}: {await ProblemAsync(response, ct)}", response.StatusCode),
        };
    }

    private static async Task<MusicFabJob> ReadJobAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return Parse(doc.RootElement);
    }

    // Validation problems ({"errors": {"url": ["…"]}}), plain text (409) or nothing
    private static async Task<string> ProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Object)
                return string.Join("; ", errors.EnumerateObject().SelectMany(p => p.Value.EnumerateArray().Select(v => v.GetString())));
            if (doc.RootElement.ValueKind == JsonValueKind.String) return doc.RootElement.GetString() ?? "";
        }
        catch (JsonException)
        {
        }

        return string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase ?? "" : body.Trim();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
