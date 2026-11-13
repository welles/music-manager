using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MusicManager.Core.Configuration;
using MusicManager.Core.Data;
using MusicManager.Core.Model;

namespace MusicManager.Core.Ai;

/// <summary>
/// Claude API integration (official Anthropic C# SDK): tag cleanup and decisions for uncertain
/// release selections. Responses are constrained to JSON schemas (structured outputs).
/// </summary>
public sealed class AiService(IOptions<MusicManagerOptions> options, ILogger<AiService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private AnthropicClient? _client;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.AnthropicApiKey);

    internal AnthropicClient Client
    {
        get => _client ??= new AnthropicClient { ApiKey = options.Value.AnthropicApiKey };
        set => _client = value; // tests
    }

    // --- Tag cleanup -----------------------------------------------------------------------

    private const string CleanupSystemPrompt = """
        You clean up metadata of music files before they are looked up in MusicBrainz.
        Files come from downloads, video sites and streaming rippers, so tags and file names are
        often messy: video markers like "(Official Video)", "[HD]", uploader names, track numbers,
        "Artist - Title" in the title field, swapped artist/title, underscores or URLs.

        Return the real artist(s), the clean song title and the album if it can be determined
        from the given data. Rules:
        - Featured artists belong to the artists, not to the title. "artist" is the display string
          (e.g. "A feat. B"), "artists" lists every individual artist.
        - Keep version information that identifies a different recording (Remix, Live, Acoustic,
          Remastered, Extended Mix) in the title.
        - Do not invent information that is not present in the input. Leave album empty if unknown.
        - confidence is your confidence (0..1) that artist and title are correct.
        - notes: one short sentence in German describing what you changed.
        """;

    private static readonly Dictionary<string, JsonElement> CleanupSchema = Schema(new
    {
        type = "object",
        properties = new
        {
            artist = new { type = "string" },
            artists = new { type = "array", items = new { type = "string" } },
            title = new { type = "string" },
            album = new { type = "string" },
            confidence = new { type = "number" },
            notes = new { type = "string" },
        },
        required = new[] { "artist", "artists", "title", "album", "confidence", "notes" },
        additionalProperties = false,
    });

    public async Task<AiTagSuggestion?> CleanupTagsAsync(string fileName, TagSet original, TagSet preCleaned, AppSettings settings,
        CancellationToken ct)
    {
        var input = new
        {
            file_name = fileName,
            original_tags = Describe(original),
            pre_cleaned_tags = Describe(preCleaned),
        };

        var json = await AskAsync(settings.AiSimpleModel, CleanupSystemPrompt, JsonSerializer.Serialize(input, JsonOptions), CleanupSchema, settings, ct);
        if (json == null) return null;

        var result = JsonSerializer.Deserialize<CleanupResponse>(json, JsonOptions)!;
        return new AiTagSuggestion
        {
            Artist = result.Artist.NullIfEmptyString(),
            Artists = result.Artists.Where(a => !string.IsNullOrWhiteSpace(a)).ToList(),
            Title = result.Title.NullIfEmptyString(),
            Album = result.Album.NullIfEmptyString(),
            Confidence = result.Confidence,
            Notes = result.Notes,
            Model = settings.AiSimpleModel,
        };
    }

    // --- Release decision ------------------------------------------------------------------

    private const string DecisionSystemPrompt = """
        You help a music library tool choose the correct MusicBrainz release for an audio file.
        The tool found the recording (via audio fingerprint or text search) and scored every release
        the recording appears on with a heuristic, but the result was too close to decide.

        Choose the release the file most likely comes from. Consider, in this order:
        1. The file's existing tags (album name, album artist, track number) – the strongest hint.
        2. Other files that arrived in the same folder (they usually belong to the same release).
        3. Prefer the original studio album over compilations, live albums, bootlegs and later
           re-releases; prefer singles/EPs only if the tags or folder suggest a single.
        4. Duration differences between file and recording.

        If the data does not allow a well-founded choice, answer with decision "review" so a human
        decides. confidence is the probability (0..1) that your choice is correct. reasoning: two
        or three short sentences in German, shown to the user.
        Use release_id and recording_id exactly as given in the candidates.
        """;

    private static readonly Dictionary<string, JsonElement> DecisionSchema = Schema(new
    {
        type = "object",
        properties = new
        {
            decision = new { type = "string", @enum = new[] { "choose", "review" } },
            release_id = new { type = "string" },
            recording_id = new { type = "string" },
            confidence = new { type = "number" },
            reasoning = new { type = "string" },
        },
        required = new[] { "decision", "release_id", "recording_id", "confidence", "reasoning" },
        additionalProperties = false,
    });

    public async Task<AiDecisionRecord?> DecideReleaseAsync(TrackFile file, IReadOnlyList<string> groupFileNames,
        IReadOnlyList<ReleaseCandidate> candidates, AppSettings settings, CancellationToken ct)
    {
        var input = new
        {
            file_name = file.FileName,
            folder = Path.GetDirectoryName(file.RelativePath),
            duration_seconds = file.DurationSeconds is { } d ? Math.Round(d, 1) : (double?)null,
            original_tags = file.OriginalTags == null ? null : Describe(file.OriginalTags),
            cleaned_tags = file.CleanedTags == null ? null : Describe(file.CleanedTags),
            other_files_in_folder = groupFileNames.Where(n => n != file.FileName).Take(30),
            candidates = candidates.Take(15).Select(c => new
            {
                release_id = c.Release.ReleaseId,
                recording_id = c.RecordingId,
                recording = $"{c.RecordingArtist} - {c.RecordingTitle}",
                recording_length_seconds = c.RecordingLengthSeconds is { } l ? Math.Round(l, 1) : (double?)null,
                match_score = Math.Round(c.MatchScore, 2),
                release_title = c.Release.Title,
                release_artist = c.Release.ArtistString,
                date = c.Release.Date,
                country = c.Release.Country,
                status = c.Release.Status,
                type = string.Join(" + ", new[] { c.Release.PrimaryType }.Concat(c.Release.SecondaryTypes).OfType<string>()),
                formats = string.Join(", ", c.Release.Formats),
                track = c.Release.TrackPosition is { } p ? $"{p}/{c.Release.TrackCount}" : null,
                heuristic_score = Math.Round(c.Score, 1),
                score_breakdown = c.Breakdown,
            }),
        };

        var json = await AskAsync(settings.AiModel, DecisionSystemPrompt, JsonSerializer.Serialize(input, JsonOptions), DecisionSchema, settings, ct);
        if (json == null) return null;

        var r = JsonSerializer.Deserialize<DecisionResponse>(json, JsonOptions)!;
        var valid = candidates.Any(c => c.Release.ReleaseId == r.ReleaseId && c.RecordingId == r.RecordingId);
        var accepted = r.Decision == "choose" && valid && r.Confidence >= settings.AiMinConfidence;
        return new AiDecisionRecord
        {
            Decision = r.Decision,
            ReleaseId = r.ReleaseId.NullIfEmptyString(),
            RecordingId = r.RecordingId.NullIfEmptyString(),
            Confidence = r.Confidence,
            Reasoning = valid || r.Decision != "choose" ? r.Reasoning : r.Reasoning + " (Ungültige Kandidaten-ID – ignoriert)",
            Accepted = accepted,
            Model = settings.AiModel,
        };
    }

    // --- Title translation -----------------------------------------------------------------

    private const string TranslateSystemPrompt = """
        You translate song and album titles that are written in a non-Latin script (Japanese,
        Korean, Chinese, Cyrillic, Greek, Arabic, …) into English for a music library.
        - Give the natural English title a listener would expect, not a literal word-by-word gloss.
        - Katakana or other transliterated English loanwords become the English words
          (e.g. "リトルワールド" → "Little World").
        - Proper names stay names in their usual Latin spelling (romanization).
        - Keep the original capitalization style in English title case, no quotes, no explanations.
        Return one entry per input title, with "original" exactly as given.
        """;

    private static readonly Dictionary<string, JsonElement> TranslateSchema = Schema(new
    {
        type = "object",
        properties = new
        {
            translations = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new { original = new { type = "string" }, english = new { type = "string" } },
                    required = new[] { "original", "english" },
                    additionalProperties = false,
                },
            },
        },
        required = new[] { "translations" },
        additionalProperties = false,
    });

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _translationCache = new();

    /// <summary>Translates titles into English; results are cached (an album is translated once).</summary>
    public async Task<Dictionary<string, string>> TranslateAsync(IReadOnlyList<string> texts, string? artist, AppSettings settings,
        CancellationToken ct)
    {
        var result = new Dictionary<string, string>();
        var missing = new List<string>();
        foreach (var text in texts)
        {
            if (_translationCache.TryGetValue(text, out var cached)) result[text] = cached;
            else missing.Add(text);
        }

        if (missing.Count == 0) return result;

        var json = await AskAsync(settings.AiSimpleModel, TranslateSystemPrompt, JsonSerializer.Serialize(new { artist, titles = missing }, JsonOptions),
            TranslateSchema, settings, ct);
        if (json == null) return result;

        var response = JsonSerializer.Deserialize<TranslateResponse>(json, JsonOptions)!;
        foreach (var t in response.Translations.Where(t => missing.Contains(t.Original) && !string.IsNullOrWhiteSpace(t.English)))
        {
            _translationCache[t.Original] = t.English.Trim();
            result[t.Original] = t.English.Trim();
        }

        return result;
    }

    private sealed record TranslateResponse(List<TranslationItem> Translations);

    private sealed record TranslationItem(string Original, string English);

    // --- Genres ----------------------------------------------------------------------------

    public sealed record GenreContext(string? Artist, string? Title, string? Album, string? Year);

    public sealed record GenreVerdict(List<string> Genres, double Confidence, string Reason);

    private const string GenreCheckSystemPrompt = """
        You check genre tags of a music file before they are written. The genres come from a broad
        source (tags of the artist, not of this song), and such sources mix up artists with the same
        name: a soft pop band and a death metal band can share one page.

        Keep only the given genres that plausibly describe this song by this artist. Drop all of them
        if they look like they belong to a different artist with the same name or clearly do not fit.
        Never add genres that are not in the list. If you do not know the artist, keep genres that fit
        the title, album and the other genres, and lower your confidence.
        - keep: the genres to keep, spelled exactly as given.
        - confidence: 0..1 that the kept genres are right.
        - reason: one short sentence in German.
        """;

    private const string GenreSuggestSystemPrompt = """
        You suggest genres for a music file for which no database has any. Use genre names as
        MusicBrainz uses them (lower case, e.g. "drum and bass", "indie pop", "hip hop").
        Only suggest genres if you actually know the artist or the song; if you do not, return an
        empty list – an empty result is better than a guess. At most three genres, most specific first.
        - genres: the suggested genres.
        - confidence: 0..1 that they are right.
        - reason: one short sentence in German.
        """;

    private static readonly Dictionary<string, JsonElement> GenreCheckSchema = Schema(new
    {
        type = "object",
        properties = new
        {
            keep = new { type = "array", items = new { type = "string" } },
            confidence = new { type = "number" },
            reason = new { type = "string" },
        },
        required = new[] { "keep", "confidence", "reason" },
        additionalProperties = false,
    });

    private static readonly Dictionary<string, JsonElement> GenreSuggestSchema = Schema(new
    {
        type = "object",
        properties = new
        {
            genres = new { type = "array", items = new { type = "string" } },
            confidence = new { type = "number" },
            reason = new { type = "string" },
        },
        required = new[] { "genres", "confidence", "reason" },
        additionalProperties = false,
    });

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, GenreVerdict?> _genreCache = new();

    /// <summary>Which of the given genres fit this song (the rest is dropped). Null when the AI is not available.</summary>
    public async Task<GenreVerdict?> CheckGenresAsync(GenreContext song, IReadOnlyList<string> genres, string source, AppSettings settings,
        CancellationToken ct)
    {
        var key = $"check|{song.Artist}|{song.Album ?? song.Title}|{string.Join(",", genres)}".ToLowerInvariant();
        if (_genreCache.TryGetValue(key, out var cached)) return cached;

        var input = new { song.Artist, song.Title, song.Album, song.Year, source, genres };
        var json = await AskAsync(settings.AiModel, GenreCheckSystemPrompt, JsonSerializer.Serialize(input, JsonOptions), GenreCheckSchema, settings, ct);
        if (json == null) return null;

        var r = JsonSerializer.Deserialize<GenreCheckResponse>(json, JsonOptions)!;
        var keep = genres.Where(g => r.Keep.Contains(g, StringComparer.OrdinalIgnoreCase)).ToList();
        return _genreCache[key] = new GenreVerdict(keep, r.Confidence, r.Reason);
    }

    /// <summary>Genres the AI knows for the song (trial, lowest confidence). Null when the AI is not available.</summary>
    public async Task<GenreVerdict?> SuggestGenresAsync(GenreContext song, AppSettings settings, CancellationToken ct)
    {
        var key = $"suggest|{song.Artist}|{song.Album ?? song.Title}".ToLowerInvariant();
        if (_genreCache.TryGetValue(key, out var cached)) return cached;

        var input = new { song.Artist, song.Title, song.Album, song.Year };
        var json = await AskAsync(settings.AiModel, GenreSuggestSystemPrompt, JsonSerializer.Serialize(input, JsonOptions), GenreSuggestSchema, settings, ct);
        if (json == null) return null;

        var r = JsonSerializer.Deserialize<GenreSuggestResponse>(json, JsonOptions)!;
        return _genreCache[key] = new GenreVerdict(r.Genres.Take(3).ToList(), r.Confidence, r.Reason);
    }

    private sealed record GenreCheckResponse(List<string> Keep, double Confidence, string Reason);

    private sealed record GenreSuggestResponse(List<string> Genres, double Confidence, string Reason);

    // --- Shared ----------------------------------------------------------------------------

    private async Task<string?> AskAsync(string model, string system, string userJson, Dictionary<string, JsonElement> schema,
        AppSettings settings, CancellationToken ct)
    {
        if (!IsConfigured) return null;

        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = 16000,
            System = system,
            OutputConfig = new OutputConfig
            {
                Effort = settings.AiEffort.ToLowerInvariant() switch
                {
                    "high" => Effort.High,
                    "medium" => Effort.Medium,
                    _ => Effort.Low,
                },
                Format = new JsonOutputFormat { Schema = schema },
            },
            Messages = [new UserMessageParam { Content = userJson }],
        };

        var response = await Client.Messages.Create(parameters, cancellationToken: ct);
        if (response.StopReason == StopReason.Refusal)
        {
            logger.LogWarning("Claude declined the request");
            return null;
        }

        var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        logger.LogDebug("Claude response: {Text}", text);
        return text.Length == 0 ? null : text;
    }

    private static Dictionary<string, object?> Describe(TagSet t)
    {
        var d = new Dictionary<string, object?>
        {
            ["title"] = t.Title,
            ["artist"] = t.Artist,
            ["artists"] = t.Artists.Count > 0 ? t.Artists : null,
            ["album"] = t.Album,
            ["album_artist"] = t.AlbumArtist,
            ["track"] = t.TrackNumber?.ToString(CultureInfo.InvariantCulture),
            ["date"] = t.Date,
        };
        foreach (var (k, v) in t.Extra.Where(e => !e.Key.StartsWith('_')).Take(10))
            d[$"extra:{k}"] = v.Length > 200 ? v[..200] : v;
        return d.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static Dictionary<string, JsonElement> Schema(object schema)
    {
        var element = JsonSerializer.SerializeToElement(schema);
        return element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private sealed record CleanupResponse(string Artist, List<string> Artists, string Title, string Album, double Confidence, string Notes);

    private sealed record DecisionResponse(string Decision, string ReleaseId, string RecordingId, double Confidence, string Reasoning);
}

internal static class StringExtensions
{
    public static string? NullIfEmptyString(this string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
