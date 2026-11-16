using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MusicManager.Core.Data;
using MusicManager.Core.Pipeline;
using MusicManager.Core.Services;
using MusicManager.Tests.Subsonic;

namespace MusicManager.Tests;

/// <summary>
/// Downloads requested from MusicFab (MusicFab.Manager in the MusicFab container): the request goes to its API with the
/// key, the job's state is followed until it ends, and the files' inbox group is known.
/// </summary>
public sealed class MusicFabRequestTests : IDisposable
{
    private const string Key = "musicfab-key";
    private const string Album = "https://music.youtube.com/playlist?list=OLAK5uy_mF5pKC9wC88SaXYNwmEyJwCoCqQ9FDnxU";

    private readonly SubsonicTestHost _base = new();
    private readonly WebApplicationFactory<Program> _host;
    private readonly FakeMusicFab _musicFab = new();

    public MusicFabRequestTests()
    {
        _host = _base.WithWebHostBuilder(b =>
        {
            b.UseSetting("MusicManager:MusicFabUrl", "http://musicfab:5810/");
            b.UseSetting("MusicManager:MusicFabApiKey", Key);
            b.UseSetting("MusicManager:MusicFabAccessClientId", "access-id.access");
            b.UseSetting("MusicManager:MusicFabAccessClientSecret", "access-secret");
            b.ConfigureTestServices(s => s.AddHttpClient<MusicFabClient>().ConfigurePrimaryHttpMessageHandler(() => _musicFab));
        });
    }

    public void Dispose()
    {
        _host.Dispose();
        _base.Dispose();
    }

    private MusicFabRequests Requests => _host.Services.GetRequiredService<MusicFabRequests>();

    private async Task<MusicFabRequest> StoredAsync(int id)
    {
        await using var db = await _host.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        return await db.MusicFabRequests.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    [Fact]
    public async Task ARequestGoesToMusicFabWithTheKey()
    {
        var request = await Requests.RequestAsync($" {Album} ", "Spotify-Playlist „Mix“", CancellationToken.None);

        Assert.Equal(Album, _musicFab.LastUrl);
        Assert.Equal(Key, _musicFab.LastKey);
        Assert.Equal(("access-id.access", "access-secret"), _musicFab.LastAccess);
        var stored = await StoredAsync(request.Id);
        Assert.Equal(("Queued", Album, "Spotify-Playlist „Mix“"), (stored.State, stored.Url, stored.Origin));
        Assert.Equal(_musicFab.Jobs.Keys.Single(), stored.JobId);
    }

    [Fact]
    public async Task TheJobIsFollowedUntilItEnds()
    {
        var request = await Requests.RequestAsync(Album, null, CancellationToken.None);
        var job = _musicFab.Jobs[request.JobId];

        job.State = "Downloading";
        job.Title = "Album - Action Cuts";
        job.Tracks = 27;
        job.Progress = 40;
        job.Files = ["/output/musicfab/Album - Action Cuts/Kevin MacLeod-A Mission.m4a"];
        Assert.True(await Requests.RefreshAsync(CancellationToken.None));
        var running = await StoredAsync(request.Id);
        Assert.Equal(("Downloading", 40.0, 1, "musicfab/Album - Action Cuts"), (running.State, running.Progress, running.Files, running.InboxFolder));
        Assert.Null(running.FinishedAt);

        job.State = "Failed";
        job.Error = "1 of 27 tracks failed: MusicFab gave up on it";
        job.Files = [.. job.Files, "/output/musicfab/Album - Action Cuts/Kevin MacLeod-Action.m4a"];
        Assert.False(await Requests.RefreshAsync(CancellationToken.None));
        var done = await StoredAsync(request.Id);
        Assert.Equal(("Failed", 2, 27, "Album - Action Cuts"), (done.State, done.Files, done.Tracks, done.Title));
        Assert.Equal(job.Error, done.Error);
        Assert.NotNull(done.FinishedAt);
    }

    [Fact]
    public async Task AJobMusicFabForgotEnds()
    {
        var request = await Requests.RequestAsync(Album, null, CancellationToken.None);
        _musicFab.Jobs.Clear();

        Assert.False(await Requests.RefreshAsync(CancellationToken.None));
        var lost = await StoredAsync(request.Id);
        Assert.Equal("Lost", lost.State);
        Assert.True(lost.IsFinished);
    }

    [Fact]
    public async Task OnlyAWaitingJobCanBeCancelled()
    {
        var waiting = await Requests.RequestAsync(Album, null, CancellationToken.None);
        Assert.True(await Requests.CancelAsync(waiting.Id, CancellationToken.None));
        Assert.Equal("Cancelled", (await StoredAsync(waiting.Id)).State);

        var started = await Requests.RequestAsync("https://music.youtube.com/watch?v=jNQXAC9IVRw", null, CancellationToken.None);
        _musicFab.Jobs[started.JobId].State = "Downloading";
        Assert.False(await Requests.CancelAsync(started.Id, CancellationToken.None));
    }

    [Fact]
    public async Task RefusalsSayWhy()
    {
        var invalid = await Assert.ThrowsAsync<MusicFabException>(() => Requests.RequestAsync("ftp://nope", null, CancellationToken.None));
        Assert.Contains("an absolute http(s) URL", invalid.Message);

        _musicFab.Key = "another-key";
        var key = await Assert.ThrowsAsync<MusicFabException>(() => Requests.RequestAsync(Album, null, CancellationToken.None));
        Assert.Contains("API-Key", key.Message);

        // Cloudflare Access sends a client without a valid service token to its login
        _musicFab.Key = Key;
        _musicFab.AccessRefused = true;
        var access = await Assert.ThrowsAsync<MusicFabException>(() => Requests.RequestAsync(Album, null, CancellationToken.None));
        Assert.Contains("Service-Token", access.Message);
    }

    [Fact]
    public async Task WithoutUrlAndKeyItIsOff()
    {
        var requests = _base.Get<MusicFabRequests>();

        Assert.False(requests.IsConfigured);
        Assert.False(await requests.RefreshAsync(CancellationToken.None));
        await Assert.ThrowsAsync<MusicFabException>(() => requests.RequestAsync(Album, null, CancellationToken.None));
    }

    [Theory]
    [InlineData("/output/musicfab/Album - Action Cuts/a.m4a", "musicfab/Album - Action Cuts")]
    [InlineData("/output/musicfab/Other/jawed-Me at the zoo.m4a", "musicfab/Other")]
    [InlineData("/output/musicfab/a.m4a", "musicfab")]
    [InlineData("/somewhere/else/a.m4a", null)]
    public void TheFilesNameTheirInboxGroup(string file, string? folder) =>
        Assert.Equal(folder, MusicFabRequests.InboxFolder([file]));

    /// <summary>MusicFab.Manager's API as the Music Manager uses it, with jobs the tests move along.</summary>
    private sealed class FakeMusicFab : HttpMessageHandler
    {
        public sealed class Job
        {
            public Guid Id { get; } = Guid.NewGuid();
            public string State { get; set; } = "Queued";
            public string? Title { get; set; }
            public int? Tracks { get; set; }
            public List<string> Files { get; set; } = [];
            public double? Progress { get; set; }
            public string? Error { get; set; }
        }

        public readonly Dictionary<Guid, Job> Jobs = [];
        public string Key = MusicFabRequestTests.Key;
        public string? LastUrl;
        public string? LastKey;
        public (string?, string?) LastAccess;
        public bool AccessRefused;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("musicfab:5810", request.RequestUri!.Authority);
            LastAccess = (Header(request, "CF-Access-Client-Id"), Header(request, "CF-Access-Client-Secret"));
            if (AccessRefused)
                return new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://team.cloudflareaccess.com/cdn-cgi/access/login") } };
            LastKey = Header(request, "X-Api-Key");
            if (LastKey != Key) return new HttpResponseMessage(HttpStatusCode.Unauthorized);

            var path = request.RequestUri.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/downloads")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                LastUrl = body.RootElement.GetProperty("url").GetString();
                if (!LastUrl!.StartsWith("http", StringComparison.Ordinal))
                    return Json(HttpStatusCode.BadRequest, new { title = "One or more validation errors occurred.", errors = new { url = new[] { "an absolute http(s) URL" } } });
                var job = new Job();
                Jobs[job.Id] = job;
                return Json(HttpStatusCode.Created, Dto(job));
            }

            var id = Guid.Parse(path.Split('/').Last());
            if (!Jobs.TryGetValue(id, out var found)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Delete)
            {
                if (found.State != "Queued") return Json(HttpStatusCode.Conflict, $"only queued jobs can be cancelled, this one is {found.State}");
                found.State = "Cancelled";
            }

            return Json(HttpStatusCode.OK, Dto(found));
        }

        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? values.Single() : null;

        // MusicFab.Manager leaves out null fields
        private static Dictionary<string, object> Dto(Job j)
        {
            var dto = new Dictionary<string, object> { ["id"] = j.Id, ["url"] = Album, ["state"] = j.State, ["taskIds"] = Array.Empty<string>(), ["files"] = j.Files };
            if (j.Title != null) dto["title"] = j.Title;
            if (j.Tracks != null) dto["tracks"] = j.Tracks;
            if (j.Progress != null) dto["progress"] = j.Progress;
            if (j.Error != null) dto["error"] = j.Error;
            return dto;
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
