namespace FeatherQR.Internals.RmQr;

/// <summary>
/// rMQR format information decoding (ISO/IEC 23941 7.9): each 18-bit copy is
/// matched against the 64 valid words of its side (the finder-side and
/// sub-finder-side copies carry different XOR masks), correcting up to 3 bit errors
/// (BCH(18,6) minimum distance ≥ 7, verified in RmQRConstantsUnitTest). The matrix
/// decoder knows the version from the physical dimensions, so with both copies
/// available only the copies naming that version count, the closer of those wins,
/// ties prefer the finder side (a copy miscorrected toward another version's word
/// cannot veto the valid one). The image decoder reads the finder-side copy alone
/// through <see cref="TryDecodeCopy"/> to learn the version before sampling.
/// </summary>
internal static class RmQRFormatInformationDecoder
{
    private const int MaxCorrectableBits = 3;

    // 64 valid words per side, index = eccBit << 5 | versionIndex, built from the
    // encoder's own GetFormatBits so both sides always agree.
    private static readonly int[] finderSideCandidates = BuildCandidates(subFinderSide: false);
    private static readonly int[] subFinderSideCandidates = BuildCandidates(subFinderSide: true);

    private static int[] BuildCandidates(bool subFinderSide)
    {
        var table = new int[64];
        for (var index = 0; index < 64; index++)
        {
            var version = (RmQRVersion)((index & 31) + 1);
            var eccLevel = (RmQREccLevel)(index >> 5);
            table[index] = RmQRConstants.GetFormatBits(version, eccLevel, subFinderSide);
        }
        return table;
    }

    /// <summary>Decodes one copy; false when no valid word is within 3 bits.</summary>
    public static bool TryDecodeCopy(int raw, bool subFinderSide, out RmQRVersion version, out RmQREccLevel eccLevel, out int distance)
    {
        var candidates = subFinderSide ? subFinderSideCandidates : finderSideCandidates;
        var best = 0;
        distance = int.MaxValue;
        for (var i = 0; i < candidates.Length; i++)
        {
            var d = PopCount((uint)(raw ^ candidates[i]));
            if (d < distance)
            {
                distance = d;
                best = i;
                if (d == 0)
                    break;
            }
        }

        if (distance > MaxCorrectableBits)
        {
            version = default;
            eccLevel = default;
            return false;
        }

        version = (RmQRVersion)((best & 31) + 1);
        eccLevel = (RmQREccLevel)(best >> 5);
        return true;
    }

    /// <summary>
    /// Decodes from both copies when the version is already known from the physical
    /// dimensions: only copies that agree with <paramref name="expectedVersion"/> count
    /// (a copy miscorrected toward another version's word cannot veto the valid one),
    /// the closer agreeing copy wins (ties → finder side); false when neither copy is a
    /// valid word of the expected version within the correction distance.
    /// </summary>
    public static bool TryDecode(int finderSideRaw, int subFinderSideRaw, RmQRVersion expectedVersion, out RmQREccLevel eccLevel, out int distance)
    {
        var leftOk = TryDecodeCopy(finderSideRaw, subFinderSide: false, out var leftVersion, out var leftEcc, out var leftDistance) && leftVersion == expectedVersion;
        var rightOk = TryDecodeCopy(subFinderSideRaw, subFinderSide: true, out var rightVersion, out var rightEcc, out var rightDistance) && rightVersion == expectedVersion;

        if (leftOk && (!rightOk || leftDistance <= rightDistance))
        {
            eccLevel = leftEcc;
            distance = leftDistance;
            return true;
        }

        if (rightOk)
        {
            eccLevel = rightEcc;
            distance = rightDistance;
            return true;
        }

        eccLevel = default;
        distance = -1;
        return false;
    }

    private static int PopCount(uint v)
    {
        // 32-bit SWAR popcount (netstandard2.0 has no BitOperations.PopCount)
        v -= (v >> 1) & 0x55555555;
        v = (v & 0x33333333) + ((v >> 2) & 0x33333333);
        v = (v + (v >> 4)) & 0x0F0F0F0F;
        return (int)((v * 0x01010101) >> 24);
    }
}
