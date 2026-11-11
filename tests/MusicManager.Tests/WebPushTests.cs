using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MusicManager.Core.Services;

namespace MusicManager.Tests;

public class WebPushTests
{
    /// <summary>Decrypts like the browser does (RFC 8291, receiver side).</summary>
    private static byte[] Decrypt(byte[] body, ECDiffieHellman receiver, byte[] authSecret)
    {
        var salt = body[..16];
        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16, 4)));
        var idLength = body[20];
        var senderPublic = body[21..(21 + idLength)];
        var cipherAndTag = body[(21 + idLength)..];

        var p = receiver.ExportParameters(false);
        var receiverPublic = new byte[] { 4 }.Concat(p.Q.X!).Concat(p.Q.Y!).ToArray();
        using var sender = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = senderPublic[1..33], Y = senderPublic[33..65] },
        });
        var ecdh = receiver.DeriveRawSecretAgreement(sender.PublicKey);
        var keyInfo = Encoding.ASCII.GetBytes("WebPush: info\0").Concat(receiverPublic).Concat(senderPublic).ToArray();
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdh, 32, authSecret, keyInfo);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        var cipher = cipherAndTag[..^16];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(cek, 16);
        aes.Decrypt(nonce, cipher, cipherAndTag[^16..], plain);
        Assert.Equal(2, plain[^1]);
        return plain[..^1];
    }

    [Fact]
    public void EncryptedPayloadCanBeDecryptedByTheBrowser()
    {
        using var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p = browser.ExportParameters(false);
        var browserPublic = new byte[] { 4 }.Concat(p.Q.X!).Concat(p.Q.Y!).ToArray();
        var auth = RandomNumberGenerator.GetBytes(16);

        var message = Encoding.UTF8.GetBytes("""{"title":"Music Manager","body":"3 Lieder zu prüfen"}""");
        var body = WebPushSender.Encrypt(message, browserPublic, auth);

        Assert.Equal(16 + 4 + 1 + 65 + message.Length + 1 + 16, body.Length);
        Assert.Equal(message, Decrypt(body, browser, auth));
    }

    [Fact]
    public void Base64UrlRoundTrip()
    {
        var data = RandomNumberGenerator.GetBytes(65);
        var text = WebPushSender.Base64Url(data);
        Assert.DoesNotContain('=', text);
        Assert.Equal(data, WebPushSender.FromBase64Url(text));
    }
}
