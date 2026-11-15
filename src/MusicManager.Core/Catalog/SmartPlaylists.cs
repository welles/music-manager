namespace MusicManager.Core.Catalog;

/// <summary>
/// Playlists that Music Manager keeps by itself: worked out from the library and the user data on every request, read-only
/// for clients. Their IDs ("pl-smart-…") never clash with stored playlists ("pl-{number}").
/// </summary>
/// <param name="Shuffled">
/// Mixed anew on every request: a player takes the order when playback starts and keeps it, so the list is new each time
/// the playlist is started, never in between two songs.
/// </param>
public sealed record SmartPlaylist(string Id, string Name, string Comment, bool Shuffled);

public static class SmartPlaylists
{
    public const string Prefix = CatalogIds.PlaylistPrefix + "smart-";

    public static readonly SmartPlaylist All =
        new($"{Prefix}alle", "Alle", "Alle Lieder der Bibliothek, bei jedem Start neu gemischt.", Shuffled: true);

    public static readonly SmartPlaylist NeverPlayed =
        new($"{Prefix}nie-gespielt", "Noch nie gespielt", "Lieder ohne eine einzige Wiedergabe, bei jedem Start neu gemischt.", Shuffled: true);

    public static readonly SmartPlaylist Favorites =
        new($"{Prefix}favoriten", "Favoriten", "Favorisierte Lieder, zuletzt favorisierte zuerst.", Shuffled: false);

    public static readonly IReadOnlyList<SmartPlaylist> Every = [All, NeverPlayed, Favorites];

    public static SmartPlaylist? Find(string? id) => Every.FirstOrDefault(p => p.Id == id);

    /// <summary>The songs of <paramref name="playlist"/> in their order (shuffled ones in a new order each time).</summary>
    public static List<CatalogSong> Songs(SmartPlaylist playlist, CatalogSnapshot catalog, UserDataSnapshot user, Random? random = null)
    {
        List<CatalogSong> songs;
        if (playlist == Favorites)
        {
            songs = user.StarredIds.Select(catalog.Song).OfType<CatalogSong>().ToList();
        }
        else
        {
            // Played = a play the client reported as such (scrobble); a song that was only started and skipped counts as unplayed.
            songs = playlist == NeverPlayed
                ? catalog.Songs.Where(s => user.Plays(s.Id).Count == 0).ToList()
                : catalog.Songs.ToList();
        }

        if (playlist.Shuffled) (random ?? Random.Shared).Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(songs));
        return songs;
    }

    /// <summary>
    /// When the content last changed, for clients that reload a playlist only when this moves: always now. The shuffled
    /// ones have a new order each time; for favorites no date says when one was removed (an older star taken back, a
    /// song gone from the library), so "now" is the only value that never misses a change. The lists are cheap to load.
    /// </summary>
    public static DateTimeOffset Changed(DateTimeOffset now) => now;

    /// <summary>A fixed creation date: they have always existed as far as clients are concerned.</summary>
    public static readonly DateTimeOffset Created = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
}
