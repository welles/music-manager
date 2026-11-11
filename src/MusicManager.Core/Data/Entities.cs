using MusicManager.Core.Model;

namespace MusicManager.Core.Data;

public enum FileStatus
{
    /// <summary>Detected in the inbox, waiting for the quiet period of its group.</summary>
    Waiting,

    /// <summary>Ready for processing.</summary>
    Queued,

    Processing,

    /// <summary>Uncertain match – waits for a decision in the frontend.</summary>
    NeedsReview,

    /// <summary>Neither fingerprint nor text search found the recording in MusicBrainz.</summary>
    NotInMusicBrainz,

    /// <summary>Possible duplicate of a file in the library – waits for a decision in the duplicate view.</summary>
    Duplicate,

    /// <summary>Decision made in the frontend, waiting to be written.</summary>
    Approved,

    Done,
    Error,

    /// <summary>Moved to the trash without filing it.</summary>
    Discarded,
}

public enum DecisionSource
{
    None,
    Automatic,
    Ai,
    Manual,
    ManualTags,

    /// <summary>Tags taken from Spotify or Deezer after confirmation.</summary>
    External,
}

public sealed class InboxGroup
{
    public int Id { get; set; }

    /// <summary>Folder relative to the inbox ("" for loose files in the inbox root).</summary>
    public required string RelativeFolder { get; set; }

    public string? SourceProfile { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>True while files of this group are still being collected.</summary>
    public bool Open { get; set; } = true;

    public List<TrackFile> Files { get; set; } = [];
}

public sealed class TrackFile
{
    public int Id { get; set; }

    public int GroupId { get; set; }
    public InboxGroup? Group { get; set; }

    /// <summary>Path relative to the inbox.</summary>
    public required string RelativePath { get; set; }

    public FileStatus Status { get; set; } = FileStatus.Waiting;

    /// <summary>Short explanation of the current status (e.g. why it needs review).</summary>
    public string? StatusReason { get; set; }

    public string? Codec { get; set; }
    public double? DurationSeconds { get; set; }
    public int? BitrateKbps { get; set; }
    public long FileSize { get; set; }
    public DateTime FileModifiedUtc { get; set; }

    public TagSet? OriginalTags { get; set; }

    /// <summary>Tag fields exactly as found in the original file (all values of multi-value fields).</summary>
    public List<MusicManager.Core.Services.TagField> OriginalFields { get; set; } = [];
    public TagSet? CleanedTags { get; set; }

    /// <summary>"rules" or "ai".</summary>
    public string? CleanupSource { get; set; }

    public List<ReleaseCandidate> Candidates { get; set; } = [];
    public string? SelectedCandidateKey { get; set; }
    public DecisionSource Decision { get; set; }

    public AiDecisionRecord? AiDecision { get; set; }

    /// <summary>AI suggestion for cleaned tags / files not in MusicBrainz.</summary>
    public AiTagSuggestion? AiSuggestion { get; set; }

    /// <summary>Hits from Spotify/Deezer (links in the tags or search) for files not in MusicBrainz.</summary>
    public List<ExternalMatch> ExternalMatches { get; set; } = [];

    /// <summary>Source of the applied external tags ("spotify", "deezer").</summary>
    public string? ExternalSource { get; set; }

    /// <summary>Library files that may be the same recording (or another version of it).</summary>
    public List<DuplicateMatch> Duplicates { get; set; } = [];

    /// <summary>Decision in the duplicate view; null = not decided (check before writing).</summary>
    public DuplicateDecision? DuplicateDecision { get; set; }

    /// <summary>For "keep new": library file (relative) that is replaced; the new file takes its path.</summary>
    public string? ReplaceLibraryPath { get; set; }

    /// <summary>Tags that will be (or were) written.</summary>
    public TagSet? FinalTags { get; set; }

    /// <summary>Target path relative to the library.</summary>
    public string? TargetPath { get; set; }

    /// <summary>AcoustID of the file (from its tags or the fingerprint lookup).</summary>
    public string? AcoustId { get; set; }

    public string? CoverSource { get; set; }

    /// <summary>Uploaded or chosen cover for manual tagging (stored in the work folder).</summary>
    public string? ManualCoverFile { get; set; }

    public bool ManuallyTagged { get; set; }

    /// <summary>Path in the trash where the original file was moved.</summary>
    public string? TrashPath { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    public List<FileEvent> Events { get; set; } = [];

    public string FileName => Path.GetFileName(RelativePath);

    public ReleaseCandidate? SelectedCandidate => Candidates.FirstOrDefault(c => c.Key == SelectedCandidateKey);
}

public sealed class FileEvent
{
    public long Id { get; set; }
    public int TrackFileId { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public EventLevel Level { get; set; }
    public required string Message { get; set; }
}

public enum EventLevel
{
    Info,
    Warning,
    Error,
}

public sealed class AiDecisionRecord
{
    /// <summary>"choose" or "review".</summary>
    public required string Decision { get; set; }

    public string? ReleaseId { get; set; }
    public string? RecordingId { get; set; }
    public double Confidence { get; set; }
    public string? Reasoning { get; set; }
    public bool Accepted { get; set; }
    public string? Model { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AiTagSuggestion
{
    public string? Artist { get; set; }
    public List<string> Artists { get; set; } = [];
    public string? Title { get; set; }
    public string? Album { get; set; }
    public double Confidence { get; set; }
    public string? Notes { get; set; }
    public string? Model { get; set; }
}

public enum ArtistRuleKind
{
    /// <summary>Always split this artist name into individual artists.</summary>
    ForceSplit,

    /// <summary>Replace this artist name with <see cref="ArtistRule.Replacement"/>.</summary>
    ForceReplace,

    /// <summary>Never split this name (band names containing "and", "&amp;" ...).</summary>
    NoSplit,
}

public sealed class ArtistRule
{
    public int Id { get; set; }
    public ArtistRuleKind Kind { get; set; }
    public required string Pattern { get; set; }
    public string? Replacement { get; set; }
    public string? Comment { get; set; }
}

/// <summary>Pre-cleaning rules for files coming from a specific source (subfolder of the inbox).</summary>
public sealed class SourceProfile
{
    public int Id { get; set; }
    public required string Name { get; set; }

    /// <summary>First-level folder in the inbox that identifies the source.</summary>
    public required string FolderName { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>"A,B" → two artists.</summary>
    public bool SplitCommaWithoutSpace { get; set; }

    /// <summary>Tags removed before matching (e.g. show, discnumber).</summary>
    public List<string> DeleteTags { get; set; } = [];

    /// <summary>"3/12" → "3".</summary>
    public bool TrimTrackTotal { get; set; }

    /// <summary>Drops album tags that are playlist names ("Black Music Month Playlist", the playlist in "show").</summary>
    public bool DropPlaylistAlbum { get; set; }
}

public sealed class SettingsEntry
{
    public int Id { get; set; }
    public required string Json { get; set; }
}

/// <summary>A file in the music library (index for duplicate detection).</summary>
public sealed class LibraryEntry
{
    public int Id { get; set; }

    /// <summary>Path relative to the library root.</summary>
    public required string RelativePath { get; set; }

    public long FileSize { get; set; }
    public DateTime FileModifiedUtc { get; set; }

    public string? Codec { get; set; }
    public int? BitrateKbps { get; set; }
    public double? DurationSeconds { get; set; }

    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Date { get; set; }
    public string? MbRecordingId { get; set; }
    public string? MbReleaseId { get; set; }
    public string? AcoustId { get; set; }

    /// <summary>MusicBrainz release track ID (tag MUSICBRAINZ_RELEASETRACKID).</summary>
    public string? MbTrackId { get; set; }

    /// <summary>Original release date (YYYY, YYYY-MM or YYYY-MM-DD).</summary>
    public string? OriginalDate { get; set; }

    /// <summary>MusicBrainz release types ("album", "compilation" …).</summary>
    public List<string> ReleaseTypes { get; set; } = [];

    /// <summary>Compilation flag of the file (cpil / TCMP / COMPILATION).</summary>
    public bool IsCompilation { get; set; }

    public string? Isrc { get; set; }

    /// <summary>
    /// Stable public song ID for the Subsonic API ("tr-…"). Assigned once and carried over when the
    /// file is replaced or moved; never derived again from the tags.
    /// </summary>
    public string? SongId { get; set; }

    /// <summary>When the file entered the library (carried over together with <see cref="SongId"/>).</summary>
    public DateTimeOffset? AddedAt { get; set; }

    /// <summary>Value of the MUSICMANAGER_SOURCE tag (null = not written by this tool).</summary>
    public string? TagSource { get; set; }

    /// <summary>Raw Chromaprint fingerprint (base64 of the uint values), see <see cref="Rules.FingerprintMatcher"/>.</summary>
    public string? Fingerprint { get; set; }

    /// <summary>Fingerprinting failed (e.g. unreadable file); not retried until the file changes.</summary>
    public bool FingerprintFailed { get; set; }

    public List<string> Artists { get; set; } = [];
    public List<string> AlbumArtists { get; set; } = [];
    public List<string> Genres { get; set; } = [];
    public int? TrackNumber { get; set; }
    public int? DiscNumber { get; set; }
    public int? Year { get; set; }
    public string? MbReleaseGroupId { get; set; }
    public List<string> MbArtistIds { get; set; } = [];
    public List<string> MbAlbumArtistIds { get; set; } = [];

    /// <summary>File has an embedded cover.</summary>
    public bool HasCover { get; set; }

    /// <summary>Value of the MUSICMANAGER_RULES tag: rules version the file was tagged with.</summary>
    public string? RulesVersion { get; set; }

    /// <summary>Version of the index logic that read this entry (older entries are re-read).</summary>
    public int IndexVersion { get; set; }

    public DateTimeOffset IndexedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum DuplicateKind
{
    /// <summary>Same recording – not filed automatically.</summary>
    Duplicate,

    /// <summary>Probably another version (remix, live, remaster …) – only shown as a hint.</summary>
    OtherVersion,
}

public enum DuplicateDecision
{
    KeepNew,
    KeepExisting,
    KeepBoth,
}

/// <summary>A library file that matches a new file, with a snapshot of its data for the comparison view.</summary>
public sealed class DuplicateMatch
{
    public int LibraryEntryId { get; set; }
    public required string RelativePath { get; set; }
    public DuplicateKind Kind { get; set; }
    public required string Reason { get; set; }
    public double? Similarity { get; set; }

    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? Date { get; set; }
    public string? Codec { get; set; }
    public int? BitrateKbps { get; set; }
    public double? DurationSeconds { get; set; }
    public long FileSize { get; set; }
    public string? MbRecordingId { get; set; }
    public string? MbReleaseId { get; set; }
    public string? TagSource { get; set; }
}

/// <summary>A group of library files marked as "not a duplicate" in the library check.</summary>
public sealed class DismissedDuplicateGroup
{
    public int Id { get; set; }

    /// <summary>Sorted relative paths of the group, joined with '|'.</summary>
    public required string Key { get; set; }

    public DateTimeOffset DismissedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Cached artist image (or the knowledge that none was found).</summary>
public sealed class ArtistImage
{
    public int Id { get; set; }

    /// <summary>Normalized artist name (lookup key).</summary>
    public required string Key { get; set; }

    public required string Name { get; set; }
    public string? MbArtistId { get; set; }

    /// <summary>"wikidata", "deezer", "spotify" – null if nothing was found.</summary>
    public string? Source { get; set; }

    /// <summary>Image file in the data folder (relative to it).</summary>
    public string? FilePath { get; set; }

    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A browser (usually the installed PWA on the phone) that receives Web Push notifications.</summary>
public sealed class PushSubscription
{
    public int Id { get; set; }

    /// <summary>Push service URL of the browser (unique per subscription).</summary>
    public required string Endpoint { get; set; }

    /// <summary>Browser's public ECDH key (base64url).</summary>
    public required string P256dh { get; set; }

    /// <summary>Authentication secret (base64url).</summary>
    public required string Auth { get; set; }

    /// <summary>Short description of the device for the settings page.</summary>
    public string? Device { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSentAt { get; set; }
}

/// <summary>A starred song, album or artist (Subsonic API), keyed by its public catalog ID.</summary>
public sealed class Star
{
    /// <summary>Public ID ("tr-…", "al-…", "ar-…"). Kept when the item is not in the library.</summary>
    public required string ItemId { get; set; }

    public DateTimeOffset StarredAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>User rating (1..5) of a song, album or artist, keyed by its public catalog ID.</summary>
public sealed class Rating
{
    public required string ItemId { get; set; }
    public int Value { get; set; }
}

/// <summary>Play count and last play of a song, keyed by its public song ID.</summary>
public sealed class PlayStat
{
    public required string SongId { get; set; }
    public int PlayCount { get; set; }
    public DateTimeOffset? LastPlayed { get; set; }
}

/// <summary>Playlist of the Subsonic API (public ID "pl-{Id}").</summary>
public sealed class Playlist
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Comment { get; set; }
    public bool Public { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Entries; order by <see cref="PlaylistEntry.Position"/>.</summary>
    public List<PlaylistEntry> Entries { get; set; } = [];
}

/// <summary>One song of a playlist. Entries whose song is not in the library are kept (and hidden).</summary>
public sealed class PlaylistEntry
{
    public int Id { get; set; }
    public int PlaylistId { get; set; }
    public int Position { get; set; }
    public required string SongId { get; set; }
}
