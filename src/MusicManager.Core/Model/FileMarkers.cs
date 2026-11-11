using System.Globalization;

namespace MusicManager.Core.Model;

/// <summary>
/// Identity and user data of a library song, mirrored into its file so a lost database can be rebuilt and
/// the file keeps its identity when it is moved or renamed outside the tool. Only rarely changing values:
/// play counts stay in the database (every play would rewrite the file).
/// </summary>
public sealed record FileMarkers(string? SongId, DateTimeOffset? AddedAt, int? Rating, DateTimeOffset? Favorite)
{
    public const string SongIdTag = "MUSICMANAGER_SONG_ID";
    public const string AddedTag = "MUSICMANAGER_ADDED";

    /// <summary>1..5, the scale of the Subsonic API.</summary>
    public const string RatingTag = "RATING";

    /// <summary>When the song was starred; absent = not a favorite.</summary>
    public const string FavoriteTag = "MUSICMANAGER_FAVORITE";

    public static readonly string[] TagNames = [SongIdTag, AddedTag, RatingTag, FavoriteTag];

    public static readonly FileMarkers None = new(null, null, null, null);

    /// <summary>Tag values; null = remove the tag.</summary>
    public IReadOnlyDictionary<string, string?> ToTags() => new Dictionary<string, string?>
    {
        [SongIdTag] = SongId,
        [AddedTag] = Format(AddedAt),
        [RatingTag] = Rating is >= 1 and <= 5 ? Rating.Value.ToString(CultureInfo.InvariantCulture) : null,
        [FavoriteTag] = Format(Favorite),
    };

    public static FileMarkers FromTags(Func<string, string?> get)
    {
        var rating = int.TryParse(get(RatingTag), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) && r is >= 1 and <= 5 ? r : (int?)null;
        return new FileMarkers(Clean(get(SongIdTag)), Parse(get(AddedTag)), rating, Parse(get(FavoriteTag)));
    }

    /// <summary>Canonical text, stored in the index to see whether the file is up to date without reading it.</summary>
    public string Canonical() => string.Join("\u001f", ToTags().Select(t => $"{t.Key}={t.Value}"));

    private static string? Format(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Parse(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d
            : null;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
