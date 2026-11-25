using Microsoft.EntityFrameworkCore;
using MusicManager.Core.Data;
using MusicManager.Core.Model;
using MusicManager.Core.Pipeline;
using MusicManager.Tests.Subsonic;

namespace MusicManager.Tests;

/// <summary>Which open duplicates go without a decision: 100 % automatically, from a chosen similarity with the button.</summary>
public class DiscardSimilarTests
{
    private static DuplicateMatch Match(double? similarity, bool? same, double? weakest = null, double gap = 0.3,
        DuplicateKind kind = DuplicateKind.Duplicate, string codec = "aac", int? kbps = 256) => new()
    {
        RelativePath = "a/b.m4a", Reason = "x", Kind = kind, AudioSimilarity = similarity, AudioWeakest = weakest ?? similarity,
        AudioLengthGap = similarity == null ? null : gap, SameAudio = same, AudioCheck = same == null ? null : "verglichen",
        DurationSeconds = 200, Codec = codec, BitrateKbps = kbps, Album = "Al",
    };

    private static TrackFile File(params DuplicateMatch[] matches) =>
        new()
        {
            RelativePath = "in.mp3", DurationSeconds = 200, Codec = "aac", BitrateKbps = 256, FinalTags = new TagSet { Album = "Al" },
            TargetPath = "A/Al/A - S.m4a", Duplicates = [.. matches],
        };

    [Theory]
    [InlineData(1.0, true, true)]
    [InlineData(0.996, true, true)] // shown as 100 %
    [InlineData(0.994, true, false)] // shown as 99 %
    [InlineData(1.0, false, false)] // a section or the length differs
    [InlineData(null, null, false)] // not compared
    public void OnlyHundredPercentOfTheSameAudioIsDiscardedAutomatically(double? similarity, bool? same, bool expected) =>
        Assert.Equal(expected, SameAudioChecker.IdenticalMatch(File(Match(similarity, same))) != null);

    [Fact]
    public void AnotherVersionIsNeverDiscardedAutomatically() =>
        Assert.Null(SameAudioChecker.IdenticalMatch(File(Match(1.0, true, kind: DuplicateKind.OtherVersion))));

    [Fact]
    public void TheSameRecordingOnAnotherReleaseIsNotDiscardedAutomatically()
    {
        // The single is in the library, the album track comes in: the album would be left with a gap.
        var single = Match(1.0, true);
        single.Album = "Song (Single)";
        single.MbReleaseId = "single";
        var file = File(single);
        file.FinalTags!.MbReleaseId = "album";

        Assert.Null(SameAudioChecker.IdenticalMatch(file));
        // The button can still take it, if chosen.
        Assert.True(SameAudioChecker.IsAtLeast(file, 98));
    }

    [Theory]
    [InlineData("rel", "rel", "x", "y", "other/path.m4a", true)] // same MusicBrainz release
    [InlineData(null, null, "Al", "al", "other/path.m4a", true)] // same album
    [InlineData("a", "b", "x", "y", "A/Al/A - S.m4a", true)] // the target path itself
    [InlineData("standard", "deluxe", "Al", "Al", "other/path.m4a", false)] // another edition of the album
    [InlineData("rel", null, "Al", "Al", "other/path.m4a", true)] // a release ID missing: the album decides
    [InlineData("a", "b", "x", "y", "other/path.m4a", false)]
    [InlineData(null, null, null, null, "other/path.m4a", false)] // nothing known: ask
    public void SameReleaseIsRecognized(string? release, string? libraryRelease, string? album, string? libraryAlbum, string libraryPath, bool same)
    {
        var match = Match(1.0, true);
        match.MbReleaseId = libraryRelease;
        match.Album = libraryAlbum;
        match.RelativePath = libraryPath;
        var file = File(match);
        file.FinalTags = new TagSet { MbReleaseId = release, Album = album };

        Assert.Equal(same, SameAudioChecker.IsSameRelease(file, match));
    }

    [Theory]
    [InlineData(0.985, 98, true)]
    [InlineData(0.974, 98, false)]
    [InlineData(0.95, 95, true)] // evenly a little off (another master): below the "same audio" verdict, but chosen
    public void TheButtonUsesTheChosenPercentage(double similarity, int percent, bool expected) =>
        Assert.Equal(expected, SameAudioChecker.IsAtLeast(File(Match(similarity, false)), percent));

    [Fact]
    public void ASectionThatSoundsDifferentStaysHoweverHighTheAverage() =>
        // A 10-second voice drop in a 4-minute track: 98 % on average, one section at 60 %.
        Assert.False(SameAudioChecker.IsAtLeast(File(Match(0.975, false, weakest: 0.6)), 90));

    [Fact]
    public void TheMostSimilarMatchCounts()
    {
        var file = File(Match(0.90, false), Match(0.99, true));

        Assert.Equal(0.99, SameAudioChecker.MostSimilar(file)!.AudioSimilarity);
        Assert.True(SameAudioChecker.IsAtLeast(file, 98));
    }

    [Fact]
    public void AnotherLengthIsNotDiscardedHoweverSimilar() =>
        // Measured like the verdict ("die Länge weicht um 3 s ab"), not with the stored lengths of the index.
        Assert.False(SameAudioChecker.IsAtLeast(File(Match(0.999, false, gap: 3)), 98));

    [Theory]
    [InlineData("flac", null, "aac", 256, false)] // lossless copy of a lossy library file
    [InlineData("aac", 320, "aac", 128, false)] // clearly higher bitrate
    [InlineData("aac", 260, "aac", 256, true)] // about the same
    [InlineData("mp3", 320, "flac", 900, true)] // the library file is lossless
    [InlineData("flac", 900, "alac", 800, true)] // both lossless
    public void ABetterCopyIsNotDiscardedAutomatically(string codec, int? kbps, string libraryCodec, int libraryKbps, bool discarded)
    {
        var file = File(Match(1.0, true, codec: libraryCodec, kbps: libraryKbps));
        file.Codec = codec;
        file.BitrateKbps = kbps;

        Assert.Equal(discarded, SameAudioChecker.IdenticalMatch(file) != null);
    }

    [Fact]
    public void ComparisonsWithoutTheSimilarityAreRunOnceMore()
    {
        var old = Match(null, true);
        Assert.True(SameAudioChecker.NeedsCheck(File(old)));
        var withoutGap = Match(0.99, true);
        withoutGap.AudioLengthGap = null;
        Assert.True(SameAudioChecker.NeedsCheck(File(withoutGap)));
        Assert.True(SameAudioChecker.NeedsCheck(File(Match(null, null))));
        Assert.False(SameAudioChecker.NeedsCheck(File(Match(0.99, true))));

        // Could not be compared (file unreadable): not again on every start.
        var failed = Match(null, null);
        failed.AudioCheck = "Audio-Vergleich nicht möglich: neue Datei nicht lesbar";
        Assert.False(SameAudioChecker.NeedsCheck(File(failed)));
    }

    private static async Task<(TrackFile File, string Path)> AddDuplicateAsync(SubsonicTestHost app, string name, params DuplicateMatch[] matches)
    {
        var relative = Path.Combine("dl", name);
        var path = Path.Combine(app.Root, "inbox", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await System.IO.File.WriteAllBytesAsync(path, [1, 2, 3]);
        await using var db = await app.Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var file = new TrackFile
        {
            RelativePath = relative, Status = FileStatus.Duplicate, DurationSeconds = 200, FinalTags = new TagSet { Title = name, Album = "Al" },
            TargetPath = $"A/{name}.m4a", Duplicates = [.. matches],
            Group = new InboxGroup { RelativeFolder = "dl", Open = false },
        };
        db.Files.Add(file);
        await db.SaveChangesAsync();
        return (file, path);
    }

    private static async Task<TrackFile> ReloadAsync(SubsonicTestHost app, int id)
    {
        await using var db = await app.Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        return await db.Files.AsNoTracking().FirstAsync(f => f.Id == id);
    }

    [Fact]
    public async Task TheButtonDiscardsOnlyOpenDuplicatesFromThePercentage()
    {
        using var app = new SubsonicTestHost();
        var (similar, similarPath) = await AddDuplicateAsync(app, "similar", Match(0.985, true));
        var (other, otherPath) = await AddDuplicateAsync(app, "other", Match(0.96, false));
        var (voice, voicePath) = await AddDuplicateAsync(app, "voice", Match(0.98, false, weakest: 0.6));

        var all = new[] { similar.Id, other.Id, voice.Id };
        var count = await app.Get<ReviewService>().DiscardSimilarAsync(all, 98);

        Assert.Equal(1, count);
        var discarded = await ReloadAsync(app, similar.Id);
        Assert.Equal(FileStatus.Discarded, discarded.Status);
        Assert.Equal(DuplicateDecision.KeepExisting, discarded.DuplicateDecision);
        Assert.False(System.IO.File.Exists(similarPath));
        Assert.True(System.IO.File.Exists(discarded.TrashPath));
        foreach (var (f, p) in new[] { (other, otherPath), (voice, voicePath) })
        {
            Assert.Equal(FileStatus.Duplicate, (await ReloadAsync(app, f.Id)).Status);
            Assert.True(System.IO.File.Exists(p));
        }

        // Already decided: nothing left to discard.
        Assert.Equal(0, await app.Get<ReviewService>().DiscardSimilarAsync(all, 98));
    }

    [Fact]
    public async Task OnlyTheConfirmedFilesAreDiscarded()
    {
        using var app = new SubsonicTestHost();
        var (shown, _) = await AddDuplicateAsync(app, "shown", Match(0.99, true));
        // Compared in the background while the confirmation was open: not part of what was confirmed.
        var (later, laterPath) = await AddDuplicateAsync(app, "later", Match(0.99, true));

        Assert.Equal(1, await app.Get<ReviewService>().DiscardSimilarAsync([shown.Id], 98));

        Assert.Equal(FileStatus.Duplicate, (await ReloadAsync(app, later.Id)).Status);
        Assert.True(System.IO.File.Exists(laterPath));
    }

    [Fact]
    public async Task ASecondDecisionIsRefused()
    {
        using var app = new SubsonicTestHost();
        var (file, _) = await AddDuplicateAsync(app, "once", Match(0.99, true));
        var review = app.Get<ReviewService>();
        await review.ResolveDuplicateAsync(file.Id, DuplicateDecision.KeepBoth);

        await Assert.ThrowsAsync<AlreadyDecidedException>(() => review.ResolveDuplicateAsync(file.Id, DuplicateDecision.KeepExisting));
        Assert.Equal(0, await review.DiscardSimilarAsync([file.Id], 98));
    }

    [Fact]
    public async Task AnIdenticalNewFileIsDiscardedWithItsOwnStatus()
    {
        using var app = new SubsonicTestHost();
        var (file, path) = await AddDuplicateAsync(app, "same", Match(1.0, true));

        Assert.True(app.Get<MatchingPipeline>().DiscardIfIdentical(file, path));

        Assert.Equal(FileStatus.DuplicateDiscarded, file.Status);
        Assert.Equal(DuplicateDecision.KeepExisting, file.DuplicateDecision);
        Assert.Contains("a/b.m4a", file.StatusReason);
        Assert.False(System.IO.File.Exists(path));
        Assert.True(System.IO.File.Exists(file.TrashPath));
    }

    [Fact]
    public async Task ANewFileThatSoundsDifferentSomewhereIsNotDiscardedAutomatically()
    {
        using var app = new SubsonicTestHost();
        var (file, path) = await AddDuplicateAsync(app, "voice", Match(1.0, false, weakest: 0.8));

        Assert.False(app.Get<MatchingPipeline>().DiscardIfIdentical(file, path));

        Assert.Equal(FileStatus.Duplicate, file.Status);
        Assert.True(System.IO.File.Exists(path));
    }

    private static async Task<bool> FpcalcAvailableAsync()
    {
        try
        {
            return (await Core.Services.ProcessRunner.RunAsync("fpcalc", ["-version"], CancellationToken.None)).ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The whole way through the pipeline with real audio: the new file is the same audio as the library file, so it is
    /// discarded instead of waiting in the duplicate view, and its inbox folder is cleaned up. Needs ffmpeg and fpcalc on
    /// the PATH (locally e.g. <c>nix shell nixpkgs#ffmpeg nixpkgs#chromaprint -c dotnet test</c>); returns early without.
    /// </summary>
    [Fact]
    public async Task AnIdenticalNewFileIsDiscardedByThePipeline()
    {
        if (!await FfmpegTestTools.AvailableAsync() || !await FpcalcAvailableAsync()) return;
        using var app = new SubsonicTestHost();

        var audio = Path.Combine(app.DataRoot, "noise.m4a");
        var r = await Core.Services.ProcessRunner.RunAsync("ffmpeg",
            ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "anoisesrc=d=30:c=pink:r=44100:seed=7", "-c:a", "aac", "-b:a", "192k", audio],
            CancellationToken.None);
        Assert.Equal(0, r.ExitCode);

        var target = Path.Combine("A", "A - Song.m4a");
        var libraryPath = Path.Combine(app.LibraryRoot, target);
        Directory.CreateDirectory(Path.GetDirectoryName(libraryPath)!);
        System.IO.File.Copy(audio, libraryPath);
        Core.Services.TagIo.WriteM4a(libraryPath, new TagSet { Title = "Song", Artist = "A", AlbumArtist = "A", Album = "Al" }, null, "musicbrainz");
        await app.ScanAsync();

        var inboxFolder = Path.Combine(app.Root, "inbox", "dl");
        Directory.CreateDirectory(inboxFolder);
        var inboxPath = Path.Combine(inboxFolder, "song.m4a");
        System.IO.File.Copy(audio, inboxPath);
        await System.IO.File.WriteAllBytesAsync(Path.Combine(inboxFolder, "cover.jpg"), [0xFF, 0xD8, 0xFF, 0xE0]);

        int id;
        await using (var db = await app.Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
        {
            var file = new TrackFile
            {
                RelativePath = Path.Combine("dl", "song.m4a"), Status = FileStatus.Approved, Codec = "aac", BitrateKbps = 192,
                DurationSeconds = 30, FinalTags = new TagSet { Title = "Song", Artist = "A", AlbumArtist = "A", Album = "Al" },
                TargetPath = target, Group = new InboxGroup { RelativeFolder = "dl", Open = false },
            };
            db.Files.Add(file);
            await db.SaveChangesAsync();
            id = file.Id;
        }

        await app.Get<MatchingPipeline>().WriteFileAsync(id, CancellationToken.None);

        var result = await ReloadAsync(app, id);
        Assert.Equal(FileStatus.DuplicateDiscarded, result.Status);
        Assert.Equal(DuplicateDecision.KeepExisting, result.DuplicateDecision);
        Assert.Contains(result.Duplicates, d => d is { SameAudio: true, AudioSimilarity: >= 0.995 });
        Assert.True(System.IO.File.Exists(result.TrashPath));
        // The library file stays, the inbox folder with its extra file is cleaned up.
        Assert.True(System.IO.File.Exists(libraryPath));
        Assert.False(Directory.Exists(inboxFolder));
    }
}
