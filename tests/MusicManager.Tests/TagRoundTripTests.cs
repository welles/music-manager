using MusicManager.Core.Model;
using MusicManager.Core.Services;

namespace MusicManager.Tests;

public class TagRoundTripTests
{
    /// <summary>Everything the writer produces must be read back, otherwise re-tagging/editing drops fields.</summary>
    [Fact]
    public void WrittenTagsAreReadBackCompletely()
    {
        var path = Path.Combine(Path.GetTempPath(), $"roundtrip-{Guid.NewGuid():N}.m4a");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.m4a"), path);
        try
        {
            var tags = new TagSet
            {
                Title = "Baddadan", Artist = "Chase & Status and Bou feat. IRAH", Artists = ["Chase & Status", "Bou", "IRAH"],
                Album = "Baddadan", AlbumArtist = "Chase & Status and Bou", AlbumArtists = ["Chase & Status", "Bou"],
                TrackNumber = 1, TrackCount = 2, DiscNumber = 1, DiscCount = 1, Date = "2023-06-02", OriginalDate = "2023-06-01",
                Genres = ["Drum And Bass", "Jungle"], Remixer = "DJ X", Isrc = "GBAHT2300123", Label = "EMI",
                ReleaseCountry = "GB", ReleaseStatus = "official", ReleaseType = "single; remix", AcoustId = "acoust-1",
                MbRecordingId = "rec-1", MbTrackId = "track-1", MbReleaseId = "rel-1", MbReleaseGroupId = "rg-1",
                MbArtistIds = ["a-1", "a-2", "a-3"], MbAlbumArtistIds = ["a-1", "a-2"],
            };
            TagIo.WriteM4a(path, tags, null, "musicbrainz", "1-test");

            var readBack = TagIo.Read(path);
            var expected = TagFields.ForFinalTags(tags, null, "musicbrainz", "1-test");
            var actual = TagFields.ForFinalTags(readBack, null, "musicbrainz", "1-test");
            Assert.Equal(expected.Select(f => $"{f.Name}={string.Join("|", f.Values)}"), actual.Select(f => $"{f.Name}={string.Join("|", f.Values)}"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
