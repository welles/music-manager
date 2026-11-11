using System.Globalization;

namespace MusicManager.Web;

/// <summary>
/// Commit the running image was built from (set by the Dockerfile from the CI build), so the app bar
/// shows which version is deployed. Without it (local build) the version is "dev".
/// </summary>
public static class AppVersion
{
    /// <summary>Short commit hash in the form of the image tag ("sha-4d54262"), or "dev".</summary>
    public static string Label { get; } = Environment.GetEnvironmentVariable("MUSICMANAGER_COMMIT") is { Length: >= 7 } sha
        ? $"sha-{sha[..7]}"
        : "dev";

    /// <summary>Full commit hash, if known.</summary>
    public static string? Commit { get; } = Environment.GetEnvironmentVariable("MUSICMANAGER_COMMIT") is { Length: > 0 } c ? c : null;

    /// <summary>When the commit was made.</summary>
    public static DateTimeOffset? CommitTime { get; } =
        DateTimeOffset.TryParse(Environment.GetEnvironmentVariable("MUSICMANAGER_COMMIT_TIME"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t
            : null;

    /// <summary>"sha-4d54262 · 11.11.2026 21:04" (local time of the server), or "dev".</summary>
    public static string Display => CommitTime is { } time ? $"{Label} · {time.ToLocalTime():dd.MM.yyyy HH:mm}" : Label;
}
