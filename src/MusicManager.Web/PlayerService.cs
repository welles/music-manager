using MusicManager.Core.Pipeline;

namespace MusicManager.Web;

/// <summary>Current track of the player bar (one per browser session).</summary>
public sealed class PlayerService
{
    public LibraryTrack? Current { get; private set; }

    public event Action? Changed;

    public void Play(LibraryTrack track)
    {
        Current = track;
        Changed?.Invoke();
    }

    public void Stop()
    {
        Current = null;
        Changed?.Invoke();
    }
}

internal static class TextExtensions
{
    public static string? NullIfEmptyText(this string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
