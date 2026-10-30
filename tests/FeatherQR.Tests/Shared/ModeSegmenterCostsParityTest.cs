using FeatherQR.Internals;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The lane tiers of the cost run (<see cref="ModeSegmenter.CostsLatinSse41"/> and <see cref="ModeSegmenter.TrackedLatinSse41"/> on x64,
/// <see cref="ModeSegmenter.CostsLatinAdvSimd"/> on ARM64, which has no tracked form) against the scalar loops, with the cost, the final state
/// and the parent table compared byte for byte. The dispatch is held to the same on both sides of the tier's limit.
/// <c>ModeSegmenterPlanParityTest</c> holds the dispatch to an all-states reference besides.
/// </summary>
public class ModeSegmenterCostsParityTest
{
    private const int ModeIndicatorBits = 4;

    private delegate int Kernel(string text, EciMode charset, int openNumeric, int openAlnum, int openByte, bool allowAlnum, Span<byte> parents, out int finalState);

    /// <summary>The dispatch and each lane tier this machine runs, with whether it writes the parents.</summary>
    private static IEnumerable<(string Name, Kernel Run, bool Tracks)> Kernels()
    {
        yield return ("dispatch", (string text, EciMode charset, int openNumeric, int openAlnum, int openByte, bool allowAlnum, Span<byte> parents, out int finalState)
            => ModeSegmenter.ComputeCosts(text, charset, ModeIndicatorBits, openNumeric - ModeIndicatorBits - 4, openAlnum - ModeIndicatorBits - 6, openByte - ModeIndicatorBits - 8, parents, out finalState, allowAlnum), true);
#if NET8_0_OR_GREATER
        if (System.Runtime.Intrinsics.X86.Sse41.IsSupported)
        {
            yield return ("Sse41", (string text, EciMode charset, int openNumeric, int openAlnum, int openByte, bool allowAlnum, Span<byte> parents, out int finalState)
                => parents.IsEmpty
                    ? ModeSegmenter.CostsLatinSse41(text, openNumeric, openAlnum, openByte, allowAlnum, out finalState)
                    : ModeSegmenter.TrackedLatinSse41(text, openNumeric, openAlnum, openByte, allowAlnum, parents, out finalState), true);
        }
        if (System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
        {
            yield return ("AdvSimd", (string text, EciMode charset, int openNumeric, int openAlnum, int openByte, bool allowAlnum, Span<byte> parents, out int finalState)
                => ModeSegmenter.CostsLatinAdvSimd(text, openNumeric, openAlnum, openByte, allowAlnum, out finalState), false);
        }
#endif
    }

    public static IEnumerable<string> Alphabets() =>
    [
        "0123456789",
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
        " $%*+-./:",
        "abcdefghijklmnopqrstuvwxyz",
        "!\"#&'(),;<=>?@[\\]^_`{|}~\u0000\u001D\u007F",
        "0123456789ABCDEFGHIJ $%*+-./:abcxyz!?",
        "0123456789abc",
        "AAAAAAAAAA1111111111aaaaaaaaaa",
        "a7A1",
        "éèêàçñüß ÿ",
        "日本語漢字テストࠀ￿😀",
    ];

    /// <summary>Opening costs the scans pass: the three Standard QR bands, the rMQR R7x59 triple and a shape narrower still, as (numeric, alphanumeric, byte with its first character).</summary>
    public static IEnumerable<(int OpenNumeric, int OpenAlnum, int OpenByte)> Opens() =>
    [
        (ModeIndicatorBits + 10 + 4, ModeIndicatorBits + 9 + 6, ModeIndicatorBits + 8 + 8),
        (ModeIndicatorBits + 12 + 4, ModeIndicatorBits + 11 + 6, ModeIndicatorBits + 16 + 8),
        (ModeIndicatorBits + 14 + 4, ModeIndicatorBits + 13 + 6, ModeIndicatorBits + 16 + 8),
        (ModeIndicatorBits + 5 + 4, ModeIndicatorBits + 5 + 6, ModeIndicatorBits + 4 + 8),
        (ModeIndicatorBits + 4 + 4, ModeIndicatorBits + 3 + 6, ModeIndicatorBits + 4 + 8),
    ];

    [Test]
    [MethodDataSource(nameof(Alphabets))]
    public async Task EveryKernel_MatchesTheScalarLoops_AtEveryLengthCharsetAndWidth(string alphabet)
    {
        var random = new Random(20261019);
        // 7,089 is the tier's limit, where the costliest plan still fits its 16-bit lanes, and 7,090 and 8,000 are past it, where the dispatch must take the scalar loops.
        foreach (var length in Enumerable.Range(0, 71).Concat([100, 120, 361, 1000, 2860, 7089, 7090, 8000]))
        {
            for (var seed = 0; seed < (length > 1000 ? 1 : 2); seed++)
            {
                var chars = new char[length];
                for (var i = 0; i < length; i++)
                    chars[i] = alphabet[random.Next(alphabet.Length)];
                var text = new string(chars);
                foreach (var charset in new[] { EciMode.Default, EciMode.Iso8859_1 })
                {
                    foreach (var (openNumeric, openAlnum, openByte) in Opens())
                    {
                        foreach (var allowAlnum in new[] { true, false })
                        {
                            var because = $"{charset} ({openNumeric}, {openAlnum}, {openByte}) alnum={allowAlnum} on {length} chars of 「{Show(alphabet)}」";
                            var expected = ModeSegmenter.ComputeCostsScalar(text, charset, openNumeric, openAlnum, openByte - 8, default, out var expectedState, allowAlnum);
                            var expectedTable = new byte[length * ModeSegmenter.ParentBytesPerChar];
                            if (length > 0)
                                ModeSegmenter.ComputeCostsScalar(text, charset, openNumeric, openAlnum, openByte - 8, expectedTable, out _, allowAlnum);
                            foreach (var (name, run, tracks) in Kernels())
                            {
                                if (name != "dispatch" && length > ModeSegmenter.MaxLaneCostChars)
                                    continue;
                                var actual = run(text, charset, openNumeric, openAlnum, openByte, allowAlnum, default, out var actualState);
                                await Assert.That((actual, actualState)).IsEqualTo((expected, expectedState)).Because($"{name} costs only, {because}");
                                if (length == 0 || !tracks)
                                    continue;
                                var table = new byte[length * ModeSegmenter.ParentBytesPerChar];
                                var tracked = run(text, charset, openNumeric, openAlnum, openByte, allowAlnum, table, out var trackedState);
                                await Assert.That((tracked, trackedState)).IsEqualTo((expected, expectedState)).Because($"{name} tracked, {because}");
                                await Assert.That(table).IsEquivalentTo(expectedTable, CollectionOrdering.Matching).Because($"{name} parents, {because}");
                            }
                        }
                    }
                }
            }
        }
    }

#if NET8_0_OR_GREATER
    /// <summary>The parent words the lane tier reads, held to the rule they encode: by the cheapest state and whether n1, a1 and b opened from it, b's parent in byte 0, n1's and a1's in byte 1.</summary>
    [Test]
    public async Task ParentWords_EncodeTheRule()
    {
        var words = ModeSegmenter.parentWords.ToArray();
        await Assert.That(words.Length).IsEqualTo(48);
        for (var idx = 0; idx < 6; idx++)
        {
            for (var key = 0; key < 8; key++)
            {
                var parentN1 = (key & 1) != 0 ? idx : 0;
                var parentA1 = (key & 2) != 0 ? idx : 3;
                var parentB = (key & 4) != 0 ? idx : 5;
                var expected = parentB | ((parentN1 | (parentA1 << 3)) << 8);
                await Assert.That((int)words[(idx << 3) | key]).IsEqualTo(expected).Because($"cheapest state {idx}, opened bits {key}");
            }
        }
    }
#endif

    private static string Show(string text)
        => string.Concat(text.Take(40).Select(c => c < 0x20 || c >= 0x7F ? $"\\u{(int)c:X4}" : c.ToString()));
}
