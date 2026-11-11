using Microsoft.EntityFrameworkCore;
using MusicManager.Core.Data;
using MusicManager.Core.Pipeline;

namespace MusicManager.Core.Catalog;

/// <summary>Changes of <see cref="PlaylistStore.UpdateAsync"/>; null leaves a field as it is.</summary>
public sealed record PlaylistChanges(
    string? Name = null,
    string? Comment = null,
    bool? Public = null,
    IReadOnlyList<string>? SongIdsToAdd = null,
    IReadOnlyList<int>? IndicesToRemove = null);

/// <summary>
/// Playlists of the Subsonic API. Entries are song IDs; entries whose song is not in the library are
/// kept but hidden from clients, so positions the client sends refer to the visible entries only
/// (<c>inLibrary</c> decides what is visible).
/// </summary>
public sealed class PlaylistStore(IDbContextFactory<AppDbContext> dbFactory, Storage storage)
{
    /// <summary>Raised after a playlist was created, changed or deleted.</summary>
    public event Action? Changed;

    public async Task<List<Playlist>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var playlists = await db.Playlists.AsNoTracking().Include(p => p.Entries).OrderBy(p => p.Name).ThenBy(p => p.Id).ToListAsync(ct);
        foreach (var p in playlists) Sort(p);
        return playlists;
    }

    public async Task<Playlist?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var playlist = await db.Playlists.AsNoTracking().Include(p => p.Entries).FirstOrDefaultAsync(p => p.Id == id, ct);
        return playlist == null ? null : Sort(playlist);
    }

    public async Task<Playlist> CreateAsync(string name, IReadOnlyList<string> songIds, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var playlist = new Playlist { Name = name };
        playlist.Entries.AddRange(songIds.Select((id, i) => new PlaylistEntry { SongId = id, Position = i }));
        db.Playlists.Add(playlist);
        await db.SaveChangesAsync(ct);
        Changed?.Invoke();
        return playlist;
    }

    /// <summary>
    /// Replaces the visible songs (createPlaylist with an ID). Hidden entries are kept and move behind
    /// the new songs, since the client cannot say where they belong.
    /// </summary>
    public Task<Playlist?> ReplaceSongsAsync(int id, string? name, IReadOnlyList<string> songIds, Func<string, bool> inLibrary,
        CancellationToken ct = default) => ModifyAsync(id, playlist =>
    {
        if (!string.IsNullOrWhiteSpace(name)) playlist.Name = name;
        playlist.Entries.RemoveAll(e => inLibrary(e.SongId));
        playlist.Entries.InsertRange(0, songIds.Select(s => new PlaylistEntry { SongId = s }));
    }, ct);

    /// <summary>updatePlaylist: removes visible entries by index (before adding), then appends the new songs.</summary>
    public Task<Playlist?> UpdateAsync(int id, PlaylistChanges changes, Func<string, bool> inLibrary, CancellationToken ct = default) =>
        ModifyAsync(id, playlist =>
        {
            if (!string.IsNullOrWhiteSpace(changes.Name)) playlist.Name = changes.Name;
            if (changes.Comment != null) playlist.Comment = changes.Comment.Length == 0 ? null : changes.Comment;
            if (changes.Public != null) playlist.Public = changes.Public.Value;

            if (changes.IndicesToRemove is { Count: > 0 } indices)
            {
                var visible = playlist.Entries.Where(e => inLibrary(e.SongId)).ToList();
                var remove = indices.Where(i => i >= 0 && i < visible.Count).Select(i => visible[i]).ToHashSet();
                playlist.Entries.RemoveAll(remove.Contains);
            }

            if (changes.SongIdsToAdd is { Count: > 0 } add)
                playlist.Entries.AddRange(add.Select(s => new PlaylistEntry { SongId = s }));
        }, ct);

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var playlist = await db.Playlists.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (playlist == null) return false;
        db.Playlists.Remove(playlist);
        await db.SaveChangesAsync(ct);
        // Otherwise the file sync would take the left-over file for a new playlist.
        if (playlist.FileName != null) File.Delete(storage.LibraryPath(Path.Combine(PlaylistFile.Folder, playlist.FileName)));
        Changed?.Invoke();
        return true;
    }

    private async Task<Playlist?> ModifyAsync(int id, Action<Playlist> change, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var playlist = await db.Playlists.Include(p => p.Entries).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (playlist == null) return null;
        Sort(playlist);
        change(playlist);
        for (var i = 0; i < playlist.Entries.Count; i++) playlist.Entries[i].Position = i;
        playlist.ChangedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        Changed?.Invoke();
        return playlist;
    }

    private static Playlist Sort(Playlist p)
    {
        p.Entries.Sort((a, b) => a.Position != b.Position ? a.Position.CompareTo(b.Position) : a.Id.CompareTo(b.Id));
        return p;
    }
}
