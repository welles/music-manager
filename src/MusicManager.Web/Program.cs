using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using MudBlazor.Services;
using MusicManager.Core;
using MusicManager.Core.Pipeline;
using MusicManager.Web;
using MusicManager.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddScoped<PlayerService>();
builder.Services.AddMusicManager(builder.Configuration);

// Keep Data Protection keys (antiforgery, circuits) on the data volume.
var dataPath = builder.Configuration.GetSection("MusicManager")["DataPath"] ?? "/data";
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys")))
    .SetApplicationName("MusicManager");

// Runs behind a Cloudflare Tunnel; authentication is done by Cloudflare Access.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

await app.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync(CancellationToken.None);

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapGet("/healthz", () => Results.Ok("ok"));

// Cover of a file: embedded picture of the filed file, otherwise of the original in the inbox.
app.MapGet("/api/files/{id:int}/cover", async (int id, string? which, Microsoft.EntityFrameworkCore.IDbContextFactory<MusicManager.Core.Data.AppDbContext> dbFactory, Storage storage, HttpContext http) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var file = await db.Files.FindAsync(id);
    byte[]? data = null;
    if (file != null)
    {
        var result = new[]
        {
            file.TargetPath != null && file.Status == MusicManager.Core.Data.FileStatus.Done ? storage.LibraryPath(file.TargetPath) : null,
            file.ManualCoverFile,
        };
        // The original is in the inbox, or in the trash once the file was filed.
        var original = new[] { storage.InboxPath(file.RelativePath), file.TrashPath };
        var paths = which switch
        {
            "original" => original,
            "result" => result,
            _ => result.Concat(original).ToArray(),
        };
        foreach (var p in paths.Where(p => p != null && File.Exists(p)))
        {
            data = p == file.ManualCoverFile ? await File.ReadAllBytesAsync(p!) : MusicManager.Core.Services.TagIo.ReadEmbeddedCover(p!);
            if (data != null) break;
        }
    }

    // With ?v= (the file's last change) the URL changes whenever the cover can change.
    http.Response.Headers.CacheControl = http.Request.Query.ContainsKey("v") ? "private, max-age=604800, immutable" : "private, max-age=300";
    if (data == null)
    {
        const string placeholder = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><rect width=\"24\" height=\"24\" fill=\"#9e9e9e33\"/><path fill=\"#9e9e9e\" d=\"M12 3v10.55A4 4 0 1 0 14 17V7h4V3z\"/></svg>";
        return Results.Content(placeholder, "image/svg+xml");
    }

    var mime = data.Length > 3 && data[0] == 0x89 && data[1] == 0x50 ? "image/png" : "image/jpeg";
    return Results.File(data, mime);
});

// Processing state, e.g. for scripts: pending = files still waiting to be processed or written.
app.MapGet("/api/status", async (Microsoft.EntityFrameworkCore.IDbContextFactory<MusicManager.Core.Data.AppDbContext> dbFactory, WorkQueue queue, PipelineNotifier notifier) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var counts = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToDictionaryAsync(
        db.Files.GroupBy(f => f.Status).Select(g => new { g.Key, Count = g.Count() }), x => x.Key.ToString(), x => x.Count);
    string[] pendingStates = ["Waiting", "Queued", "Processing", "Approved"];
    return Results.Ok(new
    {
        pending = pendingStates.Sum(s => counts.GetValueOrDefault(s)),
        queue = queue.Count,
        activity = notifier.CurrentActivity,
        counts,
    });
});
app.MapAudioEndpoints();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
