namespace MusicManager.Core.Configuration;

/// <summary>
/// Static configuration taken from appsettings / environment variables (prefix <c>MusicManager__</c>).
/// Everything that should be editable at runtime lives in <see cref="AppSettings"/> instead.
/// </summary>
public sealed class MusicManagerOptions
{
    public const string Section = "MusicManager";

    /// <summary>Folder that receives new files. Mounted as a volume in Docker.</summary>
    public string InboxPath { get; set; } = "/inbox";

    /// <summary>Navidrome library root. Mounted as a volume in Docker.</summary>
    public string LibraryPath { get; set; } = "/music";

    /// <summary>Folder for replaced or discarded files (instead of deleting them).</summary>
    public string TrashPath { get; set; } = "/trash";

    /// <summary>Database, work files and other application state.</summary>
    public string DataPath { get; set; } = "/data";

    /// <summary>AcoustID application API key (https://acoustid.org/new-application).</summary>
    public string? AcoustIdApiKey { get; set; }

    /// <summary>Anthropic API key; AI features stay disabled without it.</summary>
    public string? AnthropicApiKey { get; set; }

    /// <summary>Spotify app (client credentials) for the fallback search; optional.</summary>
    public string? SpotifyClientId { get; set; }

    public string? SpotifyClientSecret { get; set; }

    /// <summary>Contact (mail address or URL) sent in the MusicBrainz User-Agent.</summary>
    public string MusicBrainzContact { get; set; } = "https://github.com/welles/music-manager";

    /// <summary>MusicBrainz server, e.g. for a local mirror. Empty = musicbrainz.org.</summary>
    public string? MusicBrainzServer { get; set; }

    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public string FpcalcPath { get; set; } = "fpcalc";

    public string DatabasePath => Path.Combine(DataPath, "musicmanager.db");
    public string WorkPath => Path.Combine(DataPath, "work");
}
