using MusicManager.Core.Catalog;
using MusicManager.Core.Model;
using MusicManager.Core.Pipeline;
using MusicManager.Core.Services;
using TagLib;
using TagLib.Mpeg4;

namespace MusicManager.Tests.Subsonic;

/// <summary>Tag edits made outside Music Manager are detected through the hash stored in the file.</summary>
public class TagHashTests
{
    private static TagSet Tags(string title) => new()
    {
        Title = title, Artist = "A", Artists = ["A"], Album = "B", AlbumArtist = "A", AlbumArtists = ["A"], Genres = ["Rock"],
        TrackNumber = 1, TrackCount = 10, MbRecordingId = "rec", MbReleaseId = "rel",
    };

    /// <summary>Edits the file like another tagger would (TagLib, no Music Manager code).</summary>
    private static void EditOutside(string path, Action<AppleTag> edit)
    {
        using var file = TagLib.File.Create(path);
        edit((AppleTag)file.GetTag(TagTypes.Apple, true));
        file.Save();
    }

    [Fact]
    public async Task FreshlyWrittenFileIsNotEditedOutside()
    {
        using var app = new SubsonicTestHost();
        var path = app.AddFile("A/B/1.m4a", Tags("One"));

        var check = TagIo.CheckHashes(path)!;
        Assert.NotNull(check.Stored);
        Assert.False(check.Edited);

        await app.ScanAsync();
        var entry = (await app.EntryAsync("A/B/1.m4a"))!;
        Assert.Equal(check.Stored!.Tags, entry.TagsHash);
        Assert.False(entry.TagsEditedOutside);
        Assert.False(entry.CoverEditedOutside);
    }

    [Fact]
    public async Task OutsideTagEditIsDetectedOnScan()
    {
        using var app = new SubsonicTestHost();
        var path = app.AddFile("A/B/1.m4a", Tags("One"));
        await app.ScanAsync();

        EditOutside(path, apple => apple.Title = "One (fixed by hand)");
        await app.ScanAsync();

        var entry = (await app.EntryAsync("A/B/1.m4a"))!;
        Assert.True(entry.TagsEditedOutside);
        Assert.False(entry.CoverEditedOutside);
        var track = (await app.Get<LibraryCatalog>().GetTracksAsync()).Single();
        Assert.Contains(LibraryProblem.EditedOutside, track.Problems(""));
    }

    [Fact]
    public async Task OutsideCoverChangeIsDetectedSeparately()
    {
        using var app = new SubsonicTestHost();
        var path = app.AddFile("A/B/1.m4a", Tags("One"));

        EditOutside(path, apple => apple.Pictures = [new Picture(new ByteVector([0xFF, 0xD8, 0xFF, 0x01, 0x02])) { Type = PictureType.FrontCover }]);
        await app.ScanAsync();

        var entry = (await app.EntryAsync("A/B/1.m4a"))!;
        Assert.False(entry.TagsEditedOutside);
        Assert.True(entry.CoverEditedOutside);
    }

    [Fact]
    public async Task MarkerChangesAreNotOutsideEdits()
    {
        using var app = new SubsonicTestHost();
        var path = app.AddFile("A/B/1.m4a", Tags("One"));
        await app.ScanAsync();
        var entry = (await app.EntryAsync("A/B/1.m4a"))!;

        // Written by the marker sync …
        await app.Get<UserDataStore>().StarAsync([entry.SongId!]);
        await app.Get<UserDataStore>().SetRatingAsync(entry.SongId!, 4);
        Assert.Equal(1, await app.Get<FileMarkerSync>().SyncAsync(CancellationToken.None));
        Assert.False(TagIo.CheckHashes(path)!.Edited);

        // … or by hand (a player writing the rating): neither counts.
        EditOutside(path, apple => apple.SetDashBox(TagFields.ITunesMean, FileMarkers.RatingTag, "2"));
        await app.ScanAsync();
        Assert.False((await app.EntryAsync("A/B/1.m4a"))!.TagsEditedOutside);
    }

    [Fact]
    public async Task FileWithoutHashGetsABaselineOnMarkerSync()
    {
        using var app = new SubsonicTestHost();
        var path = app.AddFile("A/B/1.m4a", Tags("One"));
        // Written before the hash existed.
        EditOutside(path, apple =>
        {
            apple.SetDashBox(TagFields.ITunesMean, TagHashes.TagsTag, "");
            apple.SetDashBox(TagFields.ITunesMean, TagHashes.CoverTag, "");
        });
        Assert.Null(TagIo.CheckHashes(path)!.Stored);

        await app.ScanAsync();
        var entry = (await app.EntryAsync("A/B/1.m4a"))!;
        Assert.Null(entry.TagsHash);
        Assert.False(entry.TagsEditedOutside);

        Assert.Equal(1, await app.Get<FileMarkerSync>().SyncAsync(CancellationToken.None));
        var check = TagIo.CheckHashes(path)!;
        Assert.NotNull(check.Stored);
        Assert.False(check.Edited);
        Assert.Equal(check.Stored!.Tags, (await app.EntryAsync("A/B/1.m4a"))!.TagsHash);

        // Nothing more to do afterwards, and the baseline now catches edits.
        Assert.Equal(0, await app.Get<FileMarkerSync>().SyncAsync(CancellationToken.None));
        EditOutside(path, apple => apple.Album = "Other");
        await app.ScanAsync();
        Assert.True((await app.EntryAsync("A/B/1.m4a"))!.TagsEditedOutside);
    }

    [Fact]
    public async Task RetagKeepsOutsideEditsAndClearsTheFlag()
    {
        using var app = new SubsonicTestHost();
        var path = app.AddFile("A/B/1.m4a", Tags("One"));
        EditOutside(path, apple => apple.Title = "One (fixed by hand)");
        await app.ScanAsync();
        var entry = (await app.EntryAsync("A/B/1.m4a"))!;
        Assert.True(entry.TagsEditedOutside);

        var retag = app.Get<RetagService>();
        Assert.True(await retag.StartPreviewAsync([entry.Id]));
        await WaitAsync(retag);
        var preview = Assert.Single(retag.Previews);
        Assert.Null(preview.Error);
        Assert.True(preview.EditedOutside);
        Assert.False(preview.DiscardsOutsideEdits);
        Assert.True(preview.Changed);
        // Not rebuilt from MusicBrainz (which would need the network): the edit stays and the file counts as manual.
        Assert.Equal("One (fixed by hand)", preview.NewTags!.Title);
        Assert.Equal("manual", preview.Source);

        Assert.True(await retag.StartApplyAsync([preview], movePaths: false));
        await WaitAsync(retag);
        Assert.True(preview.Applied, preview.Error);

        Assert.Equal("One (fixed by hand)", TagIo.Read(path).Title);
        Assert.False(TagIo.CheckHashes(path)!.Edited);
        Assert.False((await app.EntryAsync("A/B/1.m4a"))!.TagsEditedOutside);
    }

    [Fact]
    public async Task DiscardingIsOnlyOfferedForMusicBrainzFiles()
    {
        using var app = new SubsonicTestHost();
        var path = app.AddFile("A/B/1.m4a", Tags("One"));
        TagIo.WriteM4a(path, Tags("One"), null, "manual");
        EditOutside(path, apple => apple.Title = "One (fixed by hand)");
        await app.ScanAsync();
        var entry = (await app.EntryAsync("A/B/1.m4a"))!;

        // Asked to discard, but a manually tagged file has nothing to rebuild from: the edit stays.
        var retag = app.Get<RetagService>();
        Assert.True(await retag.StartPreviewAsync([entry.Id], discardOutsideEdits: true));
        await WaitAsync(retag);
        var preview = Assert.Single(retag.Previews);
        Assert.True(preview.EditedOutside);
        Assert.False(preview.CanDiscardOutsideEdits);
        Assert.False(preview.DiscardsOutsideEdits);
        Assert.Equal("One (fixed by hand)", preview.NewTags!.Title);
        Assert.Contains("nicht verwerfbar", preview.Method);
    }

    private static async Task WaitAsync(RetagService retag)
    {
        for (var i = 0; i < 200 && retag.State.Running; i++) await Task.Delay(50);
        Assert.False(retag.State.Running);
    }
}
