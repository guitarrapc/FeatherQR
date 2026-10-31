namespace FeatherQR.Internals.RmQR;

/// <summary>
/// Decodes rMQR format information with up to three bit errors per copy (ISO/IEC 23941 7.9).
/// </summary>
/// <remarks>
/// Matrix decoding accepts only copies matching the physically known version. The closer copy wins, with finder-side ties.
/// Image decoding uses <see cref="TryDecodeCopy"/> on the finder side to discover the version before sampling.
/// </remarks>
internal static class RmQRFormatInformationDecoder
{
    // Each side has 64 BCH(18,6) codewords with a distinct XOR mask and minimum distance at least seven.
    // Checking the physical version prevents a miscorrected copy from vetoing the other valid copy.
    private const int MaxCorrectableBits = 3;

    // 64 valid words per side, index = eccBit << 5 | versionIndex, built from the
    // encoder's own GetFormatBits so both sides always agree.
    private static readonly int[] finderSideCandidates = BuildCandidates(subFinderSide: false);
    private static readonly int[] subFinderSideCandidates = BuildCandidates(subFinderSide: true);

    // The word of a side by its bits 0-5: no two words of a side share those bits (RmQRFormatCopyEarlyExitTest checks it)
    private static readonly int[] finderSideByLowBits = BuildByLowBits(finderSideCandidates);
    private static readonly int[] subFinderSideByLowBits = BuildByLowBits(subFinderSideCandidates);

    private static int[] BuildByLowBits(int[] candidates)
    {
        var table = new int[64];
        foreach (var word in candidates)
            table[word & 63] = word;
        return table;
    }

    /// <summary>The valid word of a side whose bits 0-5 are those of <paramref name="lowBits"/>, the only one a copy with those bits can equal.</summary>
    internal static int WordOfLowBits(int lowBits, bool subFinderSide) => (subFinderSide ? subFinderSideByLowBits : finderSideByLowBits)[lowBits & 63];

    /// <summary>The valid word of a version and level on one side.</summary>
    internal static int Word(RmQRVersion version, RmQREccLevel eccLevel, bool subFinderSide) => (subFinderSide ? subFinderSideCandidates : finderSideCandidates)[(int)eccLevel << 5 | ((int)version - 1)];

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

    /// <summary>Decodes one copy. Returns false when no valid word is within 3 bits.</summary>
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
    /// Decodes from both copies when the version is already known from the physical dimensions: only copies that agree with <paramref name="expectedVersion"/> count (a copy miscorrected toward another version's word cannot veto the valid one), the closer agreeing copy wins (ties → finder side). Returns false when neither copy is a valid word of the expected version within the correction distance.
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
#if NET8_0_OR_GREATER
        return System.Numerics.BitOperations.PopCount(v);
#else
        // 32-bit SWAR popcount: the netstandard targets have no BitOperations.PopCount
        v -= (v >> 1) & 0x55555555;
        v = (v & 0x33333333) + ((v >> 2) & 0x33333333);
        v = (v + (v >> 4)) & 0x0F0F0F0F;
        return (int)((v * 0x01010101) >> 24);
#endif
    }
}
