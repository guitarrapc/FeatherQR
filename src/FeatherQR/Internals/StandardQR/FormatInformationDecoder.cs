namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// Decodes the 15-bit format information (ECC level + mask pattern).
/// </summary>
/// <remarks>
/// Inverse of <see cref="QRCodeConstants.GetFormatBits"/>.
/// Instead of running BCH(15,5) syndrome decoding, the raw 15 bits are matched against all 32 valid masked format patterns (4 ECC levels × 8 masks) by Hamming distance.
/// BCH(15,5) has minimum distance 7, so up to 3 bit errors are unambiguously correctable, a candidate is accepted only when its distance is ≤ 3 and strictly better than any other.
/// </remarks>
internal static class FormatInformationDecoder
{
    private const int MaxCorrectableBits = 3;

    // All 32 valid masked format patterns, index = eccLevel(0-3) * 8 + maskPattern(0-7).
    // Built from the encoder's own GetFormatBits so both sides always agree.
    private static readonly ushort[] candidates = BuildCandidates();

    private static ushort[] BuildCandidates()
    {
        var table = new ushort[32];
        for (var level = 0; level < 4; level++)
        {
            for (var mask = 0; mask < 8; mask++)
            {
                table[level * 8 + mask] = QRCodeConstants.GetFormatBits((QREccLevel)level, mask);
            }
        }
        return table;
    }

    /// <summary>
    /// Decodes format information from the two redundant 15-bit copies.
    /// </summary>
    /// <param name="rawCopy1">Raw 15 bits read around the top-left finder pattern (bit i = format bit i).</param>
    /// <param name="rawCopy2">Raw 15 bits of the second copy (top-right + bottom-left).</param>
    /// <param name="eccLevel">Decoded error correction level.</param>
    /// <param name="maskPattern">Decoded mask pattern (0-7).</param>
    /// <returns>False when neither copy is within correction distance of a valid pattern.</returns>
    public static bool TryDecode(ushort rawCopy1, ushort rawCopy2, out QREccLevel eccLevel, out int maskPattern)
    {
        // Prefer the copy with the smaller best-distance; each copy is an
        // independent BCH codeword, so distances must not be mixed across copies.
        var best1 = FindBestCandidate(rawCopy1, out var distance1);
        var best2 = FindBestCandidate(rawCopy2, out var distance2);

        var best = distance1 <= distance2 ? best1 : best2;
        var distance = Math.Min(distance1, distance2);

        if (distance > MaxCorrectableBits)
        {
            eccLevel = default;
            maskPattern = 0;
            return false;
        }

        eccLevel = (QREccLevel)(best >> 3);
        maskPattern = best & 7;
        return true;
    }

    private static int FindBestCandidate(ushort raw, out int bestDistance)
    {
        var best = 0;
        bestDistance = int.MaxValue;
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
        return best;
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
