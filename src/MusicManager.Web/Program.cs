using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using MudBlazor.Services;
using MusicManager.Core;
using MusicManager.Core.Pipeline;
using MusicManager.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
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
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
