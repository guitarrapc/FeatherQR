#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// Transposed mask selection for versions 12-40: every penalty rule runs between whole words of neighbouring rows. The tables and the
/// scalar steps both tiers share: AVX2 (ModulePlacer.Masking.Transposed.X86.cs) and 128-bit vectors (ModulePlacer.Masking.Transposed.Vector128.cs).
/// </summary>
/// <remarks>
/// <para>
/// A row of these versions spans two or three 64-bit words, so a row-direction rule on row words shifts every term across words. Each
/// candidate is held twice instead: as row words R (word k of row y, bit = column - 64k) and as column words C = transpose(R) (word k of
/// column x, bit = row - 64k), both as word planes, [k * Stride + index]. A column-direction run or finder-like window is a run along the
/// index of R, and a row-direction one is the same run along the index of C, so both are computed by one routine over consecutive words of a
/// plane, several rows or columns per vector, with no shift across words. Only the 2x2 rule shifts, one bit per row word.
/// </para>
/// <para>
/// Masking is an XOR, so a candidate is the packed symbol XOR the pattern's template restricted to the data area, in both orientations:
/// the data is packed and transposed once per symbol, and each candidate costs one masking pass per plane. The templates are 12-periodic
/// along both axes, so they are kept as 12 rows (and 12 columns) per pattern, and the per-version tables hold only the unblocked modules
/// of each row and of each column. The format information is set per candidate in both orientations, the version information once in R
/// before the transpose.
/// </para>
/// <para>
/// The column-direction rules, the 2x2 rule and the dark count come from R, the row-direction rules from C. Before C's finder windows,
/// the last and smallest term, the partial score with the balance is a lower bound of the total, so a candidate already above the best
/// total skips them. A checkpoint after R alone almost never fired: half the score is left there (Lessons Learned, Performance in
/// specs/standardqr-encoder.md).
/// </para>
/// </remarks>
internal static partial class ModulePlacer
{
    /// <summary>A version's tables for the transposed tiers, built on first use.</summary>
    private sealed class TransposedLayout
    {
        public readonly int Size;
        /// <summary>64-bit words per row and per column: 2 for versions 12-27, 3 for 28-40.</summary>
        public readonly int Words;
        /// <summary>Entries per word plane: the symbol's rows (or columns), then zero rows to past every offset read and to a whole 64-row block.</summary>
        public readonly int Stride;
        /// <summary>
        /// Entries of a plane the rules read, which the masking pass writes: the symbol and the nine past it that the AVX2 tier's farthest
        /// offset load reaches (the 128-bit tier's reaches seven), to a multiple of 4.
        /// </summary>
        public readonly int ReadRows;
        /// <summary>[k * Stride + y]: the unblocked modules of row y, word k.</summary>
        public readonly ulong[] AllowedR;
        /// <summary>[k * Stride + x]: the unblocked modules of column x, word k.</summary>
        public readonly ulong[] AllowedC;
        /// <summary>[k]: the bits of word k inside the symbol.</summary>
        public readonly ulong[] Valid;
        /// <summary>[k]: the bits of word k where a 2x2 block can start (the last column cannot).</summary>
        public readonly ulong[] ValidN1;

        public TransposedLayout(int version, int size)
        {
            Size = size;
            Words = (size + 63) >> 6;
            Stride = Math.Max((size + 16 + 3) & ~3, 64 * Words);
            ReadRows = (size + 9 + 3) & ~3;
            Valid = new ulong[Words];
            ValidN1 = new ulong[Words];
            for (var k = 0; k < Words; k++)
            {
                Valid[k] = LowBits(size - 64 * k);
                ValidN1[k] = LowBits(size - 1 - 64 * k);
            }

            var blockedMask = GetLayout(version).BlockedMask;
            AllowedR = new ulong[Words * Stride];
            AllowedC = new ulong[Words * Stride];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    if (IsModuleBlocked(blockedMask, y * size + x))
                        continue;
                    AllowedR[(x >> 6) * Stride + y] |= 1ul << (x & 63);
                    AllowedC[(y >> 6) * Stride + x] |= 1ul << (y & 63);
                }
            }
        }

        private static ulong LowBits(int bits) => bits <= 0 ? 0 : bits >= 64 ? ulong.MaxValue : (1ul << bits) - 1;
    }

    private static readonly TransposedLayout?[] transposedLayouts = new TransposedLayout?[41];

    private static TransposedLayout GetTransposedLayout(int version, int size)
    {
        // The tables are cached per version, so a version/size mismatch must never reach the builder (it would poison the slot for every later caller).
        if (version < 12 || version > 40 || size != QRCodeData.SizeFromVersion(version))
            throw new ArgumentException($"size {size} does not match a multi-word version {version} (12-40)", nameof(size));
        ref var slot = ref transposedLayouts[version];
        var layout = Volatile.Read(ref slot);
        if (layout is not null) return layout;
        layout = new TransposedLayout(version, size);
        Volatile.Write(ref slot, layout);
        return layout;
    }

    /// <summary>The bytes a version's tables hold, built on first use: for the test that keeps them in view.</summary>
    internal static int TransposedTableBytes(int version)
    {
        var layout = GetTransposedLayout(version, QRCodeData.SizeFromVersion(version));
        return sizeof(ulong) * (layout.AllowedR.Length + layout.AllowedC.Length + layout.Valid.Length + layout.ValidN1.Length);
    }

    /// <summary>The mask templates as word planes, [k * 96 + pattern * 12 + r]: word k of template row r (bit = column - 64k).</summary>
    private static readonly ulong[] _maskTemplatePlanesR = BuildMaskTemplatePlanes(columns: false);

    /// <summary>The transposed templates, [k * 96 + pattern * 12 + c]: word k of template column c (bit = row - 64k).</summary>
    private static readonly ulong[] _maskTemplatePlanesC = BuildMaskTemplatePlanes(columns: true);

    private static ulong[] BuildMaskTemplatePlanes(bool columns)
    {
        var planes = new ulong[3 * 96];
        for (var p = 0; p < 8; p++)
        {
            for (var i = 0; i < 12; i++)
            {
                for (var b = 0; b < 192; b++)
                {
                    if (columns ? MaskHit(p, b, i) : MaskHit(p, i, b))
                        planes[(b >> 6) * 96 + p * 12 + i] |= 1ul << (b & 63);
                }
            }
        }
        return planes;
    }

    /// <summary>The scratch one plane's rules use: four runs of Stride + 8 entries (vertical equality, finder core, light run, horizontal equality).</summary>
    private static int WorkLength(TransposedLayout layout) => 4 * (layout.Stride + 8);

    /// <summary>
    /// Sets bit <paramref name="bit"/> of entry <paramref name="index"/>: in row planes the module at row index, column bit, and in column
    /// planes, with the two swapped, the same module.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetPlaneBit(Span<ulong> planes, int stride, int index, int bit)
        => planes[(bit >> 6) * stride + index] |= 1ul << (bit & 63);

    /// <summary>Completes the packed row planes: zero rows past the symbol, and the version information.</summary>
    private static void FinishRowPlanes(Span<ulong> r, TransposedLayout layout, int version)
    {
        var size = layout.Size;
        var stride = layout.Stride;
        for (var k = 0; k < layout.Words; k++)
        {
            r.Slice(k * stride + size, stride - size).Clear();
        }

        // Version information sits in blocked modules, so it is the same in every candidate (versions 7+, always here).
        var versionBits = QRCodeConstants.GetVersionBits(version);
        for (var x = 0; x < 6; x++)
        {
            for (var y = 0; y < 3; y++)
            {
                if ((versionBits & (1u << (x * 3 + y))) == 0) continue;
                SetPlaneBit(r, stride, y + size - 11, x);
                SetPlaneBit(r, stride, x, y + size - 11);
            }
        }
    }

    /// <summary>Sets candidate <paramref name="pattern"/>'s format information in both orientations.</summary>
    private static void SetFormatTransposed(Span<ulong> rp, Span<ulong> cp, TransposedLayout layout, int pattern, QREccLevel eccLevel)
    {
        // The format modules are blocked, so light in every candidate before this: the overlay sets its dark modules (same scheme as PokeFormatBits64).
        var formatBits = QRCodeConstants.GetFormatBits(eccLevel, pattern);
        var size = layout.Size;
        var stride = layout.Stride;
        for (var i = 0; i < 15; i++)
        {
            if ((formatBits & (1 << i)) == 0) continue;
            var x2 = i < 8 ? size - 1 - i : 8;
            var y2 = i < 8 ? 8 : size - 15 + i;
            SetPlaneBit(rp, stride, FormatYs1[i], FormatXs1[i]);
            SetPlaneBit(cp, stride, FormatXs1[i], FormatYs1[i]);
            SetPlaneBit(rp, stride, y2, x2);
            SetPlaneBit(cp, stride, x2, y2);
        }
    }

    /// <summary>The winning pattern's XOR delta of row <paramref name="y"/>: the template row restricted to the row's data modules.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Row192 WinnerDelta(TransposedLayout layout, int pattern, int y, int tplRow)
    {
        var allowed = layout.AllowedR;
        var stride = layout.Stride;
        var rowAllowed = new Row192(allowed[y], allowed[stride + y], layout.Words == 3 ? allowed[2 * stride + y] : 0);
        return _maskTemplates[pattern * 12 + tplRow] & rowAllowed;
    }
}
#endif
