using Microsoft.Extensions.Options;
using MusicManager.Core.Configuration;
using MusicManager.Core.Services;

namespace MusicManager.Web;

/// <summary>
/// Spotify login for the playlists page: <c>/spotify/connect</c> sends the browser to Spotify, which comes back to
/// <c>/spotify/callback</c> (that address must be registered with the app in the Spotify dashboard).
/// </summary>
public static class SpotifyEndpoints
{
    public static void MapSpotifyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/spotify/connect", (HttpContext http, SpotifyUserClient spotify, IOptions<MusicManagerOptions> options) =>
            spotify.IsConfigured
                ? Results.Redirect(spotify.AuthorizeUrl(RedirectUri(http, options.Value)))
                : Results.Redirect("/playlists?spotify=" + Uri.EscapeDataString("Kein Spotify-Client konfiguriert (MusicManager__SpotifyClientId/Secret).")));

        app.MapGet("/spotify/callback", async (HttpContext http, string? code, string? state, string? error, SpotifyUserClient spotify,
            IOptions<MusicManagerOptions> options, CancellationToken ct) =>
        {
            if (error != null || code == null || state == null)
                return Results.Redirect("/playlists?spotify=" + Uri.EscapeDataString(error == "access_denied" ? "Anmeldung abgebrochen." : $"Spotify: {error ?? "keine Antwort"}"));
            try
            {
                await spotify.CompleteLoginAsync(code, state, RedirectUri(http, options.Value), ct);
                return Results.Redirect("/playlists?spotify=connected");
            }
            catch (SpotifyException ex)
            {
                return Results.Redirect("/playlists?spotify=" + Uri.EscapeDataString(ex.Message));
            }
        });
    }

    /// <summary>Configured, or this host's callback (behind the tunnel the forwarded headers give https and the public host).</summary>
    public static string RedirectUri(HttpContext http, MusicManagerOptions options) =>
        options.SpotifyRedirectUri is { Length: > 0 } configured ? configured : $"{http.Request.Scheme}://{http.Request.Host}/spotify/callback";
}
