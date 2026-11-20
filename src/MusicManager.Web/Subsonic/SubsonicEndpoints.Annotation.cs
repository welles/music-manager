using MusicManager.Core.Catalog;
using MusicManager.Core.Data;

namespace MusicManager.Web.Subsonic;

// Annotations: star, unstar, setRating, scrobble, getStarred2.
public static partial class SubsonicEndpoints
{
    private static void MapAnnotation()
    {
        Add("star", async r =>
        {
            var added = await r.Service<UserDataStore>().StarAsync(StarIds(r), r.Aborted);
            // Not cancelled with the request: the star is already set, a repeated star would not record it again.
            await r.Service<PlayHistoryService>().RecordAsync(PlayEventKind.Starred, added, DateTimeOffset.UtcNow, r.Get("c"), CancellationToken.None);
            return Ok();
        });

        Add("unstar", async r =>
        {
            var removed = await r.Service<UserDataStore>().UnstarAsync(StarIds(r), r.Aborted);
            var now = DateTimeOffset.UtcNow;
            // Starred and unstarred again right away: the player has no other way to flag a song (issue #18).
            // Not cancelled with the request: the star is already gone, a repeated unstar could not report it again.
            var reported = await r.Service<SongReportService>().ReportQuickUnstarsAsync(removed, now, CancellationToken.None);
            var history = r.Service<PlayHistoryService>();
            await history.RecordAsync(PlayEventKind.Reported, reported, now, r.Get("c"), CancellationToken.None);
            await history.RecordAsync(PlayEventKind.Unstarred, removed.Select(s => s.ItemId).Except(reported, StringComparer.Ordinal), now,
                r.Get("c"), CancellationToken.None);
            return Ok();
        });

        Add("setRating", async r =>
        {
            var id = ItemId(r.Require("id"));
            var rating = r.RequireInt("rating");
            if (rating is < 0 or > 5) throw new SubsonicException(SubsonicErrors.Generic, $"Invalid rating: {rating}");
            await r.Service<UserDataStore>().SetRatingAsync(id, rating, r.Aborted);
            return Ok();
        });

        Add("scrobble", async r =>
        {
            var ids = r.GetAll("id").Where(i => i.Length > 0).ToList();
            if (ids.Count == 0) throw SubsonicException.Missing("id");
            foreach (var id in ids)
            {
                if (!CatalogIds.IsSong(id)) throw SubsonicException.NotFound("Song");
            }

            var times = r.GetLongs("time");
            var now = DateTimeOffset.UtcNow;
            var history = r.Service<PlayHistoryService>();
            // "Now playing": a start in the history; one without a play shortly after is a skip (issue #20).
            if (r.GetBool("submission") == false)
            {
                await history.RecordAsync(PlayEventKind.Started, ids, now, r.Get("c"), r.Aborted);
                return Ok();
            }

            var plays = ids.Select((id, i) =>
            {
                // One time per ID; a single time applies to all of them.
                var ms = i < times.Count ? times[i] : times.Count == 1 ? times[0] : (long?)null;
                return (id, ms is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(Math.Min(ms.Value, now.ToUnixTimeMilliseconds())) : now);
            }).ToList();
            await r.Service<UserDataStore>().ScrobbleAsync(plays, r.Aborted);
            // Counted already: a repeated scrobble would count it twice, so the history is written in any case.
            await history.RecordAsync(PlayEventKind.Played, plays, r.Get("c"), CancellationToken.None);
            return Ok();
        });

        Add("getStarred2", async r =>
        {
            var catalog = await Catalog(r);
            var user = await UserData(r);
            var mapper = new SubsonicMapper(user);
            var starred = user.StarredIds.ToList();
            return Ok("starred2", new
            {
                artist = starred.Select(catalog.Artist).OfType<CatalogArtist>().Select(a => mapper.Artist(a)).ToList(),
                album = starred.Select(catalog.Album).OfType<CatalogAlbum>().Select(a => mapper.Album(a)).ToList(),
                song = starred.Select(catalog.Song).OfType<CatalogSong>().Select(mapper.Song).ToList(),
            });
        });
    }

    /// <summary>
    /// IDs of star/unstar: <c>id</c> (songs, but album and artist IDs are recognized by their prefix),
    /// <c>albumId</c> and <c>artistId</c>, each repeatable. Items need not be in the library.
    /// </summary>
    private static List<string> StarIds(SubsonicRequest r)
    {
        var ids = r.GetAll("id").Concat(r.GetAll("albumId")).Concat(r.GetAll("artistId")).Where(i => i.Length > 0).ToList();
        if (ids.Count == 0) throw SubsonicException.Missing("id");
        return ids.Select(ItemId).ToList();
    }

    private static string ItemId(string id) => CatalogIds.IsItem(id) ? id : throw SubsonicException.NotFound("Item");
}
