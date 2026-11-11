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
    private const string ITunesMean = TagFields.ITunesMean;
    private static readonly ByteVector DayBox = new([0xA9, (byte)'d', (byte)'a', (byte)'y']);
    private static readonly ByteVector TvShowBox = ByteVector.FromString("tvsh", StringType.Latin1);

    /// <summary>Marker that identifies files written by this tool and the source of their tags.</summary>
    public const string SourceMarker = "MUSICMANAGER_SOURCE";

    /// <summary>Rules version the file was tagged with, see <see cref="Pipeline.SettingsService.GetRulesVersionAsync"/>.</summary>
    public const string RulesMarker = "MUSICMANAGER_RULES";

    public static TagSet Read(string path)
    {
        var t = new TagSet();
        try
        {
            using var file = File.Create(path);
            var tag = file.Tag;
            t.Title = tag.Title.NullIfEmpty();
            // Several values (multi-value ARTIST): display as "A & B", the list goes to Artists.
            t.Artist = string.Join(" & ", tag.Performers.Where(p => !string.IsNullOrWhiteSpace(p))).NullIfEmpty();
            if (tag.Performers.Length > 1)
                t.Artists = tag.Performers.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            t.Album = tag.Album.NullIfEmpty();
            t.AlbumArtist = string.Join(" & ", tag.AlbumArtists.Where(p => !string.IsNullOrWhiteSpace(p))).NullIfEmpty();
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

            t.Date = FullDate(file) ?? t.Date;
            ReadExtra(file, t);
            ReadWrittenFields(path, t);
            t.AcoustId = (t.Extra.GetValueOrDefault("Acoustid Id") ?? t.Extra.GetValueOrDefault("acoustid_id")).NullIfEmpty();
        }
        catch (Exception ex) when (ex is CorruptFileException or UnsupportedFormatException)
        {
            t.Extra["_error"] = ex.Message;
        }

        return t;
    }

    /// <summary>Full release date (YYYY-MM-DD) where the format stores it; TagLib's Year only has the year.</summary>
    private static string? FullDate(File file)
    {
        string? value = null;
        if (file.GetTag(TagTypes.Apple) is AppleTag apple)
            value = apple.GetText(DayBox).FirstOrDefault();
        else if (file.GetTag(TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
            value = id3.GetFrames<TagLib.Id3v2.TextInformationFrame>("TDRC").FirstOrDefault()?.Text.FirstOrDefault();
        else if (file.GetTag(TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
            value = xiph.GetFirstField("DATE");
        return value is { Length: >= 4 } && char.IsDigit(value[0]) ? value.Trim() : null;
    }

    /// <summary>
    /// Fields the writer produces but TagLib's generic properties do not cover (freeform atoms,
    /// Vorbis comments, multi-value IDs). Without them, re-tagging or editing a file would drop them.
    /// </summary>
    private static void ReadWrittenFields(string path, TagSet t)
    {
        var fields = TagFields.Read(path);
        string? One(params string[] names) => TagFields.Get(fields, names).FirstOrDefault();
        List<string> Many(params string[] names) => TagFields.Get(fields, names);

        t.OriginalDate ??= One("ORIGINALDATE");
        t.Label ??= One("LABEL");
        t.Isrc ??= One("ISRC");
        t.Remixer ??= One("REMIXER");
        t.MbTrackId ??= One("MusicBrainz Release Track Id", "MUSICBRAINZ_RELEASETRACKID");
        t.ReleaseStatus ??= One("MusicBrainz Album Status", "RELEASESTATUS");
        t.ReleaseCountry ??= One("MusicBrainz Album Release Country", "RELEASECOUNTRY");
        var types = Many("MusicBrainz Album Type", "RELEASETYPE");
        if (types.Count > 0) t.ReleaseType ??= string.Join("; ", types);
        if (t.MbArtistIds.Count == 0) t.MbArtistIds = Many("MusicBrainz Artist Id", "MUSICBRAINZ_ARTISTID");
        if (t.MbAlbumArtistIds.Count == 0) t.MbAlbumArtistIds = Many("MusicBrainz Album Artist Id", "MUSICBRAINZ_ALBUMARTISTID");
        var albumArtists = Many("ALBUMARTIST", "ALBUM ARTIST");
        if (albumArtists.Count > 1 && t.AlbumArtists.Count == 0) t.AlbumArtists = albumArtists;
        var artists = Many("ARTIST");
        if (artists.Count > 1 && t.Artists.Count == 0) t.Artists = artists;
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

    /// <summary>Tags plus audio properties for the library index (one file open).</summary>
    public static (TagSet Tags, double? Duration, int? BitrateKbps, string? Codec) ReadForIndex(string path)
    {
        var tags = Read(path);
        try
        {
            using var file = File.Create(path, ReadStyle.Average);
            var p = file.Properties;
            return (tags, p?.Duration.TotalSeconds > 0 ? p.Duration.TotalSeconds : null, p?.AudioBitrate > 0 ? p.AudioBitrate : null,
                ShortCodec(p?.Codecs.OfType<ICodec>().FirstOrDefault()?.Description));
        }
        catch (Exception ex) when (ex is CorruptFileException or UnsupportedFormatException)
        {
            return (tags, null, null, null);
        }
    }

    /// <summary>Same short names as ffprobe ("aac", "mp3", "flac" …) so both sides of a comparison match.</summary>
    public static string? ShortCodec(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var d = description.ToLowerInvariant();
        return d switch
        {
            _ when d.Contains("aac") || d.Contains("mp4a") => "aac",
            _ when d.Contains("layer 3") => "mp3",
            _ when d.Contains("alac") || d.Contains("apple lossless") => "alac",
            _ when d.Contains("flac") => "flac",
            _ when d.Contains("vorbis") => "vorbis",
            _ when d.Contains("opus") => "opus",
            _ when d.Contains("pcm") || d.Contains("wave") => "pcm",
            _ when d.Contains("wma") || d.Contains("windows media") => "wma",
            _ => description,
        };
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

    private static readonly Dictionary<string, ByteVector> TextAtoms = new()
    {
        ["TITLE"] = Atom("©nam"), ["ARTIST"] = Atom("©ART"), ["ALBUM"] = Atom("©alb"), ["ALBUMARTIST"] = Atom("aART"),
        ["DATE"] = Atom("©day"), ["GENRE"] = Atom("©gen"),
    };

    private static ByteVector Atom(string name) =>
        name[0] == '©' ? new ByteVector([0xA9, .. System.Text.Encoding.ASCII.GetBytes(name[1..])]) : ByteVector.FromString(name, StringType.Latin1);

    /// <summary>
    /// Writes the final tags into an M4A file. The field list comes from
    /// <see cref="TagFields.ForFinalTags"/>, so the before/after view shows exactly what is written.
    /// Multi-value fields (ARTISTS, GENRE, artist IDs …) are written as one atom with several values.
    /// </summary>
    public static void WriteM4a(string path, TagSet t, byte[]? cover, string source, string? rulesVersion = null)
    {
        using var file = File.Create(path, "audio/mp4", ReadStyle.Average);
        var apple = (AppleTag)file.GetTag(TagTypes.Apple, true);
        apple.Clear();
        Tag tag = apple;

        foreach (var field in TagFields.ForFinalTags(t, cover != null ? "Front-Cover" : null, source, rulesVersion))
        {
            switch (field.Name)
            {
                case "TRACKNUMBER":
                    tag.Track = (uint)(t.TrackNumber ?? 0);
                    tag.TrackCount = (uint)(t.TrackCount ?? 0);
                    break;
                case "DISCNUMBER":
                    tag.Disc = (uint)(t.DiscNumber ?? 0);
                    tag.DiscCount = (uint)(t.DiscCount ?? 0);
                    break;
                case "COVER":
                    tag.Pictures = [new Picture(new ByteVector(cover!)) { Type = PictureType.FrontCover, MimeType = "image/jpeg" }];
                    break;
                default:
                    if (TextAtoms.TryGetValue(field.Name, out var atom))
                        apple.SetData(atom, field.Values.Select(TextBox).ToArray());
                    else
                        SetDashValues(apple, field.Name, field.Values);
                    break;
            }
        }

        file.Save();
    }

    private static AppleDataBox TextBox(string value) =>
        new(ByteVector.FromString("data", StringType.Latin1), (uint)AppleDataBox.FlagType.ContainsText) { Text = value };

    /// <summary>Freeform atom (----:com.apple.iTunes:NAME) with one data child per value.</summary>
    private static void SetDashValues(AppleTag apple, string name, List<string> values)
    {
        apple.SetDashBox(ITunesMean, name, values[0]);
        if (values.Count == 1) return;

        var box = apple.OfType<AppleAnnotationBox>().First(b =>
            b.BoxType.ToString(StringType.Latin1) == "----"
            && b.Children.OfType<AppleAdditionalInfoBox>().Any(c => c.BoxType.ToString(StringType.Latin1) == "name" && c.Text == name));
        foreach (var value in values.Skip(1))
            box.AddChild(TextBox(value));
    }
}
