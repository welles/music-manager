using System.Numerics;

namespace MusicManager.Core.Rules;

/// <summary>
/// Compares two raw Chromaprint fingerprints (fpcalc -raw). Each value covers ~0.124 s of audio
/// as 32 sub-fingerprint bits; similarity is 1 − bit error rate at the best alignment.
/// Same recording (also re-encoded) typically scores above 0.85, different songs around 0.5.
/// </summary>
public static class FingerprintMatcher
{
    /// <summary>Alignment search window: ±40 values ≈ ±5 seconds.</summary>
    public const int MaxOffset = 40;

    /// <summary>At least this many overlapping values (≈ 12 s) are needed for a meaningful result.</summary>
    public const int MinOverlap = 100;

    public static double Similarity(uint[] a, uint[] b, int maxOffset = MaxOffset)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var best = 0.0;
        for (var offset = -maxOffset; offset <= maxOffset; offset++)
        {
            var startA = Math.Max(0, offset);
            var startB = Math.Max(0, -offset);
            var overlap = Math.Min(a.Length - startA, b.Length - startB);
            if (overlap < MinOverlap) continue;

            var errors = 0;
            for (var i = 0; i < overlap; i++)
                errors += BitOperations.PopCount(a[startA + i] ^ b[startB + i]);

            var similarity = 1.0 - errors / (32.0 * overlap);
            if (similarity > best) best = similarity;
        }

        return best;
    }

    public static string Encode(uint[] values)
    {
        var bytes = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    public static uint[] Decode(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded)) return [];
        var bytes = Convert.FromBase64String(encoded);
        var values = new uint[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, values, 0, values.Length * 4);
        return values;
    }
}
