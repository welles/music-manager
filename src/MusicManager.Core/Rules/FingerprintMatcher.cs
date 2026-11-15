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

    /// <summary>Values per section for <see cref="Compare"/>: 80 ≈ 10 s.</summary>
    public const int SectionValues = 80;

    /// <summary>
    /// Whole-track comparison: the similarity at the best alignment, plus the weakest 10-second section. A plain
    /// re-encode stays high everywhere; a voice or other sound laid over the music (a commentary track) pulls
    /// sections down even where the average still looks close.
    /// </summary>
    public static AudioComparison Compare(uint[] a, uint[] b)
    {
        var (overall, offset) = (0.0, 0);
        for (var o = -MaxOffset; o <= MaxOffset; o++)
        {
            var s = SimilarityAt(a, b, o, 0, int.MaxValue);
            if (s > overall) (overall, offset) = (s, o);
        }

        var startA = Math.Max(0, offset);
        var startB = Math.Max(0, -offset);
        var overlap = Math.Max(0, Math.Min(a.Length - startA, b.Length - startB));
        var (weakest, weakestAt) = (overall, 0);
        for (var k = 0; k + SectionValues <= overlap; k += SectionValues)
        {
            var s = SimilarityAt(a, b, offset, k, SectionValues);
            if (s < weakest) (weakest, weakestAt) = (s, k);
        }

        return new AudioComparison(overall, weakest, TimeSpan.FromSeconds((startA + weakestAt) * SecondsPerValue));
    }

    /// <summary>Audio time one raw value stands for.</summary>
    public const double SecondsPerValue = 0.1238;

    private static double SimilarityAt(uint[] a, uint[] b, int offset, int from, int count)
    {
        var startA = Math.Max(0, offset) + from;
        var startB = Math.Max(0, -offset) + from;
        var n = Math.Min(count, Math.Min(a.Length - startA, b.Length - startB));
        if (n < Math.Min(count, MinOverlap)) return 0;
        var errors = 0;
        for (var i = 0; i < n; i++)
            errors += BitOperations.PopCount(a[startA + i] ^ b[startB + i]);
        return 1.0 - errors / (32.0 * n);
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

/// <summary>Result of <see cref="FingerprintMatcher.Compare"/>: average similarity and the weakest section (with where it starts).</summary>
public sealed record AudioComparison(double Overall, double Weakest, TimeSpan WeakestAt);
