using System.Net;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MusicManager.Core.Data;
using MusicManager.Core.Model;
using MusicManager.Core.Pipeline;
using MusicManager.Core.Services;
using MusicManager.Tests.Subsonic;

namespace MusicManager.Tests;

/// <summary>
/// "Spotify/Deezer übernehmen" for many files: every file is saved and queued for writing on its own, and a file that
/// fails does not stop or discard the others. Runs against the real services with a test database; the cover
/// download is faked (no network), the pipeline worker does not run, so the queue can be read.
/// </summary>
public sealed class ExternalTakeoverTests : IDisposable
{
    private const string FailingCover = "https://covers.test/fails.jpg";
    private const string TimingOutCover = "https://covers.test/times-out.jpg";

    private readonly SubsonicTestHost _base = new();
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host;

    public ExternalTakeoverTests()
    {
        _host = _base.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddHttpClient<CoverArtService>().ConfigurePrimaryHttpMessageHandler(() => new FakeCovers())));
    }

    public void Dispose()
    {
        _host.Dispose();
        _base.Dispose();
    }

    private T Get<T>() where T : notnull => _host.Services.GetRequiredService<T>();

    /// <summary>Answers every cover URL with a few bytes; <see cref="FailingCover"/> fails in a way the download does not catch.</summary>
    private sealed class FakeCovers : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            request.RequestUri!.ToString() switch
            {
                FailingCover => throw new InvalidOperationException("cover broken"),
                // Like a timeout deeper down (e.g. a MusicBrainz call during the genre lookup): a cancellation
                // nobody asked for. The cover download itself does not catch this one.
                TimingOutCover => throw new OperationCanceledException("timed out"),
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }),
            };
    }

    private async Task<List<int>> SeedAsync(params string?[] coverUrls)
    {
        // No genre lookup (MusicBrainz, Last.fm): the test stays offline.
        var settings = Get<SettingsService>();
        var s = await settings.GetCopyAsync();
        s.UseGenres = false;
        await settings.SaveAsync(s);

        await using var db = await Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var group = new InboxGroup { RelativeFolder = "batch" };
        db.Groups.Add(group);
        var files = coverUrls.Select((cover, i) => new TrackFile
        {
            Group = group,
            RelativePath = $"batch/{i}.mp3",
            Status = FileStatus.NotInMusicBrainz,
            OriginalTags = new TagSet { Title = $"Song {i}", Artist = "Band" },
            ExternalMatches =
            [
                new ExternalMatch
                {
                    Source = "spotify", Id = $"s{i}", Title = $"Song {i}", Artists = ["Band"], Album = $"Album {i}",
                    CoverUrl = cover, Score = 1.0,
                },
            ],
        }).ToList();
        db.Files.AddRange(files);
        await db.SaveChangesAsync();
        return files.Select(f => f.Id).ToList();
    }

    private async Task<List<WorkItem>> DrainQueueAsync()
    {
        var queue = Get<WorkQueue>();
        var items = new List<WorkItem>();
        var count = queue.Count;
        if (count == 0) return items;
        await foreach (var item in queue.ReadAllAsync(CancellationToken.None))
        {
            items.Add(item);
            if (items.Count == count) break;
        }

        return items;
    }

    private async Task<Dictionary<int, TrackFile>> LoadAsync()
    {
        await using var db = await Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        return await db.Files.AsNoTracking().ToDictionaryAsync(f => f.Id);
    }

    [Fact]
    public async Task Every_file_is_saved_and_queued_for_writing_once()
    {
        var ids = await SeedAsync("https://covers.test/0.jpg", null, "https://covers.test/2.jpg");

        var (applied, problems) = await Get<ReviewService>().ApplyPerfectExternalAsync("spotify");

        Assert.Equal(3, applied);
        Assert.Empty(problems);
        var files = await LoadAsync();
        foreach (var id in ids)
        {
            Assert.Equal(FileStatus.Approved, files[id].Status);
            Assert.Equal(DecisionSource.External, files[id].Decision);
            Assert.Equal("spotify", files[id].ExternalSource);
        }

        Assert.NotNull(files[ids[0]].ManualCoverFile);
        Assert.Null(files[ids[1]].ManualCoverFile);
        var queued = await DrainQueueAsync();
        Assert.Equal(ids.Select(id => new WorkItem(WorkKind.WriteFile, id)), queued.OrderBy(w => w.Id));
        Assert.Null(Get<PipelineNotifier>().CurrentActivity);
    }

    [Fact]
    public async Task A_failing_file_is_reported_and_the_others_are_still_taken_over()
    {
        var ids = await SeedAsync("https://covers.test/0.jpg", FailingCover, "https://covers.test/2.jpg");

        var (applied, problems) = await Get<ReviewService>().ApplyPerfectExternalAsync("spotify");

        Assert.Equal(2, applied);
        var problem = Assert.Single(problems);
        Assert.Contains("cover broken", problem);

        var files = await LoadAsync();
        Assert.Equal(FileStatus.Approved, files[ids[0]].Status);
        Assert.Equal(FileStatus.Approved, files[ids[2]].Status);
        // The failing file is not saved: still waiting in the review queue, untouched.
        Assert.Equal(FileStatus.NotInMusicBrainz, files[ids[1]].Status);
        Assert.Equal(DecisionSource.None, files[ids[1]].Decision);
        Assert.Null(files[ids[1]].FinalTags);

        var queued = await DrainQueueAsync();
        Assert.Equal([ids[0], ids[2]], queued.Select(w => w.Id).Order());
        Assert.Null(Get<PipelineNotifier>().CurrentActivity);
    }

    [Fact]
    public async Task A_timeout_in_one_file_does_not_end_the_takeover()
    {
        var ids = await SeedAsync(TimingOutCover, "https://covers.test/1.jpg");

        var (applied, problems) = await Get<ReviewService>().ApplyPerfectExternalAsync("spotify");

        Assert.Equal(1, applied);
        Assert.Contains("timed out", Assert.Single(problems));
        var files = await LoadAsync();
        Assert.Equal(FileStatus.NotInMusicBrainz, files[ids[0]].Status);
        Assert.Equal(FileStatus.Approved, files[ids[1]].Status);
        Assert.Equal([ids[1]], (await DrainQueueAsync()).Select(w => w.Id));
        Assert.Null(Get<PipelineNotifier>().CurrentActivity);
    }
}
