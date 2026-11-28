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
        // Every copy the matrix decoder reads is 15 bits. A wider word is no copy.
        var entry = raw < nearest.Length ? nearest[raw] : NoCandidate;
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

    private const byte NoCandidate = 0xFF;

    /// <summary>
    /// The candidate within three bits of each 15-bit word in the low 5 bits and its distance above them, or <see cref="NoCandidate"/>.
    /// The words within three bits of the 32 candidates are 576 each and never shared, since the candidates lie at least seven apart, so marking them gives the nearest candidate a search would find.
    /// </summary>
    private static readonly byte[] nearest = BuildNearest();

    private static byte[] BuildNearest()
    {
        var table = new byte[1 << 15];
        table.AsSpan().Fill(NoCandidate);
        for (var i = 0; i < MicroQRConstants.FormatWordCount; i++)
        {
            var word = MicroQRConstants.GetFormatWord(i);
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
}
