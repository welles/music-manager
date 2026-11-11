using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MusicManager.Core.Configuration;
using MusicManager.Core.Data;

namespace MusicManager.Core.Services;

/// <summary>
/// Sends Web Push messages: VAPID authentication (RFC 8292) and aes128gcm payload encryption (RFC 8291),
/// with nothing but the .NET crypto primitives. The VAPID key pair is created once and kept in the data folder.
/// </summary>
public sealed class WebPushSender
{
    /// <summary>Contact the push services may use; a URL, so no personal address ends up in requests.</summary>
    private const string Subject = "https://github.com/welles/music-manager";

    private readonly HttpClient _http;
    private readonly ILogger<WebPushSender> _logger;
    private readonly ECDsa _vapid;

    public WebPushSender(IHttpClientFactory httpFactory, IOptions<MusicManagerOptions> options, ILogger<WebPushSender> logger)
    {
        _http = httpFactory.CreateClient("webpush");
        _logger = logger;
        _vapid = LoadOrCreateKey(Path.Combine(options.Value.DataPath, "vapid.pem"));
        PublicKey = Base64Url(PublicKeyBytes(_vapid));
    }

    /// <summary>VAPID public key (base64url, uncompressed point) – the browser subscribes with it.</summary>
    public string PublicKey { get; }

    public enum Result { Sent, Gone, Failed }

    public async Task<Result> SendAsync(PushSubscription subscription, object payload, CancellationToken ct)
    {
        var body = Encrypt(JsonSerializer.SerializeToUtf8Bytes(payload), FromBase64Url(subscription.P256dh), FromBase64Url(subscription.Auth));

        using var request = new HttpRequestMessage(HttpMethod.Post, subscription.Endpoint) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.Add("TTL", "86400");
        request.Headers.Add("Urgency", "normal");
        request.Headers.TryAddWithoutValidation("Authorization", $"vapid t={VapidToken(new Uri(subscription.Endpoint))}, k={PublicKey}");

        try
        {
            using var response = await _http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return Result.Sent;
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return Result.Gone;

            _logger.LogWarning("Push to {Host} failed: {Status} {Body}", request.RequestUri!.Host, (int)response.StatusCode,
                await response.Content.ReadAsStringAsync(ct));
            return Result.Failed;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Push to {Host} failed", request.RequestUri!.Host);
            return Result.Failed;
        }
    }

    // --- RFC 8291 ------------------------------------------------------------------------

    /// <summary>Encrypts one record: salt | rs | idlen | keyid (sender public key) | ciphertext+tag.</summary>
    public static byte[] Encrypt(byte[] plaintext, byte[] uaPublic, byte[] authSecret, ECDiffieHellman? senderKey = null, byte[]? salt = null)
    {
        using var sender = senderKey ?? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var receiver = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = uaPublic[1..33], Y = uaPublic[33..65] },
        });

        var asPublic = PublicKeyBytes(sender);
        var ecdhSecret = sender.DeriveRawSecretAgreement(receiver.PublicKey);

        var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), uaPublic, asPublic);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, authSecret, keyInfo);

        salt ??= RandomNumberGenerator.GetBytes(16);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        // Single record: the 0x02 delimiter marks the last record, no padding.
        var padded = Concat(plaintext, [2]);
        var cipher = new byte[padded.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(cek, 16))
            aes.Encrypt(nonce, padded, cipher, tag);

        var header = new byte[16 + 4 + 1];
        salt.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), 4096);
        header[20] = (byte)asPublic.Length;
        return Concat(header, asPublic, cipher, tag);
    }

    // --- RFC 8292 ------------------------------------------------------------------------

    private string VapidToken(Uri endpoint)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { typ = "JWT", alg = "ES256" }));
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            aud = endpoint.GetLeftPart(UriPartial.Authority),
            exp = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds(),
            sub = Subject,
        }));
        var unsigned = $"{header}.{claims}";
        // .NET signs in the IEEE P1363 format (r|s) that JWS expects.
        var signature = _vapid.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256);
        return $"{unsigned}.{Base64Url(signature)}";
    }

    private static ECDsa LoadOrCreateKey(string path)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(path))
        {
            key.ImportFromPem(File.ReadAllText(path));
            return key;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, key.ExportECPrivateKeyPem());
        return key;
    }

    private static byte[] PublicKeyBytes(AsymmetricAlgorithm key)
    {
        var p = key switch
        {
            ECDsa ecdsa => ecdsa.ExportParameters(false),
            ECDiffieHellman ecdh => ecdh.ExportParameters(false),
            _ => throw new ArgumentException("EC key expected", nameof(key)),
        };
        return Concat([4], p.Q.X!, p.Q.Y!);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var p in parts)
        {
            p.CopyTo(result, offset);
            offset += p.Length;
        }

        return result;
    }

    public static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }
}
