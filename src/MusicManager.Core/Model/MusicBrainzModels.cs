namespace MusicManager.Core.Model;

/// <summary>One part of a MusicBrainz artist credit.</summary>
public sealed record CreditPart(string ArtistId, string Name, string CreditedName, string JoinPhrase);

/// <summary>A recording that may match a file, including the releases it appears on.</summary>
public sealed class RecordingMatch
{
    public required string RecordingId { get; init; }
    public required string Title { get; init; }
    public List<CreditPart> Credits { get; init; } = [];
    public double? LengthSeconds { get; init; }

    /// <summary>Score of the lookup (AcoustID 0..1 or search score / 100).</summary>
    public double MatchScore { get; init; }

    /// <summary>"acoustid", "search" or "manual".</summary>
    public string Source { get; init; } = "acoustid";

    public List<ReleaseAppearance> Releases { get; init; } = [];

    /// <summary>Video recordings (music videos) never match an audio file.</summary>
    public bool IsVideo { get; init; }

    public string ArtistString => CreditFormatter.Join(Credits);
}

/// <summary>Result of a MusicBrainz recording text search.</summary>
public sealed record RecordingSearchHit(string RecordingId, int Score, string Title, double? LengthSeconds, bool IsVideo, int ReleaseCount);

/// <summary>A release the recording appears on, with the data needed for scoring.</summary>
public sealed class ReleaseAppearance
{
    public required string ReleaseId { get; init; }
    public required string Title { get; init; }
    public string? ReleaseGroupId { get; init; }
    public string? PrimaryType { get; init; }
    public List<string> SecondaryTypes { get; init; } = [];
    public string? Status { get; init; }
    public string? Country { get; init; }
    public string? Date { get; init; }
    public List<string> Formats { get; init; } = [];
    public string? ArtistString { get; init; }
    public int? TrackPosition { get; init; }
    public int? TrackCount { get; init; }
    public int? MediumPosition { get; init; }

    public int? Year => Date is { Length: >= 4 } && int.TryParse(Date[..4], out var y) ? y : null;
}

/// <summary>A scored release candidate for one file.</summary>
public sealed class ReleaseCandidate
{
    public required string RecordingId { get; init; }
    public required string RecordingTitle { get; init; }
    public required string RecordingArtist { get; init; }
    public double? RecordingLengthSeconds { get; init; }
    public double MatchScore { get; init; }
    public string MatchSource { get; init; } = "acoustid";
    public required ReleaseAppearance Release { get; init; }

    public double Score { get; set; }

    /// <summary>Normalized score 0..1 (Score / maximum achievable positive score).</summary>
    public double Confidence { get; set; }

    /// <summary>Score parts for display ("Album-Ähnlichkeit" -> 22.5).</summary>
    public Dictionary<string, double> Breakdown { get; set; } = [];

    public string Key => $"{RecordingId}/{Release.ReleaseId}";
}

/// <summary>Full release data used to build the final tags.</summary>
public sealed class ReleaseDetail
{
    public required string ReleaseId { get; init; }
    public required string Title { get; init; }
    public string? ReleaseGroupId { get; init; }
    public string? PrimaryType { get; init; }
    public List<string> SecondaryTypes { get; init; } = [];
    public string? Status { get; init; }
    public string? Country { get; init; }
    public string? Date { get; init; }
    public string? OriginalDate { get; init; }
    public string? Label { get; init; }
    public bool HasFrontCover { get; init; }
    public List<CreditPart> Credits { get; init; } = [];
    public List<MediumDetail> Media { get; init; } = [];

    /// <summary>Genre name -> vote count (release and release group).</summary>
    public Dictionary<string, int> Genres { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class MediumDetail
{
    public int Position { get; init; }
    public string? Format { get; init; }
    public List<TrackDetail> Tracks { get; init; } = [];
}

public sealed class TrackDetail
{
    public required string TrackId { get; init; }
    public required string RecordingId { get; init; }
    public int Position { get; init; }
    public required string Title { get; init; }
    public double? LengthSeconds { get; init; }
    public List<CreditPart> Credits { get; init; } = [];
    public List<string> Isrcs { get; init; } = [];

    /// <summary>Remixers from the recording's "remixer" relationships.</summary>
    public List<CreditPart> Remixers { get; init; } = [];
}

/// <summary>Artist data needed for name translation and genres.</summary>
public sealed class ArtistInfo
{
    public required string ArtistId { get; init; }
    public required string Name { get; init; }

    /// <summary>locale -> primary alias name.</summary>
    public Dictionary<string, string> LocaleAliases { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, int> Genres { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class CreditFormatter
{
    private static readonly string[] SpacedJoinWords = ["&", "x", "×", "+", "/", "feat.", "ft.", "feat", "featuring", "with", "and", "vs.", "vs", "und"];

    public static string Join(IEnumerable<CreditPart> credits, Func<CreditPart, string>? name = null)
    {
        name ??= c => c.CreditedName;
        return string.Concat(credits.Select(c => name(c) + NormalizeJoinPhrase(c.JoinPhrase))).Trim();
    }

    /// <summary>
    /// Fixes sloppy join phrases from imported data: "," → ", ", "&amp;" → " &amp; ".
    /// </summary>
    public static string NormalizeJoinPhrase(string joinPhrase)
    {
        var t = joinPhrase.Trim();
        if (t.Length == 0) return joinPhrase;
        if (t == ",") return ", ";
        return SpacedJoinWords.Contains(t, StringComparer.OrdinalIgnoreCase) ? $" {t} " : joinPhrase;
    }
}
