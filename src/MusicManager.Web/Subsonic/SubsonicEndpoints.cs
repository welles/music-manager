using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;
using MusicManager.Core.Configuration;

namespace MusicManager.Web.Subsonic;

/// <summary>
/// Minimal OpenSubsonic API under <c>/rest/{name}</c> (also <c>{name}.view</c>), so players such as
/// Symfonium can use the library. JSON only, authentication with the OpenSubsonic API key only.
/// </summary>
public static partial class SubsonicEndpoints
{
    public const string ApiVersion = "1.16.1";
    public const string ServerType = "music-manager";

    public delegate Task<IResult> Handler(SubsonicRequest request);

    /// <summary>Endpoints by name (case-insensitive, without ".view").</summary>
    private static readonly Dictionary<string, Handler> Handlers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Endpoints that work without authentication.</summary>
    private static readonly HashSet<string> Public = new(StringComparer.OrdinalIgnoreCase) { "getOpenSubsonicExtensions" };

    public static readonly string ServerVersion = ReadServerVersion();

    static SubsonicEndpoints()
    {
        MapSystem();
        MapBrowsing();
        MapMedia();
    }

    private static void Add(string name, Handler handler) => Handlers[name] = handler;

    public static void MapSubsonicEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/rest/{name}", ["GET", "HEAD", "POST"], HandleAsync)
            .DisableAntiforgery()
            .ExcludeFromDescription();
    }

    private static async Task<IResult> HandleAsync(string name, HttpContext http)
    {
        // Errors and empty responses (e.g. 416 on a range request) must not be replaced by the HTML error page.
        if (http.Features.Get<IStatusCodePagesFeature>() is { } statusPages) statusPages.Enabled = false;

        if (name.EndsWith(".view", StringComparison.OrdinalIgnoreCase)) name = name[..^5];
        var form = http.Request.HasFormContentType ? await http.Request.ReadFormAsync(http.RequestAborted) : null;
        var request = new SubsonicRequest(http, form);
        try
        {
            if (!Handlers.TryGetValue(name, out var handler))
                return Failed(SubsonicErrors.Generic, $"Unknown endpoint: {name}");
            if (!Public.Contains(name))
                Authenticate(request);
            return await handler(request);
        }
        catch (SubsonicException ex)
        {
            return Failed(ex.Code, ex.Message);
        }
        catch (Exception ex) when (!http.RequestAborted.IsCancellationRequested)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SubsonicEndpoints))
                .LogError(ex, "Subsonic call {Name} failed", name);
            return Failed(SubsonicErrors.Generic, ex.Message);
        }
    }

    /// <summary>OpenSubsonic API key authentication; u/p/t/s are not supported.</summary>
    private static void Authenticate(SubsonicRequest request)
    {
        var apiKey = request.Get("apiKey");
        var legacy = new[] { "u", "p", "t", "s" }.Any(request.Has);
        if (apiKey != null && legacy)
            throw new SubsonicException(SubsonicErrors.ConflictingAuth, "Multiple conflicting authentication mechanisms provided");
        if (apiKey == null)
        {
            throw legacy
                ? new SubsonicException(SubsonicErrors.AuthMechanismNotSupported, "Provided authentication mechanism not supported, use an API key")
                : SubsonicException.Missing("apiKey");
        }

        var configured = request.Service<IOptions<MusicManagerOptions>>().Value.SubsonicApiKey;
        if (string.IsNullOrEmpty(configured) || !KeyEquals(apiKey, configured))
            throw new SubsonicException(SubsonicErrors.InvalidApiKey, "Invalid API key");
    }

    /// <summary>Constant-time comparison (hashes first, so the length does not leak either).</summary>
    private static bool KeyEquals(string given, string expected) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    // --- Envelope -----------------------------------------------------------------------------

    private static Dictionary<string, object?> Envelope(string status) => new()
    {
        ["status"] = status,
        ["version"] = ApiVersion,
        ["type"] = ServerType,
        ["serverVersion"] = ServerVersion,
        ["openSubsonic"] = true,
    };

    /// <summary>Successful response, optionally with one payload object under <paramref name="key"/>.</summary>
    public static IResult Ok(string? key = null, object? payload = null)
    {
        var body = Envelope("ok");
        if (key != null) body[key] = payload;
        return Results.Json(new Dictionary<string, object> { ["subsonic-response"] = body }, SubsonicJson.Options);
    }

    public static IResult Failed(int code, string message)
    {
        var body = Envelope("failed");
        body["error"] = new SubsonicError { Code = code, Message = message };
        return Results.Json(new Dictionary<string, object> { ["subsonic-response"] = body }, SubsonicJson.Options);
    }

    private static string ReadServerVersion()
    {
        var version = typeof(SubsonicEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? typeof(SubsonicEndpoints).Assembly.GetName().Version?.ToString() ?? "0";
        var plus = version.IndexOf('+');
        return plus > 0 ? version[..plus] : version;
    }
}
