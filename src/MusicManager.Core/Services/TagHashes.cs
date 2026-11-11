using System.Security.Cryptography;
using System.Text;
using MusicManager.Core.Model;
using TagLib;
using TagLib.Mpeg4;

namespace MusicManager.Core.Services;

/// <summary>
/// Hashes of the tags and the cover of an M4A file as Music Manager wrote them
/// (<c>MUSICMANAGER_TAGS_HASH</c>, <c>MUSICMANAGER_COVER_HASH</c>). A library scan hashes the file again: a
/// different value means the tags (or the cover) were changed outside the tool. The file markers
/// (song ID, added date, favorite, rating) are left out, since <see cref="Pipeline.FileMarkerSync"/> changes
/// them on purpose.
/// </summary>
public sealed record TagHashes(string Tags, string Cover)
{
    public const string TagsTag = "MUSICMANAGER_TAGS_HASH";
    public const string CoverTag = "MUSICMANAGER_COVER_HASH";

    /// <summary>Cover hash of a file without a cover (an empty value would remove the atom).</summary>
    public const string NoCover = "none";

    public static readonly string[] TagNames = [TagsTag, CoverTag];

    private static readonly HashSet<string> Excluded =
        new([.. FileMarkers.TagNames, .. TagNames], StringComparer.OrdinalIgnoreCase);

    /// <summary>Hashes of the tags currently in <paramref name="apple"/> (in memory, before or after saving).</summary>
    public static TagHashes Compute(AppleTag apple)
    {
        var fields = new List<(string Name, string Values)>();
        using var cover = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var hasCover = false;

        foreach (var box in apple.OfType<AppleAnnotationBox>())
        {
            var type = box.BoxType.ToString(StringType.Latin1);
            var data = box.Children.OfType<AppleDataBox>().ToList();
            if (type == "covr")
            {
                foreach (var d in data)
                {
                    cover.AppendData(d.Data.Data);
                    hasCover = true;
                }

                continue;
            }

            var name = type;
            if (type == "----")
            {
                var dashName = box.Children.OfType<AppleAdditionalInfoBox>()
                    .FirstOrDefault(c => c.BoxType.ToString(StringType.Latin1) == "name")?.Text ?? "";
                if (Excluded.Contains(dashName)) continue;
                name = "----:" + dashName;
            }

            fields.Add((name, string.Join("\u001e", data.Select(Value))));
        }

        // Atom order is not meaningful (another tagger may rewrite the file in a different order).
        var canonical = string.Join("\u001f", fields.OrderBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.Values, StringComparer.Ordinal)
            .Select(f => $"{f.Name}={f.Values}"));
        var tags = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..32];
        var coverHash = hasCover ? Convert.ToHexStringLower(cover.GetHashAndReset())[..32] : NoCover;
        return new TagHashes(tags, coverHash);
    }

    private static string Value(AppleDataBox d) =>
        d.Flags == (uint)AppleDataBox.FlagType.ContainsText ? d.Text ?? "" : $"{d.Flags}:{Convert.ToHexString(d.Data.Data)}";

    /// <summary>The hashes stored in the file, or null when it has none (written before this check existed).</summary>
    public static TagHashes? Stored(AppleTag apple)
    {
        var tags = apple.GetDashBox(TagFields.ITunesMean, TagsTag);
        var cover = apple.GetDashBox(TagFields.ITunesMean, CoverTag);
        return string.IsNullOrEmpty(tags) ? null : new TagHashes(tags, string.IsNullOrEmpty(cover) ? NoCover : cover);
    }

    /// <summary>Stores the hashes of the current tags in <paramref name="apple"/> (call right before saving).</summary>
    public static TagHashes Store(AppleTag apple)
    {
        var hashes = Compute(apple);
        apple.SetDashBox(TagFields.ITunesMean, TagsTag, hashes.Tags);
        apple.SetDashBox(TagFields.ITunesMean, CoverTag, hashes.Cover);
        return hashes;
    }
}

/// <summary>Result of comparing the stored hashes of a file with its current tags.</summary>
/// <param name="Stored">Hashes in the file; null = the file has none yet.</param>
public sealed record TagHashCheck(TagHashes? Stored, TagHashes Actual)
{
    public bool TagsEdited => Stored != null && Stored.Tags != Actual.Tags;
    public bool CoverEdited => Stored != null && Stored.Cover != Actual.Cover;
    public bool Edited => TagsEdited || CoverEdited;
}
