using System.Globalization;
using Microsoft.Extensions.Primitives;

namespace MusicManager.Web.Subsonic;

/// <summary>Error that ends a Subsonic call with a failed envelope (HTTP 200, <c>status: "failed"</c>).</summary>
public sealed class SubsonicException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;

    public static SubsonicException Missing(string name) => new(SubsonicErrors.MissingParameter, $"Required parameter is missing: {name}");
    public static SubsonicException NotFound(string what) => new(SubsonicErrors.NotFound, $"{what} not found");
}

/// <summary>Error codes of the Subsonic / OpenSubsonic API.</summary>
public static class SubsonicErrors
{
    public const int Generic = 0;
    public const int MissingParameter = 10;
    public const int AuthMechanismNotSupported = 42;
    public const int ConflictingAuth = 43;
    public const int InvalidApiKey = 44;
    public const int NotAuthorized = 50;
    public const int NotFound = 70;
}

/// <summary>
/// One Subsonic call: parameters come from the query string and, for form POST, from the body
/// (query values first).
/// </summary>
public sealed class SubsonicRequest(HttpContext http, IFormCollection? form)
{
    public HttpContext Http { get; } = http;
    public IServiceProvider Services => Http.RequestServices;
    public CancellationToken Aborted => Http.RequestAborted;

    public T Service<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>All values of a parameter (e.g. repeated <c>id</c>).</summary>
    public IReadOnlyList<string> GetAll(string name)
    {
        var values = StringValues.Concat(Http.Request.Query[name], form?[name] ?? StringValues.Empty);
        return values.OfType<string>().ToList();
    }

    public string? Get(string name) => GetAll(name).FirstOrDefault();

    public bool Has(string name) => GetAll(name).Count > 0;

    public string Require(string name) => Get(name) is { Length: > 0 } v ? v : throw SubsonicException.Missing(name);

    public int? GetInt(string name)
    {
        var v = Get(name);
        if (string.IsNullOrWhiteSpace(v)) return null;
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i
            : throw new SubsonicException(SubsonicErrors.Generic, $"Invalid value for {name}: {v}");
    }

    /// <summary>Integer parameter clamped to <paramref name="min"/>..<paramref name="max"/>.</summary>
    public int GetInt(string name, int fallback, int min = 0, int max = int.MaxValue) => Math.Clamp(GetInt(name) ?? fallback, min, max);

    public int RequireInt(string name) => GetInt(name) ?? throw SubsonicException.Missing(name);

    /// <summary>All values of a repeated integer parameter.</summary>
    public IReadOnlyList<int> GetInts(string name) => GetAll(name).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v =>
        int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i
            : throw new SubsonicException(SubsonicErrors.Generic, $"Invalid value for {name}: {v}")).ToList();

    /// <summary>All values of a repeated long parameter.</summary>
    public IReadOnlyList<long> GetLongs(string name) => GetAll(name).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v =>
        long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
            ? l
            : throw new SubsonicException(SubsonicErrors.Generic, $"Invalid value for {name}: {v}")).ToList();

    public bool? GetBool(string name)
    {
        var v = Get(name);
        if (string.IsNullOrWhiteSpace(v)) return null;
        return bool.TryParse(v, out var b) ? b : throw new SubsonicException(SubsonicErrors.Generic, $"Invalid value for {name}: {v}");
    }
}
