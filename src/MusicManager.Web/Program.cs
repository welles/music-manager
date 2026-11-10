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
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
