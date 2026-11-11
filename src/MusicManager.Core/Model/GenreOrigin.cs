namespace MusicManager.Core.Model;

/// <summary>How far a file's genres can be trusted.</summary>
public enum GenreConfidence
{
    /// <summary>Suggested by the AI without any source (trial).</summary>
    VeryLow,

    /// <summary>Broad artist tags, or artist genres nobody checked.</summary>
    Low,

    /// <summary>Folksonomy of the track/album, checked artist genres, genres already in the file.</summary>
    Medium,

    /// <summary>MusicBrainz genres of the recording or release.</summary>
    High,
}

/// <summary>
/// Where the genres of a file come from, written as <c>MUSICMANAGER_GENRE_SOURCE = source|confidence</c>
/// (e.g. <c>lastfm-track|medium</c>) so it survives a lost database and the library index can read it.
/// </summary>
public static class GenreOrigin
{
    public const string Marker = "MUSICMANAGER_GENRE_SOURCE";

    public const string MusicBrainz = "musicbrainz";
    public const string LastFmTrack = "lastfm-track";
    public const string LastFmAlbum = "lastfm-album";
    public const string MusicBrainzArtist = "musicbrainz-artist";
    public const string Existing = "existing";
    public const string LastFmArtist = "lastfm-artist";
    public const string Ai = "ai";
    public const string Given = "given";

    public static string Format(string source, GenreConfidence confidence) => $"{source}|{confidence.ToString().ToLowerInvariant()}";

    public static (string Source, GenreConfidence Confidence)? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split('|', 2, StringSplitOptions.TrimEntries);
        var confidence = parts.Length == 2 && Enum.TryParse<GenreConfidence>(parts[1], true, out var c) ? c : GenreConfidence.Medium;
        return (parts[0], confidence);
    }

    public static string SourceLabel(string source) => source switch
    {
        MusicBrainz => "MusicBrainz",
        LastFmTrack => "Last.fm (Titel)",
        LastFmAlbum => "Last.fm (Album)",
        MusicBrainzArtist => "MusicBrainz (Künstler)",
        Existing => "vorhandene Tags",
        LastFmArtist => "Last.fm (Künstler)",
        Ai => "KI-Vorschlag",
        Given => "Quelle/Eingabe",
        _ => source,
    };

    public static string ConfidenceLabel(GenreConfidence c) => c switch
    {
        GenreConfidence.High => "hoch",
        GenreConfidence.Medium => "mittel",
        GenreConfidence.Low => "niedrig",
        _ => "sehr niedrig",
    };

    /// <summary>"Last.fm (Titel), Belastbarkeit mittel" for the UI.</summary>
    public static string Describe(string? value) =>
        Parse(value) is { } o ? $"{SourceLabel(o.Source)}, Belastbarkeit {ConfidenceLabel(o.Confidence)}" : "unbekannt";
}
