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
        => TryDecode(raw, out version, out eccLevel, out maskPattern, out _);

    /// <summary>As <see cref="TryDecode(ushort, out MicroQRVersion, out MicroQREccLevel, out int)"/>, with the number of bits the copy differs from the candidate it decoded to.</summary>
    public static bool TryDecode(ushort raw, out MicroQRVersion version, out MicroQREccLevel eccLevel, out int maskPattern, out int distance)
    {
        // Every copy the matrix decoder reads is 15 bits; a wider word takes the search
        if (raw < nearest.Length)
        {
            var entry = nearest[raw];
            if (entry == NoCandidate)
            {
                version = default;
                eccLevel = default;
                maskPattern = -1;
                distance = -1;
                return false;
            }

            MicroQRConstants.GetVersionAndEccFromSymbolNumber((entry & 31) >> 2, out version, out eccLevel);
            maskPattern = entry & 3;
            distance = entry >> 5;
            return true;
        }

        return TryDecodeBySearch(raw, out version, out eccLevel, out maskPattern, out distance);
    }

    private const byte NoCandidate = 0xFF;

    /// <summary>
    /// The candidate within <see cref="MaxCorrectableBits"/> of each 15-bit word in the low 5 bits and its distance above them, or <see cref="NoCandidate"/>.
    /// The words within three bits of the 32 candidates are 576 each and never shared, since the candidates lie at least seven apart, so marking them gives what the search finds.
    /// </summary>
    private static readonly byte[] nearest = BuildNearest();

    private static byte[] BuildNearest()
    {
        var table = new byte[1 << 15];
        table.AsSpan().Fill(NoCandidate);
        for (var i = 0; i < candidates.Length; i++)
        {
            var word = candidates[i];
            table[word] = (byte)i;
            for (var a = 0; a < 15; a++)
            {
                table[word ^ (1 << a)] = (byte)(i | 1 << 5);
                for (var b = a + 1; b < 15; b++)
                {
                    table[word ^ (1 << a) ^ (1 << b)] = (byte)(i | 2 << 5);
                    for (var c = b + 1; c < 15; c++)
                        table[word ^ (1 << a) ^ (1 << b) ^ (1 << c)] = (byte)(i | 3 << 5);
                }
            }
        }
        return table;
    }

    private static bool TryDecodeBySearch(ushort raw, out MicroQRVersion version, out MicroQREccLevel eccLevel, out int maskPattern, out int distance)
    {
        var best = 0;
        var bestDistance = int.MaxValue;
        for (var i = 0; i < candidates.Length; i++)
        {
            var bits = PopCount((ushort)(raw ^ candidates[i]));
            if (bits < bestDistance)
            {
                bestDistance = bits;
                best = i;
                if (bits == 0)
                    break;
            }
        }

        if (bestDistance > MaxCorrectableBits)
        {
            version = default;
            eccLevel = default;
            maskPattern = -1;
            distance = -1;
            return false;
        }

        MicroQRConstants.GetVersionAndEccFromSymbolNumber(best >> 2, out version, out eccLevel);
        maskPattern = best & 3;
        distance = bestDistance;
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
