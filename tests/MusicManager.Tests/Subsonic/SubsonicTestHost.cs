using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MusicManager.Core.Data;
using MusicManager.Core.Model;
using MusicManager.Core.Pipeline;
using MusicManager.Core.Services;

namespace MusicManager.Tests.Subsonic;

/// <summary>
/// The web app against temp folders, without the app's background services (no inbox polling,
/// library scans, fingerprints or network lookups); tests drive the indexer themselves.
/// </summary>
public sealed class SubsonicTestHost : WebApplicationFactory<Program>
{
    public const string Key = "test-api-key";

    private readonly string? _apiKey;

    public SubsonicTestHost() : this(Key)
    {
    }

    public SubsonicTestHost(string? apiKey)
    {
        _apiKey = apiKey;
        Root = Path.Combine(Path.GetTempPath(), $"mm-subsonic-{Guid.NewGuid():N}");
        foreach (var dir in new[] { LibraryRoot, Path.Combine(Root, "inbox"), Path.Combine(Root, "trash"), DataRoot })
            Directory.CreateDirectory(dir);
    }

    public string Root { get; }
    public string LibraryRoot => Path.Combine(Root, "music");
    public string DataRoot => Path.Combine(Root, "data");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("MusicManager:InboxPath", Path.Combine(Root, "inbox"));
        builder.UseSetting("MusicManager:LibraryPath", LibraryRoot);
        builder.UseSetting("MusicManager:TrashPath", Path.Combine(Root, "trash"));
        builder.UseSetting("MusicManager:DataPath", DataRoot);
        builder.UseSetting("MusicManager:SubsonicApiKey", _apiKey ?? "");
        builder.ConfigureTestServices(services =>
        {
            var core = typeof(LibraryIndexer).Assembly;
            var own = services.Where(d => d.ServiceType == typeof(IHostedService)
                                          && (d.ImplementationType?.Assembly == core || d.ImplementationFactory?.Method.DeclaringType?.Assembly == core))
                .ToList();
            foreach (var d in own) services.Remove(d);
        });
    }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>Calls the API with the test key and returns the "subsonic-response" object.</summary>
    public async Task<JsonElement> CallAsync(string endpoint, string query = "", bool auth = true)
    {
        var url = $"/rest/{endpoint}?{(auth ? $"apiKey={Key}&" : "")}f=json&{query}";
        return await ReadAsync(await CreateClient().GetAsync(url));
    }

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.MediaType);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        return doc.GetProperty("subsonic-response");
    }

    /// <summary>Adds index rows directly (no files) and signals the change.</summary>
    public async Task SeedAsync(IEnumerable<LibraryEntry> entries)
    {
        await using (var db = await Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
        {
            db.Library.AddRange(entries);
            await db.SaveChangesAsync();
        }

        Get<LibraryIndexer>().OnChanged();
    }

    /// <summary>Copies the fixture into the library and tags it.</summary>
    public string AddFile(string relativePath, TagSet tags)
    {
        var path = Path.Combine(LibraryRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.m4a"), path, true);
        TagIo.WriteM4a(path, tags, null, "musicbrainz");
        return path;
    }

    public Task ScanAsync() => Get<LibraryIndexer>().ScanAsync(CancellationToken.None);

    public async Task<LibraryEntry?> EntryAsync(string relativePath)
    {
        await using var db = await Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        return await db.Library.AsNoTracking().FirstOrDefaultAsync(e => e.RelativePath == relativePath);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
            // SQLite may still hold the file briefly; the temp folder is cleaned up eventually.
        }
    }
}
