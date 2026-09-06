using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// Global image binarization, shared by the Standard QR and Micro QR image decoders.
/// </summary>
internal static class Binarizer
{
    /// <summary>
    /// Otsu's method: picks the threshold that maximizes between-class variance of the luminance histogram.
    /// Suits Tier-1 inputs with clear bimodal contrast.
    /// </summary>
    /// <remarks>
    /// The histogram fill aggregates runs: a per-pixel `histogram[value]++` walk serializes on store-forwarding whenever consecutive pixels hit the same bin, and QR-like images (long runs of two dominant values) are the worst case.
    /// Reading 8 pixels as one ulong and testing uniformity with a byte rotation turns a whole uniform group into a single `+= 8`; non-uniform groups (module boundaries, photos) fall back to 8 increments, measured ~8-10x on QR-like inputs, break-even to modestly slower on uniform random noise.
    /// The result is byte-identical either way, and bin order is irrelevant to a histogram, so the walk is endian-safe.
    /// </remarks>
    internal static byte ComputeOtsuThreshold(ReadOnlySpan<byte> luminance)
    {
        Span<int> histogram = stackalloc int[256];
        histogram.Clear();

        var offset = 0;
        ref var p = ref MemoryMarshal.GetReference(luminance);
        for (; offset + 8 <= luminance.Length; offset += 8)
        {
            var v = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, offset));
            // All 8 bytes equal ⟺ rotating by one byte is a fixed point
            // (netstandard has no BitOperations.RotateRight; the JIT emits ror)
            if (v == ((v >> 8) | (v << 56)))
            {
                histogram[(byte)v] += 8;
                continue;
            }

            histogram[(byte)v]++;
            histogram[(byte)(v >> 8)]++;
            histogram[(byte)(v >> 16)]++;
            histogram[(byte)(v >> 24)]++;
            histogram[(byte)(v >> 32)]++;
            histogram[(byte)(v >> 40)]++;
            histogram[(byte)(v >> 48)]++;
            histogram[(byte)(v >> 56)]++;
        }
        for (; offset < luminance.Length; offset++)
        {
            histogram[luminance[offset]]++;
        }

        var total = luminance.Length;
        long sumAll = 0;
        for (var i = 0; i < 256; i++)
        {
            sumAll += (long)i * histogram[i];
        }

        long sumBackground = 0;
        long weightBackground = 0;
        var bestVariance = -1.0;
        var bestThreshold = 128;

        for (var t = 0; t < 256; t++)
        {
            weightBackground += histogram[t];
            if (weightBackground == 0)
                continue;
            var weightForeground = total - weightBackground;
            if (weightForeground == 0)
                break;

            sumBackground += (long)t * histogram[t];
            var meanBackground = (double)sumBackground / weightBackground;
            var meanForeground = (double)(sumAll - sumBackground) / weightForeground;
            var diff = meanBackground - meanForeground;
            var variance = weightBackground * (double)weightForeground * diff * diff;

            if (variance > bestVariance)
            {
                bestVariance = variance;
                bestThreshold = t + 1; // dark: luminance < threshold
            }
        }

        return (byte)Math.Min(bestThreshold, 255);
    }
}
