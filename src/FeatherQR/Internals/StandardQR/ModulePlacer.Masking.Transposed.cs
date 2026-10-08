#if NET8_0_OR_GREATER
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// Transposed mask selection for versions 12-40: every penalty rule runs between whole words of neighbouring rows. The tables and the
/// scalar steps the tiers share: AVX2 (ModulePlacer.Masking.Transposed.X86.cs), 128-bit vectors (ModulePlacer.Masking.Transposed.Vector128.cs),
/// and ARM64 (ModulePlacer.Masking.Arm64.cs), which masks and scores with the 128-bit tier's code.
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
/// On AVX2 the encoder skips the placed bytes: the interleaved stream goes into C directly (<see cref="PlaceStreamColumns"/>), over a
/// column template that already holds the function modules and the version information, and R is its transpose. The zigzag fills a
/// pair of columns at a time, so a run of rows is two column words' bits interleaved in the stream.
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

    /// <summary>The entries a transposed selection rents from the shared pool (four planes and the rules' scratch): for the test that hands it a dirty array.</summary>
    internal static int TransposedScratchLength(int version)
    {
        var layout = GetTransposedLayout(version, QRCodeData.SizeFromVersion(version));
        return 4 * layout.Words * layout.Stride + WorkLength(layout);
    }

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

    /// <summary>
    /// A version's data placement written into column planes: the function modules and the version information as column words, and the
    /// zigzag walk of <see cref="PlacementLayout.Ops"/> as runs of rows where both strip columns are free and as scattered modules.
    /// </summary>
    private sealed class StreamPlacement
    {
        /// <summary>[k * Stride + x]: column x's function modules and version information, rows 64k..; zero past the symbol.</summary>
        public readonly ulong[] TemplateC;
        public readonly StreamOp[] Ops;
        /// <summary>The scattered modules' places, one per stream bit of a scatter op: word index in the planes &lt;&lt; 6 | bit.</summary>
        public readonly uint[] Targets;
        public readonly int FreeModules;

        public StreamPlacement(ulong[] templateC, StreamOp[] ops, uint[] targets, int freeModules)
        {
            TemplateC = templateC;
            Ops = ops;
            Targets = targets;
            FreeModules = freeModules;
        }
    }

    /// <summary>
    /// A walk segment of <see cref="StreamPlacement"/>. A run takes 2 * Count stream bits from Start, the right column X and the left X - 1
    /// in turn, from row Y up or down; a scatter op takes Count bits from Start to <see cref="StreamPlacement.Targets"/> from Offset.
    /// </summary>
    private readonly struct StreamOp
    {
        public readonly int Start;
        public readonly int Count;
        public readonly int X;
        public readonly int Y;
        public readonly int Offset;
        public readonly bool IsRun;
        public readonly bool Up;

        public StreamOp(bool isRun, int start, int count, int x, int y, bool up, int offset)
        {
            IsRun = isRun;
            Start = start;
            Count = count;
            X = x;
            Y = y;
            Up = up;
            Offset = offset;
        }
    }

    private static readonly StreamPlacement?[] streamPlacements = new StreamPlacement?[41];

    /// <summary>A version's stream placement into the transposed layout's column planes (versions 12-40), built on first use.</summary>
    private static StreamPlacement GetStreamPlacement(int version, TransposedLayout layout)
    {
        ref var slot = ref streamPlacements[version];
        var placement = Volatile.Read(ref slot);
        if (placement is not null) return placement;
        placement = BuildStreamPlacement(version, layout);
        Volatile.Write(ref slot, placement);
        return placement;
    }

    /// <summary>The bytes a version's stream placement holds, built on first use: for the test that keeps them in view.</summary>
    internal static int StreamPlacementBytes(int version)
    {
        var placement = GetStreamPlacement(version, GetTransposedLayout(version, QRCodeData.SizeFromVersion(version)));
        return sizeof(ulong) * placement.TemplateC.Length + Unsafe.SizeOf<StreamOp>() * placement.Ops.Length + sizeof(uint) * placement.Targets.Length;
    }

    private static StreamPlacement BuildStreamPlacement(int version, TransposedLayout layout)
    {
        var placement = GetLayout(version);
        var size = layout.Size;
        var stride = layout.Stride;

        var templateC = new ulong[layout.Words * stride];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (placement.Template[y * size + x] != 0)
                    SetPlaneBit(templateC, stride, x, y);
            }
        }
        // The version information, the bits FinishRowPlanes sets in the row planes, here with row and column swapped (versions 7+, always here).
        var versionBits = QRCodeConstants.GetVersionBits(version);
        for (var x = 0; x < 6; x++)
        {
            for (var y = 0; y < 3; y++)
            {
                if ((versionBits & (1u << (x * 3 + y))) == 0) continue;
                SetPlaneBit(templateC, stride, x, y + size - 11);
                SetPlaneBit(templateC, stride, y + size - 11, x);
            }
        }

        var ops = new StreamOp[placement.Ops.Length];
        var targets = new List<uint>();
        for (var i = 0; i < ops.Length; i++)
        {
            var op = placement.Ops[i];
            if (op.IsRun)
            {
                // Core is the left module of the run's first row.
                ops[i] = new StreamOp(true, op.Start, op.Count, op.Core % size + 1, op.Core / size, op.RowStep < 0, 0);
                continue;
            }
            ops[i] = new StreamOp(false, op.Start, op.Count, 0, 0, false, targets.Count);
            for (var b = op.Start; b < op.Start + op.Count; b++)
            {
                var core = placement.Index[b];
                var y = core / size;
                var x = core % size;
                targets.Add((uint)((((y >> 6) * stride + x) << 6) | (y & 63)));
            }
        }
        return new StreamPlacement(templateC, ops, targets.ToArray(), placement.FreeModules);
    }

    /// <summary>
    /// The unmasked symbol as column planes, from the interleaved stream: <paramref name="c"/> (Words * Stride entries) gets the version's
    /// function modules and version information, then the stream's bits, most significant first, in the zigzag order. Modules past the stream's
    /// end stay light, as <see cref="PlaceDataWords(Span{byte}, PlacementLayout, ReadOnlySpan{byte})"/> leaves them.
    /// </summary>
    /// <remarks>
    /// A run of rows takes its two columns' bits alternately, so 64 stream bits are 32 rows of each column: the odd and the even bits drawn
    /// together (<see cref="UnzipPairs"/>) are column words' bits in walk order, bit-reversed for a downward run, since its first row is the
    /// lowest bit.
    /// </remarks>
    private static void PlaceStreamColumns(ReadOnlySpan<byte> stream, StreamPlacement placement, int stride, Span<ulong> c)
    {
        placement.TemplateC.AsSpan().CopyTo(c);
        var streamBits = Math.Min(stream.Length * 8, placement.FreeModules);
        ref var cRef = ref MemoryMarshal.GetReference(c);
        ref var targets = ref MemoryMarshal.GetArrayDataReference(placement.Targets);
        var ops = placement.Ops;
        for (var p = 0; p < ops.Length; p++)
        {
            var op = ops[p];
            if (op.Start >= streamBits) break;
            if (op.IsRun)
            {
                var rows = Math.Min(op.Count, (streamBits - op.Start) / 2);
                for (var done = 0; done < rows; done += 32)
                {
                    var n = Math.Min(32, rows - done);
                    UnzipPairs(ReadStreamBits(stream, op.Start + 2 * done), out var right, out var left);
                    int low;
                    ulong fieldR, fieldL;
                    if (op.Up)
                    {
                        // walk position j is row Y - done - j: the field's top bit is the highest row
                        low = op.Y - done - (n - 1);
                        fieldR = right >> (32 - n);
                        fieldL = left >> (32 - n);
                    }
                    else
                    {
                        low = op.Y + done;
                        var mask = n == 32 ? uint.MaxValue : (1u << n) - 1;
                        fieldR = ReverseBits(right) & mask;
                        fieldL = ReverseBits(left) & mask;
                    }
                    OrColumnField(ref cRef, stride, op.X, low, fieldR, n);
                    OrColumnField(ref cRef, stride, op.X - 1, low, fieldL, n);
                }
                // The stream can end between a run row's right and left module.
                var placed = op.Start + 2 * rows;
                if (placed < streamBits && rows < op.Count && StreamBit(stream, placed) != 0)
                {
                    var y = op.Up ? op.Y - rows : op.Y + rows;
                    Unsafe.Add(ref cRef, (y >> 6) * stride + op.X) |= 1ul << (y & 63);
                }
            }
            else
            {
                var end = Math.Min(op.Start + op.Count, streamBits);
                for (var b = op.Start; b < end; b++)
                {
                    if (StreamBit(stream, b) == 0) continue;
                    var target = Unsafe.Add(ref targets, op.Offset + b - op.Start);
                    Unsafe.Add(ref cRef, (nint)(target >> 6)) |= 1ul << (int)(target & 63);
                }
            }
        }
    }

    /// <summary>ORs the <paramref name="n"/>-bit <paramref name="field"/> into column <paramref name="x"/> from row <paramref name="row"/>, across two words where it straddles one.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void OrColumnField(ref ulong c, int stride, int x, int row, ulong field, int n)
    {
        var k = row >> 6;
        var shift = row & 63;
        Unsafe.Add(ref c, k * stride + x) |= field << shift;
        if (shift + n > 64)
            Unsafe.Add(ref c, (k + 1) * stride + x) |= field >> (64 - shift);
    }

    /// <summary>64 stream bits from bit <paramref name="bit"/>, the first in the top bit; bits past the stream are zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong ReadStreamBits(ReadOnlySpan<byte> stream, int bit)
    {
        var i = bit >> 3;
        var shift = bit & 7;
        if (i + 9 <= stream.Length)
        {
            var hi = BinaryPrimitives.ReadUInt64BigEndian(stream.Slice(i));
            return shift == 0 ? hi : (hi << shift) | ((ulong)stream[i + 8] >> (8 - shift));
        }
        // Within the last nine bytes the ninth is past the stream, so the window is the eight from i, zero past the end, shifted.
        ulong head = 0;
        for (var b = 0; b < 8; b++)
            head = (head << 8) | (i + b < stream.Length ? stream[i + b] : 0u);
        return head << shift;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int StreamBit(ReadOnlySpan<byte> stream, int bit) => (stream[bit >> 3] >> (7 - (bit & 7))) & 1;

    /// <summary>
    /// Splits 64 stream bits (the first in the top bit) into the pairs' first bits and second bits, 32 each, the first pair in the top bit:
    /// a run's right column and left column in walk order.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UnzipPairs(ulong w, out uint first, out uint second)
    {
        first = CompressEvenBits(w >> 1);
        second = CompressEvenBits(w);
    }

    /// <summary>Bit 2i of <paramref name="x"/> to bit i, for i = 0..31.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CompressEvenBits(ulong x)
    {
        x &= 0x5555555555555555ul;
        x = (x | (x >> 1)) & 0x3333333333333333ul;
        x = (x | (x >> 2)) & 0x0F0F0F0F0F0F0F0Ful;
        x = (x | (x >> 4)) & 0x00FF00FF00FF00FFul;
        x = (x | (x >> 8)) & 0x0000FFFF0000FFFFul;
        x = (x | (x >> 16)) & 0x00000000FFFFFFFFul;
        return (uint)x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReverseBits(uint v)
    {
        v = ((v >> 1) & 0x55555555u) | ((v & 0x55555555u) << 1);
        v = ((v >> 2) & 0x33333333u) | ((v & 0x33333333u) << 2);
        v = ((v >> 4) & 0x0F0F0F0Fu) | ((v & 0x0F0F0F0Fu) << 4);
        return BinaryPrimitives.ReverseEndianness(v);
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
