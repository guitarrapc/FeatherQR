#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace FeatherQR.Internals;

internal static partial class ModeSegmenter
{
    /// <summary>
    /// The most characters the 16-bit lanes price: the Numeric capacity of version 40-L, where the costliest plan, at most a header above the all-Byte one, stays under the lanes' 65,535 with the widest headers.
    /// A longer text, which only a Structured Append bound prices whole, takes the scalar loops.
    /// </summary>
    internal const int MaxLaneCostChars = 7089;

    /// <summary>Whether the cost run of a one-byte charset with Byte allowed takes the lane form on this machine and text.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TakesLaneCosts(int length) => Sse41.IsSupported && length <= MaxLaneCostChars;

    // The six states in the low lanes of a ushort vector, n0 n1 n2 a0 a1 b, the two above kept unreachable. A continuation moves n2 into n0, n0 into n1, n1 into n2, a1 into a0, a0 into a1 and b into b.
    private static ReadOnlySpan<byte> continueLanes => [4, 5, 0, 1, 2, 3, 8, 9, 6, 7, 10, 11, 12, 13, 14, 15];
    private static ReadOnlySpan<byte> broadcastLane0 => [0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1];

    // Among equal costs the lower state wins, as the keys of the scalar loops decide. A continuation into n1 comes from n0 and wins every tie, one into a1 from a0 loses to an opening from the
    // Numeric states, one into b from b loses to any opening. By the cheapest state, the lanes n1, a1 and b whose tie goes to the opening.
    private static ReadOnlySpan<ushort> tieToOpened =>
    [
        0, 0, 0, 0, 0xFFFF, 0xFFFF, 0, 0,
        0, 0, 0, 0, 0xFFFF, 0xFFFF, 0, 0,
        0, 0, 0, 0, 0xFFFF, 0xFFFF, 0, 0,
        0, 0, 0, 0, 0, 0xFFFF, 0, 0,
        0, 0, 0, 0, 0, 0xFFFF, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0,
    ];

    // The two parent bytes of a character by the cheapest state (rows) and three bits saying whether n1, a1 and b opened from it (columns): byte 0 the parent of b, byte 1 the parent of n1 in
    // its low three bits and of a1 above them, the continuations' parents being n0, a0 and b. Pinned to that rule by ModeSegmenterCostsParityTest.
    internal static ReadOnlySpan<ushort> parentWords =>
    [
        0x1805, 0x1805, 0x0005, 0x0005, 0x1800, 0x1800, 0x0000, 0x0000,
        0x1805, 0x1905, 0x0805, 0x0905, 0x1801, 0x1901, 0x0801, 0x0901,
        0x1805, 0x1A05, 0x1005, 0x1205, 0x1802, 0x1A02, 0x1002, 0x1202,
        0x1805, 0x1B05, 0x1805, 0x1B05, 0x1803, 0x1B03, 0x1803, 0x1B03,
        0x1805, 0x1C05, 0x2005, 0x2405, 0x1804, 0x1C04, 0x2004, 0x2404,
        0x1805, 0x1D05, 0x2805, 0x2D05, 0x1805, 0x1D05, 0x2805, 0x2D05,
    ];

    /// <summary>
    /// The six states as 16-bit lanes, costs only. A step shuffles the states to the lanes they continue into and adds the class's bits with saturation, adds the cheapest state to the headers the
    /// class can open, keeps the lane minimum, and takes the cheapest state by one horizontal minimum. A run of two or more characters only Byte encodes is priced in the scalar form of the
    /// Byte lane alone, since every other lane is unreachable after its first character. Same answer as <see cref="CostsLatin"/>, including the lowest state among equal final costs.
    /// </summary>
    internal static int CostsLatinSse41(ReadOnlySpan<char> text, int openNumeric, int openAlnum, int openByte, bool allowAlnum, out int finalState)
    {
        const ushort F = 0xFFFF;
        var oN = (ushort)openNumeric;
        var oA = allowAlnum ? (ushort)openAlnum : F;
        var oB = (ushort)openByte;
        var contOther = Vector128.Create(F, F, F, F, F, (ushort)8, F, F);
        var contAlnum = allowAlnum ? Vector128.Create(F, F, F, (ushort)5, (ushort)6, (ushort)8, F, F) : contOther;
        var contDigit = allowAlnum ? Vector128.Create((ushort)3, (ushort)4, (ushort)3, (ushort)5, (ushort)6, (ushort)8, F, F) : Vector128.Create((ushort)3, (ushort)4, (ushort)3, F, F, (ushort)8, F, F);
        var openOther = Vector128.Create(F, F, F, F, F, oB, F, F);
        var openAlnumV = Vector128.Create(F, F, F, F, oA, oB, F, F);
        var openDigit = Vector128.Create(F, oN, F, F, oA, oB, F, F);
        var continueMask = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(continueLanes));
        var broadcast = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(broadcastLane0));
        var allF = Vector128.Create(F);

        var s = allF;
        var cheapest = Vector128<ushort>.Zero;
        var length = text.Length;
        for (var i = 0; i < length; i++)
        {
            var cls = ClassOf(text[i]);
            Vector128<ushort> cont, open;
            if (cls == ClassDigit)
            {
                cont = contDigit;
                open = openDigit;
            }
            else if (cls != ClassOther)
            {
                cont = contAlnum;
                open = openAlnumV;
            }
            else if (i + 1 < length && ClassOf(text[i + 1]) == ClassOther)
            {
                var run = 2;
                i++;
                while (i + 1 < length && ClassOf(text[i + 1]) == ClassOther)
                {
                    run++;
                    i++;
                }
                var b = Math.Min(Math.Min(s.GetElement(5) + 8 * run, cheapest.GetElement(0) + openByte + 8 * (run - 1)), F);
                s = Sse2.Insert(allF, (ushort)b, 5);
                cheapest = Vector128.Create((ushort)b);
                continue;
            }
            else
            {
                cont = contOther;
                open = openOther;
            }

            s = Sse41.Min(Sse2.AddSaturate(Ssse3.Shuffle(s.AsByte(), continueMask).AsUInt16(), cont), Sse2.AddSaturate(cheapest, open));
            cheapest = Ssse3.Shuffle(Sse41.MinHorizontal(s).AsByte(), broadcast).AsUInt16();
        }

        return FinishLanes(s, out finalState);
    }

    /// <summary>
    /// <see cref="CostsLatinSse41"/> with the parents <see cref="Reconstruct"/> walks, <see cref="ParentBytesPerChar"/> a character as <see cref="TrackedLatin"/> writes them. Which of n1, a1 and b
    /// opened from the cheapest state comes from two compares and the tie rule, and the two parent bytes from one table read. Same table as the scalar loop, byte for byte.
    /// </summary>
    internal static int TrackedLatinSse41(ReadOnlySpan<char> text, int openNumeric, int openAlnum, int openByte, bool allowAlnum, Span<byte> table, out int finalState)
    {
        const ushort F = 0xFFFF;
        var length = text.Length;
        if (length == 0)
        {
            finalState = StateByte;
            return Unreachable;
        }

        var oN = (ushort)openNumeric;
        var oA = allowAlnum ? (ushort)openAlnum : F;
        var oB = (ushort)openByte;
        var contOther = Vector128.Create(F, F, F, F, F, (ushort)8, F, F);
        var contAlnum = allowAlnum ? Vector128.Create(F, F, F, (ushort)5, (ushort)6, (ushort)8, F, F) : contOther;
        var contDigit = allowAlnum ? Vector128.Create((ushort)3, (ushort)4, (ushort)3, (ushort)5, (ushort)6, (ushort)8, F, F) : Vector128.Create((ushort)3, (ushort)4, (ushort)3, F, F, (ushort)8, F, F);
        var openOther = Vector128.Create(F, F, F, F, F, oB, F, F);
        var openAlnumV = Vector128.Create(F, F, F, F, oA, oB, F, F);
        var openDigit = Vector128.Create(F, oN, F, F, oA, oB, F, F);
        var continueMask = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(continueLanes));
        var broadcast = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(broadcastLane0));
        var allF = Vector128.Create(F);
        ref var tableStart = ref MemoryMarshal.GetReference(table);
        ref var ties = ref MemoryMarshal.GetReference(tieToOpened);
        ref var words = ref MemoryMarshal.GetReference(parentWords);
        // Without Alphanumeric the scalar loop writes no parent for a1, so its lane never counts as opened and its bits of the word stay clear.
        var tieMask = allowAlnum ? allF : Vector128.Create(F, F, F, F, (ushort)0, F, F, F);
        var wordMask = allowAlnum ? (ushort)0xFFFF : (ushort)0xC7FF;

        // The first character opens from the start, whose parents are the start's.
        var cls0 = ClassOf(text[0]);
        var s = cls0 == ClassDigit ? openDigit : cls0 != ClassOther ? openAlnumV : openOther;
        var m = Sse41.MinHorizontal(s);
        var cheapest = Ssse3.Shuffle(m.AsByte(), broadcast).AsUInt16();
        int idx = m.GetElement(1);
        table[0] = StateStart;
        table[1] = StateStart | (StateStart << 3);

        for (var i = 1; i < length; i++)
        {
            var cls = ClassOf(text[i]);
            Vector128<ushort> cont, open;
            if (cls == ClassDigit)
            {
                cont = contDigit;
                open = openDigit;
            }
            else if (cls != ClassOther)
            {
                cont = contAlnum;
                open = openAlnumV;
            }
            else
            {
                // Only Byte stays reachable: its parent is the cheapest state where opening costs no more, else itself, and the rest of the run continues it.
                int b = s.GetElement(5) + 8, opened = cheapest.GetElement(0) + openByte;
                var parent = StateByte;
                if (opened <= b)
                {
                    b = opened;
                    parent = idx;
                }
                Unsafe.Add(ref tableStart, i * ParentBytesPerChar) = (byte)parent;
                while (i + 1 < length && ClassOf(text[i + 1]) == ClassOther)
                {
                    i++;
                    b += 8;
                    Unsafe.Add(ref tableStart, i * ParentBytesPerChar) = StateByte;
                }
                b = Math.Min(b, F);
                s = Sse2.Insert(allF, (ushort)b, 5);
                cheapest = Vector128.Create((ushort)b);
                idx = StateByte;
                continue;
            }

            var continued = Sse2.AddSaturate(Ssse3.Shuffle(s.AsByte(), continueMask).AsUInt16(), cont);
            var openedV = Sse2.AddSaturate(cheapest, open);
            s = Sse41.Min(continued, openedV);
            // The lanes the opening took: cheaper, or equal where the tie goes to it.
            var eq = Sse2.CompareEqual(continued, openedV);
            var sel = Sse2.Or(Sse2.AndNot(eq, Sse2.CompareEqual(s, openedV)), Sse2.And(eq, Sse2.And(Vector128.LoadUnsafe(ref ties, (nuint)(idx * 8)), tieMask)));
            var bits = Sse2.MoveMask(Sse2.PackSignedSaturate(sel.AsInt16(), sel.AsInt16()).AsByte());
            var key = ((bits >> 1) & 1) | ((bits >> 3) & 6);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref tableStart, i * ParentBytesPerChar), (ushort)(Unsafe.Add(ref words, (idx << 3) | key) & wordMask));
            m = Sse41.MinHorizontal(s);
            cheapest = Ssse3.Shuffle(m.AsByte(), broadcast).AsUInt16();
            idx = m.GetElement(1);
        }

        return FinishLanes(s, out finalState);
    }

    /// <summary>The cheapest final state, the lowest among equal costs as <see cref="Best(int, int, int, int, int, int, out int)"/> answers, or <see cref="Unreachable"/> and Byte when none is reachable.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FinishLanes(Vector128<ushort> s, out int finalState)
    {
        var m = Sse41.MinHorizontal(s);
        int cost = m.GetElement(0);
        if (cost == 0xFFFF)
        {
            finalState = StateByte;
            return Unreachable;
        }
        finalState = m.GetElement(1);
        return cost;
    }
}
#endif
