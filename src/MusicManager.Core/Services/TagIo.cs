using MusicManager.Core.Model;
using MusicManager.Core.Rules;
using TagLib;
using TagLib.Mpeg4;
using File = TagLib.File;
using Tag = TagLib.Tag;

namespace MusicManager.Core.Services;

/// <summary>Reading tags from any input format and writing the final tags into M4A files (TagLib#).</summary>
public static class TagIo
{
    private const string ITunesMean = "com.apple.iTunes";
    private static readonly ByteVector DayBox = new([0xA9, (byte)'d', (byte)'a', (byte)'y']);
    private static readonly ByteVector TvShowBox = ByteVector.FromString("tvsh", StringType.Latin1);

    /// <summary>Marker that identifies files written by this tool and the source of their tags.</summary>
    public const string SourceMarker = "MUSICMANAGER_SOURCE";

    public static TagSet Read(string path)
    {
        var t = new TagSet();
        try
        {
            using var file = File.Create(path);
            var tag = file.Tag;
            t.Title = tag.Title.NullIfEmpty();
            t.Artist = tag.JoinedPerformers.NullIfEmpty();
            if (tag.Performers.Length > 1)
                t.Artists = tag.Performers.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            t.Album = tag.Album.NullIfEmpty();
            t.AlbumArtist = tag.JoinedAlbumArtists.NullIfEmpty();
            if (tag.AlbumArtists.Length > 1)
                t.AlbumArtists = tag.AlbumArtists.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            t.TrackNumber = tag.Track > 0 ? (int)tag.Track : null;
            t.TrackCount = tag.TrackCount > 0 ? (int)tag.TrackCount : null;
            t.DiscNumber = tag.Disc > 0 ? (int)tag.Disc : null;
            t.DiscCount = tag.DiscCount > 0 ? (int)tag.DiscCount : null;
            t.Date = tag.Year > 0 ? tag.Year.ToString() : null;
            t.Genres = tag.Genres.Where(g => !string.IsNullOrWhiteSpace(g)).ToList();
            t.Remixer = tag.RemixedBy.NullIfEmpty();
            t.Isrc = tag.ISRC.NullIfEmpty();
            t.MbRecordingId = tag.MusicBrainzTrackId.NullIfEmpty();
            t.MbReleaseId = tag.MusicBrainzReleaseId.NullIfEmpty();
            t.MbReleaseGroupId = tag.MusicBrainzReleaseGroupId.NullIfEmpty();

            ReadExtra(file, t);
            t.AcoustId = (t.Extra.GetValueOrDefault("Acoustid Id") ?? t.Extra.GetValueOrDefault("acoustid_id")).NullIfEmpty();
        }
        catch (Exception ex) when (ex is CorruptFileException or UnsupportedFormatException)
        {
            t.Extra["_error"] = ex.Message;
        }

        return t;
    }

    private static void ReadExtra(File file, TagSet t)
    {
        if (file.GetTag(TagTypes.Apple) is AppleTag apple)
        {
            var show = apple.GetText(TvShowBox);
            if (show.Length > 0) t.Extra["show"] = string.Join("; ", show);
            var marker = apple.GetDashBox(ITunesMean, SourceMarker);
            if (!string.IsNullOrEmpty(marker)) t.Extra[SourceMarker] = marker;
            var acoustId = apple.GetDashBox(ITunesMean, "Acoustid Id");
            if (!string.IsNullOrEmpty(acoustId)) t.Extra["Acoustid Id"] = acoustId;
        }

        if (file.GetTag(TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
        {
            foreach (var frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>())
            {
                if (!string.IsNullOrWhiteSpace(frame.Description) && frame.Text.Length > 0)
                    t.Extra[frame.Description] = string.Join("; ", frame.Text);
            }

            foreach (var frame in id3.GetFrames<TagLib.Id3v2.TextInformationFrame>("TRCK"))
                t.Extra["tracknumber"] = string.Join("/", frame.Text);
        }

        if (file.GetTag(TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
        {
            foreach (var field in xiph)
            {
                var values = xiph.GetField(field);
                if (values.Length > 0) t.Extra.TryAdd(field.ToLowerInvariant(), string.Join("; ", values));
            }
        }
    }

    public static byte[]? ReadEmbeddedCover(string path)
    {
        try
        {
            using var file = File.Create(path);
            var pic = file.Tag.Pictures.FirstOrDefault(p => p.Type == PictureType.FrontCover) ?? file.Tag.Pictures.FirstOrDefault();
            return pic?.Data.Data;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the final tags into an M4A file. ARTIST/ALBUMARTIST get the display strings,
    /// ARTISTS/ALBUMARTISTS the individual artists (read by Navidrome as multi-artist tags).
    /// </summary>
    public static void WriteM4a(string path, TagSet t, byte[]? cover, string source)
    {
        using var file = File.Create(path, "audio/mp4", ReadStyle.Average);
        var apple = (AppleTag)file.GetTag(TagTypes.Apple, true);
        apple.Clear();

        Tag tag = apple;
        tag.Title = t.Title;
        tag.Performers = t.Artist == null ? [] : [t.Artist];
        tag.AlbumArtists = t.AlbumArtist == null ? [] : [t.AlbumArtist];
        tag.Album = t.Album;
        tag.Track = (uint)(t.TrackNumber ?? 0);
        tag.TrackCount = (uint)(t.TrackCount ?? 0);
        tag.Disc = (uint)(t.DiscNumber ?? 0);
        tag.DiscCount = (uint)(t.DiscCount ?? 0);
        tag.Genres = [.. t.Genres];
        if (t.Date != null)
            apple.SetText(DayBox, t.Date);

        tag.MusicBrainzTrackId = t.MbRecordingId;
        tag.MusicBrainzReleaseId = t.MbReleaseId;
        tag.MusicBrainzReleaseGroupId = t.MbReleaseGroupId;
        tag.MusicBrainzArtistId = t.MbArtistIds.Count > 0 ? string.Join("/", t.MbArtistIds) : null;
        tag.MusicBrainzReleaseArtistId = t.MbAlbumArtistIds.Count > 0 ? string.Join("/", t.MbAlbumArtistIds) : null;
        tag.MusicBrainzReleaseStatus = t.ReleaseStatus;
        tag.MusicBrainzReleaseType = t.ReleaseType;
        tag.MusicBrainzReleaseCountry = t.ReleaseCountry;

        SetDash(apple, "ARTISTS", t.Artists);
        SetDash(apple, "ALBUMARTISTS", t.AlbumArtists);
        SetDash(apple, "REMIXER", t.Remixer);
        SetDash(apple, "ISRC", t.Isrc);
        SetDash(apple, "LABEL", t.Label);
        SetDash(apple, "ORIGINALDATE", t.OriginalDate);
        SetDash(apple, "MusicBrainz Release Track Id", t.MbTrackId);
        SetDash(apple, "Acoustid Id", t.AcoustId);
        SetDash(apple, SourceMarker, source);

        if (cover != null)
        {
            tag.Pictures =
            [
                new Picture(new ByteVector(cover)) { Type = PictureType.FrontCover, MimeType = "image/jpeg" },
            ];
        }

        file.Save();
    }

    private static void SetDash(AppleTag apple, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            apple.SetDashBox(ITunesMean, name, value);
    }

    private static void SetDash(AppleTag apple, string name, List<string> values)
    {
        if (values.Count > 0)
            apple.SetDashBoxes(ITunesMean, name, [.. values]);
    }
}
