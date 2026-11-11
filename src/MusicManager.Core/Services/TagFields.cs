using MusicManager.Core.Model;
using TagLib;
using TagLib.Mpeg4;
using File = TagLib.File;

namespace MusicManager.Core.Services;

/// <summary>
/// One tag field as stored in a file. <see cref="Values"/> holds every individual value, so a
/// real multi-value field (3 values) can be told apart from a single string containing commas.
/// </summary>
public sealed record TagField(string Name, List<string> Values)
{
    public bool IsMulti => Values.Count > 1;
}

/// <summary>Raw tag fields of a file and the fields the writer produces, for the before/after view.</summary>
public static class TagFields
{
    public const string ITunesMean = "com.apple.iTunes";

    /// <summary>Display order of well-known fields; all others follow alphabetically.</summary>
    public static readonly string[] Order =
    [
        "TITLE", "ARTIST", "ARTISTS", "ALBUM", "ALBUMARTIST", "ALBUMARTISTS", "TRACKNUMBER", "DISCNUMBER", "DATE",
        "ORIGINALDATE", "GENRE", "REMIXER", "LABEL", "ISRC", "COVER", "Acoustid Id", "MusicBrainz Track Id",
        "MusicBrainz Release Track Id", "MusicBrainz Album Id", "MusicBrainz Release Group Id", "MusicBrainz Artist Id",
        "MusicBrainz Album Artist Id", "MusicBrainz Album Type", "MusicBrainz Album Status", "MusicBrainz Album Release Country",
        TagIo.SourceMarker,
    ];

    private static readonly Dictionary<string, string> Mp4Names = new()
    {
        ["©nam"] = "TITLE", ["©ART"] = "ARTIST", ["aART"] = "ALBUMARTIST", ["©alb"] = "ALBUM", ["trkn"] = "TRACKNUMBER",
        ["disk"] = "DISCNUMBER", ["©gen"] = "GENRE", ["gnre"] = "GENRE", ["©day"] = "DATE", ["covr"] = "COVER",
        ["©too"] = "ENCODER", ["tvsh"] = "SHOW", ["©cmt"] = "COMMENT", ["cprt"] = "COPYRIGHT", ["©wrt"] = "COMPOSER",
        ["©lyr"] = "LYRICS", ["cpil"] = "COMPILATION", ["tmpo"] = "BPM", ["rtng"] = "ADVISORY", ["©grp"] = "GROUPING",
        ["soar"] = "ARTISTSORT", ["soaa"] = "ALBUMARTISTSORT", ["sonm"] = "TITLESORT", ["soal"] = "ALBUMSORT", ["soco"] = "COMPOSERSORT",
    };

    private static readonly Dictionary<string, string> Id3Names = new()
    {
        ["TIT2"] = "TITLE", ["TPE1"] = "ARTIST", ["TPE2"] = "ALBUMARTIST", ["TALB"] = "ALBUM", ["TRCK"] = "TRACKNUMBER",
        ["TPOS"] = "DISCNUMBER", ["TCON"] = "GENRE", ["TDRC"] = "DATE", ["TYER"] = "DATE", ["TDOR"] = "ORIGINALDATE",
        ["TSSE"] = "ENCODER", ["TPUB"] = "LABEL", ["TSRC"] = "ISRC", ["TCOM"] = "COMPOSER", ["TPE4"] = "REMIXER",
        ["TSOP"] = "ARTISTSORT", ["TSO2"] = "ALBUMARTISTSORT", ["TSOT"] = "TITLESORT", ["TSOA"] = "ALBUMSORT", ["TBPM"] = "BPM",
        ["TCOP"] = "COPYRIGHT", ["TENC"] = "ENCODEDBY",
    };

    // --- Reading ---------------------------------------------------------------------------

    public static List<TagField> Read(string path)
    {
        try
        {
            using var file = File.Create(path);
            var fields = new List<TagField>();

            if (file.GetTag(TagTypes.Apple) is AppleTag apple)
                ReadMp4(apple, fields);
            else if (file.GetTag(TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
                ReadId3(id3, fields);
            else if (file.GetTag(TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
                ReadXiph(xiph, fields);
            else
                ReadGeneric(file.Tag, fields);

            return Merge(fields);
        }
        catch (Exception ex) when (ex is CorruptFileException or UnsupportedFormatException)
        {
            return [new TagField("_error", [ex.Message])];
        }
    }

    private static void ReadMp4(AppleTag apple, List<TagField> fields)
    {
        foreach (var box in apple)
        {
            if (box is not AppleAnnotationBox ann) continue;
            var type = box.BoxType.ToString(StringType.Latin1);
            string name;
            if (type == "----")
            {
                name = ann.Children.OfType<AppleAdditionalInfoBox>()
                    .FirstOrDefault(c => c.BoxType.ToString(StringType.Latin1) == "name")?.Text ?? "----";
            }
            else
            {
                name = Mp4Names.GetValueOrDefault(type, type);
            }

            var values = new List<string>();
            foreach (var data in ann.Children.OfType<AppleDataBox>())
            {
                values.Add(type switch
                {
                    "trkn" or "disk" => PairValue(data.Data),
                    "covr" => $"Bild ({ImageType(data.Data)}, {data.Data.Count / 1024} KB)",
                    _ when data.Flags == (uint)AppleDataBox.FlagType.ContainsText => data.Text ?? "",
                    _ => $"<{data.Data.Count} Byte>",
                });
            }

            fields.Add(new TagField(name, values));
        }
    }

    private static void ReadId3(TagLib.Id3v2.Tag id3, List<TagField> fields)
    {
        foreach (var frame in id3.GetFrames())
        {
            switch (frame)
            {
                case TagLib.Id3v2.UserTextInformationFrame txxx:
                    fields.Add(new TagField(txxx.Description ?? "TXXX", [.. txxx.Text]));
                    break;
                case TagLib.Id3v2.TextInformationFrame text:
                    var id = text.FrameId.ToString(StringType.Latin1);
                    fields.Add(new TagField(Id3Names.GetValueOrDefault(id, id), [.. text.Text]));
                    break;
                case TagLib.Id3v2.AttachmentFrame pic:
                    fields.Add(new TagField("COVER", [$"Bild ({pic.MimeType}, {pic.Data.Count / 1024} KB)"]));
                    break;
                case TagLib.Id3v2.CommentsFrame comment:
                    fields.Add(new TagField("COMMENT", [comment.Text]));
                    break;
                default:
                    var fid = frame.FrameId.ToString(StringType.Latin1);
                    fields.Add(new TagField(fid, [frame.ToString() ?? ""]));
                    break;
            }
        }
    }

    private static void ReadXiph(TagLib.Ogg.XiphComment xiph, List<TagField> fields)
    {
        foreach (var name in xiph)
            fields.Add(new TagField(name.ToUpperInvariant(), [.. xiph.GetField(name)]));
        if (xiph.Pictures.Length > 0)
            fields.Add(new TagField("COVER", xiph.Pictures.Select(p => $"Bild ({p.MimeType}, {p.Data.Count / 1024} KB)").ToList()));
    }

    private static void ReadGeneric(TagLib.Tag tag, List<TagField> fields)
    {
        void Add(string name, IEnumerable<string?>? values)
        {
            var list = (values ?? []).Where(v => v != null).Cast<string>().ToList();
            if (list.Count > 0) fields.Add(new TagField(name, list));
        }

        Add("TITLE", [tag.Title]);
        Add("ARTIST", tag.Performers);
        Add("ALBUMARTIST", tag.AlbumArtists);
        Add("ALBUM", [tag.Album]);
        if (tag.Track > 0) Add("TRACKNUMBER", [tag.TrackCount > 0 ? $"{tag.Track}/{tag.TrackCount}" : tag.Track.ToString()]);
        if (tag.Year > 0) Add("DATE", [tag.Year.ToString()]);
        Add("GENRE", tag.Genres);
    }

    /// <summary>Joins repeated fields (e.g. several identical atoms) into one entry.</summary>
    private static List<TagField> Merge(List<TagField> fields) =>
        fields.GroupBy(f => f.Name).Select(g => new TagField(g.Key, g.SelectMany(f => f.Values).ToList())).ToList();

    private static string PairValue(ByteVector data)
    {
        if (data.Count < 6) return "";
        var number = data.Mid(2, 2).ToUShort();
        var total = data.Mid(4, 2).ToUShort();
        return total > 0 ? $"{number}/{total}" : number.ToString();
    }

    private static string ImageType(ByteVector data) =>
        data.StartsWith(new ByteVector(0x89, 0x50, 0x4E, 0x47)) ? "PNG" : data.StartsWith(new ByteVector(0xFF, 0xD8)) ? "JPEG" : "Bild";

    // --- Fields of the final tags (exactly what WriteM4a writes) --------------------------

    public static List<TagField> ForFinalTags(TagSet t, string? coverDescription, string? source, string? rulesVersion = null)
    {
        var fields = new List<TagField>();

        void One(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) fields.Add(new TagField(name, [value]));
        }

        void Many(string name, IEnumerable<string> values)
        {
            var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            if (list.Count > 0) fields.Add(new TagField(name, list));
        }

        One("TITLE", t.Title);
        // ARTIST and ALBUMARTIST hold the individual artists as multi-value fields (Picard script:
        // $setmulti(artist,%artists%) + $delete(artists)) – no joined "A feat. B" string, no ARTISTS tag.
        Many("ARTIST", t.Artists.Count > 0 ? t.Artists : [t.Artist ?? ""]);
        One("ALBUM", t.Album);
        Many("ALBUMARTIST", t.AlbumArtists.Count > 0 ? t.AlbumArtists : [t.AlbumArtist ?? ""]);
        if (t.TrackNumber != null) One("TRACKNUMBER", t.TrackCount != null ? $"{t.TrackNumber}/{t.TrackCount}" : $"{t.TrackNumber}");
        if (t.DiscNumber != null) One("DISCNUMBER", t.DiscCount != null ? $"{t.DiscNumber}/{t.DiscCount}" : $"{t.DiscNumber}");
        One("DATE", t.Date);
        One("ORIGINALDATE", t.OriginalDate);
        Many("GENRE", t.Genres);
        One("REMIXER", t.Remixer);
        One("LABEL", t.Label);
        One("ISRC", t.Isrc);
        One("COVER", coverDescription);
        One("Acoustid Id", t.AcoustId);
        One("MusicBrainz Track Id", t.MbRecordingId);
        One("MusicBrainz Release Track Id", t.MbTrackId);
        One("MusicBrainz Album Id", t.MbReleaseId);
        One("MusicBrainz Release Group Id", t.MbReleaseGroupId);
        Many("MusicBrainz Artist Id", t.MbArtistIds);
        Many("MusicBrainz Album Artist Id", t.MbAlbumArtistIds);
        Many("MusicBrainz Album Type", (t.ReleaseType ?? "").Split(';', StringSplitOptions.TrimEntries));
        One("MusicBrainz Album Status", t.ReleaseStatus);
        One("MusicBrainz Album Release Country", t.ReleaseCountry);
        One(TagIo.SourceMarker, source);
        One(TagIo.RulesMarker, rulesVersion);
        return fields;
    }

    /// <summary>All values of the first field with one of the names (case-insensitive; names differ per format).</summary>
    public static List<string> Get(IReadOnlyList<TagField> fields, params string[] names)
    {
        foreach (var name in names)
        {
            var f = fields.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (f != null) return f.Values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        }

        return [];
    }

    public static int SortKey(string name)
    {
        var i = Array.IndexOf(Order, name);
        return i >= 0 ? i : Order.Length;
    }
}
