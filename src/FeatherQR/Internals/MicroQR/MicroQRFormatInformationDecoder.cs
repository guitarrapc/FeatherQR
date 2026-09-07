namespace FeatherQR.Internals.MicroQR;

/// <summary>
/// Decodes the 15-bit Micro QR format information (symbol number + mask pattern).
/// </summary>
/// <remarks>
/// Inverse of <see cref="MicroQRConstants.GetFormatBits"/>.
/// Micro QR places a single format information copy (Standard QR has two redundant copies), so the raw 15 bits are matched against all 32 valid masked patterns (8 symbol numbers × 4 masks) by Hamming distance.
/// BCH(15,5) has minimum distance 7, so up to 3 bit errors are unambiguously correctable, a candidate is accepted only when its distance is ≤ 3. The XOR mask (0x4445) preserves pairwise distances, so the masked patterns keep the same correction radius.
/// </remarks>
internal static class MicroQRFormatInformationDecoder
{
    private const int MaxCorrectableBits = 3;

    // All 32 valid masked format patterns, index = symbolNumber(0-7) * 4 + mask(0-3).
    // Built from the encoder's own GetFormatBits so both sides always agree.
    private static readonly ushort[] candidates = BuildCandidates();

    private static ushort[] BuildCandidates()
    {
        var table = new ushort[32];
        for (var symbolNumber = 0; symbolNumber < 8; symbolNumber++)
        {
            MicroQRConstants.GetVersionAndEccFromSymbolNumber(symbolNumber, out var version, out var eccLevel);
            for (var mask = 0; mask < 4; mask++)
            {
                table[symbolNumber * 4 + mask] = MicroQRConstants.GetFormatBits(version, eccLevel, mask);
            }
        }
        return table;
    }

    /// <summary>
    /// Decodes format information from the raw 15-bit copy.
    /// </summary>
    /// <param name="raw">Raw 15 bits read around the finder pattern (bit 14 first placed module).</param>
    /// <param name="version">Decoded Micro QR version (M1-M4).</param>
    /// <param name="eccLevel">Decoded error correction level.</param>
    /// <param name="maskPattern">Decoded mask pattern (0-3).</param>
    /// <returns>False when the copy is beyond correction distance of every valid pattern.</returns>
    public static bool TryDecode(ushort raw, out MicroQRVersion version, out MicroQREccLevel eccLevel, out int maskPattern)
    {
        var best = 0;
        var bestDistance = int.MaxValue;
        for (var i = 0; i < candidates.Length; i++)
        {
            var distance = PopCount((ushort)(raw ^ candidates[i]));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
                if (distance == 0)
                    break;
            }
        }

        if (bestDistance > MaxCorrectableBits)
        {
            version = default;
            eccLevel = default;
            maskPattern = -1;
            return false;
        }

        MicroQRConstants.GetVersionAndEccFromSymbolNumber(best >> 2, out version, out eccLevel);
        maskPattern = best & 3;
        return true;
    }

    private static int PopCount(ushort value)
    {
        // 16-bit SWAR popcount (netstandard2.0 has no BitOperations.PopCount)
        var v = (uint)value;
        v -= (v >> 1) & 0x5555;
        v = (v & 0x3333) + ((v >> 2) & 0x3333);
        v = (v + (v >> 4)) & 0x0F0F;
        return (int)((v * 0x0101) >> 8 & 0x1F);
    }
}
