using Microsoft.EntityFrameworkCore;
using MusicManager.Core.Data;
using MusicManager.Core.Model;
using MusicManager.Core.Pipeline;
using MusicManager.Core.Services;
using TagLib;
using TagLib.Mpeg4;

namespace MusicManager.Tests.Subsonic;

/// <summary>Album and artist images are always square (issue #15).</summary>
public class SquareImageTests
{
    private static TagSet Tags(string title) => new()
    {
        Title = title, Artist = "A", Artists = ["A"], Album = "B", AlbumArtist = "A", AlbumArtists = ["A"],
        TrackNumber = 1, TrackCount = 10, MbRecordingId = "rec", MbReleaseId = "rel",
    };

    [Theory]
    [InlineData("300x200\n", 300, 200)]
    [InlineData("1200x1200", 1200, 1200)]
    public void SizeIsParsed(string output, int width, int height) =>
        Assert.Equal((width, height), AudioTools.ParseSize(output));

    [Theory]
    [InlineData("")]
    [InlineData("N/A")]
    [InlineData("0x0")]
    public void UnreadableSizeIsNull(string output) => Assert.Null(AudioTools.ParseSize(output));

    [Fact]
    public void FilterCutsTheCenteredSquareBeforeScaling()
    {
        Assert.Equal("crop='min(iw,ih)':'min(iw,ih)'", AudioTools.SquareFilter(null));
        Assert.Equal("crop='min(iw,ih)':'min(iw,ih)',scale='min(500,iw)':'min(500,ih)'", AudioTools.SquareFilter(500));
    }

    [Fact]
    public void ReplacingTheCoverKeepsTheTagsAndRenewsTheHash()
    {
        using var app = new SubsonicTestHost();
        var path = app.AddFile("A/B/1.m4a", Tags("One"));
        TagIo.WriteM4a(path, Tags("One"), [0xFF, 0xD8, 1, 2, 3], "musicbrainz");

        byte[] square = [0xFF, 0xD8, 9, 9, 9];
        Assert.NotNull(TagIo.ReplaceCover(path, square));

        Assert.Equal(square, TagIo.ReadEmbeddedCover(path));
        Assert.Equal("One", TagIo.Read(path).Title);
        Assert.False(TagIo.CheckHashes(path)!.Edited);
    }

    /// <summary>Edits the file like another tagger would (TagLib, no Music Manager code).</summary>
    private static void EditOutside(string path, Action<AppleTag> edit)
    {
        using var file = TagLib.File.Create(path);
        edit((AppleTag)file.GetTag(TagTypes.Apple, true));
        file.Save();
    }

    [Fact]
    public void ReplacingTheCoverKeepsAnOutsideTagEditVisible()
    {
        using var app = new SubsonicTestHost();
        var path = app.AddFile("A/B/1.m4a", Tags("One"));
        TagIo.WriteM4a(path, Tags("One"), [0xFF, 0xD8, 1, 2, 3], "musicbrainz");
        EditOutside(path, apple => apple.Title = "Edited elsewhere");

        Assert.NotNull(TagIo.ReplaceCover(path, [0xFF, 0xD8, 9, 9, 9]));

        var check = TagIo.CheckHashes(path)!;
        Assert.True(check.TagsEdited); // still visible
        Assert.False(check.CoverEdited); // the new cover is the tool's own
    }

    [Fact]
    public void ReplacingACoverEditedOutsideKeepsTheEditVisible()
    {
        using var app = new SubsonicTestHost();
        var path = app.AddFile("A/B/1.m4a", Tags("One"));
        TagIo.WriteM4a(path, Tags("One"), [0xFF, 0xD8, 1, 2, 3], "musicbrainz");
        EditOutside(path, apple => apple.Pictures = [new Picture(new ByteVector(new byte[] { 0xFF, 0xD8, 7, 7 })) { Type = PictureType.FrontCover }]);

        Assert.Null(TagIo.ReplaceCover(path, [0xFF, 0xD8, 9, 9, 9]));
        Assert.True(TagIo.CheckHashes(path)!.CoverEdited);
    }

    [Fact]
    public async Task ImagesAreCutToTheCenteredSquare()
    {
        if (!await FfmpegTestTools.AvailableAsync()) return;
        using var app = new SubsonicTestHost();
        var audio = app.Get<AudioTools>();
        var landscape = await FfmpegTestTools.ImageAsync(Path.Combine(app.Root, "wide.jpg"), 300, 200);
        var square = await FfmpegTestTools.ImageAsync(Path.Combine(app.Root, "square.jpg"), 200, 200);

        Assert.Equal((200, 200), await audio.ImageSizeAsync((await audio.SquareImageAsync(landscape, null, default))!, default));
        Assert.Equal((100, 100), await audio.ImageSizeAsync((await audio.SquareImageAsync(landscape, 100, default))!, default));
        Assert.Equal((150, 150), await audio.ImageSizeAsync((await audio.ResizeImageAsync(landscape, 150, default))!, default));
        // Square and small enough: returned as it is, not encoded again.
        Assert.Same(square, await audio.SquareImageAsync(square, 500, default));
    }

    [Fact]
    public async Task AnImageFileKeepsItsFormatAndTime()
    {
        if (!await FfmpegTestTools.AvailableAsync()) return;
        using var app = new SubsonicTestHost();
        var audio = app.Get<AudioTools>();
        var path = Path.Combine(app.Root, "cover.png");
        await FfmpegTestTools.ImageAsync(path, 200, 320);
        var time = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        System.IO.File.SetLastWriteTimeUtc(path, time);

        Assert.True(await audio.SquareImageFileAsync(path, null, default));
        Assert.False(await audio.SquareImageFileAsync(path, null, default));

        var data = await System.IO.File.ReadAllBytesAsync(path);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], data[..4]); // still a PNG
        Assert.Equal((200, 200), await audio.ImageSizeAsync(data, default));
        Assert.Equal(time, System.IO.File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task ExistingImagesAreCutAndTheFileStaysUnflagged()
    {
        if (!await FfmpegTestTools.AvailableAsync()) return;
        using var app = new SubsonicTestHost();
        var audio = app.Get<AudioTools>();

        // A song with a landscape cover, its folder cover, a square second song, and a cached artist image.
        var wide = await FfmpegTestTools.ImageAsync(Path.Combine(app.Root, "wide.jpg"), 300, 200);
        var squareImage = await FfmpegTestTools.ImageAsync(Path.Combine(app.Root, "square.jpg"), 200, 200);
        var song = app.AddFile("A/B/1.m4a", Tags("One"));
        TagIo.WriteM4a(song, Tags("One"), wide, "musicbrainz");
        var squareSong = app.AddFile("A/C/1.m4a", Tags("Two"));
        TagIo.WriteM4a(squareSong, Tags("Two"), squareImage, "musicbrainz");
        var folderCover = Path.Combine(app.LibraryRoot, "A", "B", "cover.jpg");
        await System.IO.File.WriteAllBytesAsync(folderCover, wide);
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "artists"));
        var artist = Path.Combine(app.DataRoot, "artists", "a.jpg");
        await FfmpegTestTools.ImageAsync(artist, 400, 300);
        await using (var db = await app.Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
        {
            db.ArtistImages.Add(new ArtistImage { Key = "a", Name = "A", Source = "deezer", FilePath = Path.Combine("artists", "a.jpg") });
            await db.SaveChangesAsync();
        }

        await app.ScanAsync();
        var squareSongBytes = await System.IO.File.ReadAllBytesAsync(squareSong);
        var service = app.Get<ImageSquareService>();
        await service.RunAsync(default);

        Assert.Equal(3, service.State.Squared);
        Assert.Equal(4, service.State.Total);
        Assert.Equal((200, 200), await audio.ImageSizeAsync(TagIo.ReadEmbeddedCover(song)!, default));
        Assert.Equal((200, 200), await audio.ImageSizeAsync(await System.IO.File.ReadAllBytesAsync(folderCover), default));
        Assert.Equal((300, 300), await audio.ImageSizeAsync(await System.IO.File.ReadAllBytesAsync(artist), default));
        Assert.Equal(squareSongBytes, await System.IO.File.ReadAllBytesAsync(squareSong)); // untouched

        // The original folder cover is in the trash; the artist image (a cache) is not.
        var trashed = Directory.GetFiles(Path.Combine(app.Root, "trash"), "*", SearchOption.AllDirectories);
        var original = Assert.Single(trashed);
        Assert.EndsWith(Path.Combine(ImageSquareService.TrashFolder, "A", "B", "cover.jpg"), original);
        Assert.Equal(wide, await System.IO.File.ReadAllBytesAsync(original));

        // The index knows the new file, and the new cover does not count as an outside edit.
        var entry = (await app.EntryAsync("A/B/1.m4a"))!;
        var info = new FileInfo(song);
        Assert.Equal(info.Length, entry.FileSize);
        Assert.Equal(info.LastWriteTimeUtc, entry.FileModifiedUtc);
        Assert.False(TagIo.CheckHashes(song)!.Edited);
        await app.ScanAsync();
        Assert.False((await app.EntryAsync("A/B/1.m4a"))!.CoverEditedOutside);
    }
}
