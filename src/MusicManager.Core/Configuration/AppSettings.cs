namespace MusicManager.Core.Configuration;

public enum AiCleanupMode
{
    Off,
    WhenMessy,
    Always,
}

/// <summary>
/// Runtime settings, editable in the web frontend and stored as JSON in the database.
/// Defaults are taken from the previous Picard configuration where possible.
/// </summary>
public sealed class AppSettings
{
    // --- Inbox ---------------------------------------------------------------------------
    /// <summary>A group is processed once nothing changed in it for this many seconds.</summary>
    public int InboxQuietSeconds { get; set; } = 60;

    /// <summary>Interval for scanning the inbox.</summary>
    public int InboxScanIntervalSeconds { get; set; } = 15;

    public bool InboxWatchEnabled { get; set; } = true;

    public List<string> AudioExtensions { get; set; } =
        [".mp3", ".m4a", ".mp4", ".aac", ".flac", ".wav", ".ogg", ".opus", ".wma", ".alac", ".aiff", ".aif", ".mka", ".mkv", ".webm"];

    // --- Automation ---------------------------------------------------------------------
    /// <summary>When false every file waits in the review queue with a tag preview.</summary>
    public bool AutoFileConfidentMatches { get; set; } = true;

    // --- Matching thresholds -------------------------------------------------------------
    /// <summary>Minimum AcoustID score for a fingerprint result to be used (Picard file lookup: 0.7).</summary>
    public double AcoustIdMinScore { get; set; } = 0.7;

    /// <summary>Minimum MusicBrainz search score (0-100) for text search fallback.</summary>
    public int TextSearchMinScore { get; set; } = 85;

    /// <summary>Maximum number of recordings looked up per file.</summary>
    public int MaxRecordingsPerFile { get; set; } = 5;

    /// <summary>Best candidate must lead the best candidate of another release group by this many points.</summary>
    public double ScoreMarginThreshold { get; set; } = 8;

    /// <summary>Minimum normalized score (0..1) of the best candidate, otherwise uncertain (Picard track matching: 0.4).</summary>
    public double MinCandidateConfidence { get; set; } = 0.4;

    /// <summary>Duration differences below this are ignored (Picard: 2 seconds).</summary>
    public double LengthToleranceSeconds { get; set; } = 2;

    // --- Release scoring weights -----------------------------------------------------------
    public ScoringWeights Weights { get; set; } = new();

    /// <summary>Preferred release countries in order (Picard: XW, XE, DE, US).</summary>
    public List<string> PreferredCountries { get; set; } = ["XW", "XE", "DE", "US"];

    /// <summary>Preferred medium formats (Picard: Digital Media).</summary>
    public List<string> PreferredFormats { get; set; } = ["Digital Media"];

    // --- Metadata -----------------------------------------------------------------------
    /// <summary>Separators for splitting composed artist strings (only used for data without MusicBrainz credits).</summary>
    public List<string> ArtistSplitters { get; set; } = [.. Rules.ArtistRuleSet.DefaultSplitters];

    /// <summary>Artist alias locale used to translate artist names (Picard: en). Empty disables translation.</summary>
    public string ArtistLocale { get; set; } = "en";

    /// <summary>Use the artist's main name instead of the credited name.</summary>
    public bool StandardizeArtists { get; set; } = true;

    public bool ConvertPunctuation { get; set; } = true;

    public bool UseGenres { get; set; } = true;
    public bool UseArtistGenres { get; set; } = true;
    public int MaxGenres { get; set; } = 10;
    public int MinGenreUsagePercent { get; set; } = 50;
    public List<string> GenreFilter { get; set; } = ["seen live", "favorites", "fixme", "owned"];

    public int CoverSize { get; set; } = 500;

    /// <summary>Additional files moved along with the audio files.</summary>
    public List<string> AdditionalFilePatterns { get; set; } = ["*.jpg", "*.png"];

    // --- Conversion ---------------------------------------------------------------------
    public int AacBitrateKbps { get; set; } = 256;

    // --- Trash --------------------------------------------------------------------------
    public int TrashRetentionDays { get; set; } = 30;

    // --- AI -----------------------------------------------------------------------------
    public bool AiDecisionEnabled { get; set; } = true;
    public AiCleanupMode AiCleanupMode { get; set; } = AiCleanupMode.WhenMessy;
    public bool AiSuggestForUnknown { get; set; } = true;

    /// <summary>AI decisions below this confidence go to manual review.</summary>
    public double AiMinConfidence { get; set; } = 0.8;

    public string AiModel { get; set; } = "claude-opus-5-5";

    /// <summary>Thinking effort for AI requests: low, medium or high.</summary>
    public string AiEffort { get; set; } = "low";
}

public sealed class ScoringWeights
{
    /// <summary>Multiplied with the AcoustID / search score (0..1).</summary>
    public double Match { get; set; } = 30;

    public double TitleSimilarity { get; set; } = 10;
    public double ArtistSimilarity { get; set; } = 10;
    public double AlbumSimilarity { get; set; } = 25;
    public double AlbumArtistSimilarity { get; set; } = 5;
    public double TrackNumber { get; set; } = 5;

    /// <summary>Bonus scaled by the share of other files of the same group on the same release.</summary>
    public double GroupSameRelease { get; set; } = 25;

    /// <summary>Bonus scaled by the share of other files of the same group in the same release group.</summary>
    public double GroupSameReleaseGroup { get; set; } = 10;

    public double LengthMatch { get; set; } = 10;
    public double LengthMismatchPerSecond { get; set; } = 1;
    public double LengthMismatchMax { get; set; } = 20;

    public double EarliestRelease { get; set; } = 10;

    /// <summary>Number of years after which the date bonus reaches zero.</summary>
    public double EarliestReleaseYears { get; set; } = 10;

    public double Country { get; set; } = 6;
    public double Format { get; set; } = 3;

    public Dictionary<string, double> Status { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Official"] = 10,
        ["Promotion"] = 0,
        ["Bootleg"] = -25,
        ["Pseudo-Release"] = -30,
        ["Withdrawn"] = -10,
        ["Cancelled"] = -30,
    };

    public Dictionary<string, double> PrimaryType { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Album"] = 10,
        ["EP"] = 4,
        ["Single"] = 2,
        ["Broadcast"] = -5,
        ["Other"] = 0,
    };

    public Dictionary<string, double> SecondaryType { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Compilation"] = -15,
        ["Live"] = -20,
        ["Remix"] = -10,
        ["DJ-mix"] = -20,
        ["Mixtape/Street"] = -5,
        ["Soundtrack"] = -5,
        ["Demo"] = -10,
        ["Spokenword"] = -20,
        ["Interview"] = -20,
        ["Audiobook"] = -20,
        ["Audio drama"] = -20,
        ["Field recording"] = -10,
    };
}
