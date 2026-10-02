// PROTOTYPE, reference only. Not part of any project or of FeatherQR.slnx, never compiled by the build.
// It belongs to phase 5 of .github/docs/plans/encode-performance-plan.md and is deleted with that plan.
//
// What it is: Standard QR mask selection with every penalty rule computed between whole words of
// neighbouring rows. The candidate is held as row words and, transposed, as column words, so the
// row-direction rules run "vertically" on the transpose and no rule shifts bits across words except
// the single one-bit shift of the 2x2 rule. Design and numbers:
// .github/docs/plans/references/encode-performance-measurements.md ("A transposed scorer for versions 12 to 40").
//
// How it was run (2026-10-02, library at main 34d16c9): a scratch net10.0 BenchmarkDotNet 0.15.8 project
// with ImplicitUsings and AllowUnsafeBlocks, referencing src/FeatherQR. It reads library internals
// (ModulePlacer.GetLayout / PlaceDataWords / PlaceFormat / PlaceVersion / MaskCode / XorUnpackRow64Simd,
// QRCodeConstants.GetFormatBits / GetVersionBits), so the scratch project was named and signed as the
// test assembly, which the library's InternalsVisibleTo admits. That is a measuring shortcut, not a
// pattern for committed code. ProtoCheck.Run() is the correctness gate, ProtoStages the benchmark.
//
// Verified: on 240 matrices (versions 1-40, six inputs each, one all-light, every ECC level) all eight
// scores equal ReferenceScore (a textbook byte-matrix scorer), and the chosen pattern and masked bytes
// equal ModulePlacer.MaskCode's.
//
// Not tuned: the 64x64 transpose is scalar, there is no early-abort checkpoint, popcount is the nibble
// table (Mula), every per-pattern table is full size (about 75 KB per version at version 40), and the
// small versions (1-11), where it loses to the lane-per-pattern tier, run through the same code.
// AVX2 only (Avx2.MoveMask, Avx2.Shuffle, ModulePlacer.XorUnpackRow64Simd).
//
// The [SkipLocalsInit] on DualScorer is a leftover of the scratch harness, kept so the file is what was measured.
// The class allocates no stack buffers (its scratch is instance arrays), and the plan rules the attribute out,
// so it is not carried into the library.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using FeatherQR;
using FeatherQR.Internals.StandardQR;
using BenchmarkDotNet.Attributes;


/// <summary>
/// PROTOTYPE: Standard QR mask selection with every penalty rule computed "vertically".
/// The matrix is held twice: R (row-major words, bit x of row y) and C = transpose(R) (column-major).
/// Column-direction runs / finder windows are vertical in R; row-direction ones are vertical in C,
/// so neither needs a cross-word shift. Masking is linear, so C_p = C ^ transpose(T_p) comes from a per-version table.
/// Rule 2 needs one 1-bit shift per row word. SoA: word k of rows 0..S-1 is contiguous; Vector256 covers 4 rows.
/// </summary>
[SkipLocalsInit]
public sealed class DualScorer
{
    readonly int _size, _version, _w, _s;
    readonly ulong[] _valid;   // [k] bits < size within word k
    readonly ulong[] _maskN1;  // [k] bits < size-1 within word k
    readonly ulong[][] _preR = new ulong[8][];
    readonly ulong[][] _preC = new ulong[8][];
    readonly (int Idx, ulong Bits)[][] _fmtR = new (int, ulong)[32][];
    readonly (int Idx, ulong Bits)[][] _fmtC = new (int, ulong)[32][];
    readonly (int Idx, ulong Bits)[] _verR, _verC;

    // scratch
    readonly ulong[] _r, _c, _rp, _cp, _n, _eq, _cv, _n4, _h;
    readonly ulong[] _blk = new ulong[64];

    public int Size => _size;

    public DualScorer(int version)
    {
        _version = version;
        _size = 17 + 4 * version;
        _w = (_size + 63) / 64;
        _s = Math.Max((_size + 16 + 3) & ~3, 64 * _w);
        _valid = new ulong[_w];
        _maskN1 = new ulong[_w];
        for (var k = 0; k < _w; k++)
        {
            _valid[k] = Mask(_size - 64 * k);
            _maskN1[k] = Mask(_size - 1 - 64 * k);
        }

        var blocked = ModulePlacer.GetLayout(version).BlockedMask;
        for (var p = 0; p < 8; p++)
        {
            _preR[p] = new ulong[_w * _s];
            _preC[p] = new ulong[_w * _s];
            for (var y = 0; y < _size; y++)
            for (var x = 0; x < _size; x++)
            {
                var idx = y * _size + x;
                var isBlocked = (blocked[idx >> 3] & (1 << (idx & 7))) != 0;
                if (!isBlocked && MaskBit(p, y, x))
                {
                    _preR[p][(x >> 6) * _s + y] |= 1ul << (x & 63);
                    _preC[p][(y >> 6) * _s + x] |= 1ul << (y & 63);
                }
            }
        }

        var scratch = new byte[_size * _size];
        for (var e = 0; e < 4; e++)
        for (var p = 0; p < 8; p++)
        {
            Array.Clear(scratch);
            ModulePlacer.PlaceFormat(scratch, _size, QRCodeConstants.GetFormatBits((QREccLevel)e, p));
            (_fmtR[e * 8 + p], _fmtC[e * 8 + p]) = Overlay(scratch);
        }
        Array.Clear(scratch);
        if (version >= 7) ModulePlacer.PlaceVersion(scratch, _size, QRCodeConstants.GetVersionBits(version));
        (_verR, _verC) = Overlay(scratch);

        _r = new ulong[_w * _s]; _c = new ulong[_w * _s]; _rp = new ulong[_w * _s]; _cp = new ulong[_w * _s];
        _n = new ulong[_s + 32]; _eq = new ulong[_s + 32]; _cv = new ulong[_s + 32]; _n4 = new ulong[_s + 32]; _h = new ulong[_s + 32];
    }

    static ulong Mask(int bits) => bits <= 0 ? 0 : bits >= 64 ? ulong.MaxValue : (1ul << bits) - 1;

    static bool MaskBit(int pattern, int row, int col) => pattern switch
    {
        0 => ((row + col) & 1) == 0,
        1 => (row & 1) == 0,
        2 => col % 3 == 0,
        3 => (row + col) % 3 == 0,
        4 => ((row / 2 + col / 3) & 1) == 0,
        5 => (row * col) % 2 + (row * col) % 3 == 0,
        6 => ((row * col % 2 + row * col % 3) & 1) == 0,
        7 => (((row + col) % 2 + row * col % 3) & 1) == 0,
        _ => false,
    };

    ((int, ulong)[], (int, ulong)[]) Overlay(byte[] m)
    {
        var r = new Dictionary<int, ulong>();
        var c = new Dictionary<int, ulong>();
        for (var y = 0; y < _size; y++)
        for (var x = 0; x < _size; x++)
        {
            if (m[y * _size + x] == 0) continue;
            var ri = (x >> 6) * _s + y; r[ri] = r.GetValueOrDefault(ri) | 1ul << (x & 63);
            var ci = (y >> 6) * _s + x; c[ci] = c.GetValueOrDefault(ci) | 1ul << (y & 63);
        }
        return (r.Select(kv => (kv.Key, kv.Value)).ToArray(), c.Select(kv => (kv.Key, kv.Value)).ToArray());
    }

    /// <summary>Scores all eight candidates of <paramref name="buffer"/> (unmasked, format/version areas light), applies the winner in place, returns it.</summary>
    public int Select(Span<byte> buffer, QREccLevel ecc, Span<int> scores = default)
    {
        Pack(buffer);
        foreach (var (i, b) in _verR) _r[i] |= b;
        Transpose();
        foreach (var (i, b) in _verC) _c[i] |= b;

        var best = 0; var bestScore = int.MaxValue;
        for (var p = 0; p < 8; p++)
        {
            Xor(_r, _preR[p], _rp);
            Xor(_c, _preC[p], _cp);
            foreach (var (i, b) in _fmtR[(int)ecc * 8 + p]) _rp[i] |= b;
            foreach (var (i, b) in _fmtC[(int)ecc * 8 + p]) _cp[i] |= b;
            var score = Score();
            if (!scores.IsEmpty) scores[p] = score;
            if (score < bestScore) { bestScore = score; best = p; }
        }

        // apply the winner to the bytes
        var pre = _preR[best];
        for (var y = 0; y < _size; y++)
            for (var k = 0; k < _w; k++)
            {
                var d = pre[k * _s + y];
                if (d != 0) ModulePlacer.XorUnpackRow64Simd(buffer.Slice(y * _size + 64 * k, Math.Min(64, _size - 64 * k)), d);
            }
        return best;
    }

    void Pack(ReadOnlySpan<byte> buffer)
    {
        ref var b = ref MemoryMarshal.GetReference(buffer);
        var total = _size * _size;
        for (var y = 0; y < _size; y++)
        {
            for (var k = 0; k < _w; k++)
            {
                var start = y * _size + 64 * k;
                var len = Math.Min(64, _size - 64 * k);
                ulong w;
                if (start + 64 <= total)
                {
                    w = (uint)~Avx2.MoveMask(Vector256.Equals(Vector256.LoadUnsafe(ref b, (nuint)start), Vector256<byte>.Zero))
                      | (ulong)(uint)~Avx2.MoveMask(Vector256.Equals(Vector256.LoadUnsafe(ref b, (nuint)(start + 32)), Vector256<byte>.Zero)) << 32;
                    w &= Mask(len);
                }
                else
                {
                    w = 0;
                    for (var i = 0; i < len; i++) if (buffer[start + i] != 0) w |= 1ul << i;
                }
                _r[k * _s + y] = w;
            }
        }
    }

    void Transpose()
    {
        Array.Clear(_c);
        Span<ulong> t = _blk;
        for (var kr = 0; kr < _w; kr++)       // row block: rows 64kr..
        for (var kc = 0; kc < _w; kc++)       // column block: word kc of R
        {
            var src = kc * _s + 64 * kr;
            for (var i = 0; i < 64; i++) t[i] = src + i < (kc + 1) * _s ? _r[src + i] : 0;
            Transpose64(t);
            var dst = kr * _s + 64 * kc;     // C word kr, columns 64kc..
            for (var j = 0; j < 64 && 64 * kc + j < _s; j++) _c[dst + j] = t[j];
        }
    }

    /// <summary>In-place 64x64 bit transpose, element (r, c) = bit c of a[r].</summary>
    static void Transpose64(Span<ulong> a)
    {
        var m = 0x00000000FFFFFFFFul;
        for (var j = 32; j != 0; j >>= 1, m ^= m << j)
        {
            for (var k = 0; k < 64; k = ((k | j) + 1) & ~j)
            {
                var t = ((a[k] >> j) ^ a[k + j]) & m;
                a[k] ^= t << j;
                a[k + j] ^= t;
            }
        }
    }

    static void Xor(ulong[] a, ulong[] b, ulong[] d)
    {
        ref var ra = ref MemoryMarshal.GetArrayDataReference(a);
        ref var rb = ref MemoryMarshal.GetArrayDataReference(b);
        ref var rd = ref MemoryMarshal.GetArrayDataReference(d);
        var i = 0;
        for (; i + 4 <= a.Length; i += 4)
            (Vector256.LoadUnsafe(ref ra, (nuint)i) ^ Vector256.LoadUnsafe(ref rb, (nuint)i)).StoreUnsafe(ref rd, (nuint)i);
        for (; i < a.Length; i++) Unsafe.Add(ref rd, i) = Unsafe.Add(ref ra, i) ^ Unsafe.Add(ref rb, i);
    }

    static readonly Vector256<byte> PopLut = Vector256.Create(
        (byte)0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4,
        0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector256<ulong> Pop(Vector256<ulong> v)
    {
        var b = v.AsByte();
        var low = Avx2.Shuffle(PopLut, b & Vector256.Create((byte)0x0F));
        var high = Avx2.Shuffle(PopLut, Vector256.ShiftRightLogical(b.AsUInt16(), 4).AsByte() & Vector256.Create((byte)0x0F));
        return Avx2.SumAbsoluteDifferences(low + high, Vector256<byte>.Zero).AsUInt64();
    }

    int Score()
    {
        var ones = Vector256<ulong>.Zero;
        var twos = Vector256<ulong>.Zero;
        var finders = Vector256<ulong>.Zero;
        var blocks = Vector256<ulong>.Zero;
        var dark = Vector256<ulong>.Zero;
        for (var k = 0; k < _w; k++)
        {
            Vertical(_rp, k, ref ones, ref twos, ref finders, ref dark, rule2: true, ref blocks);
            var dummy = Vector256<ulong>.Zero;
            Vertical(_cp, k, ref ones, ref twos, ref finders, ref dummy, rule2: false, ref blocks);
        }
        var s = Vector256.Sum(ones) + 2 * Vector256.Sum(twos) + 3 * Vector256.Sum(blocks) + 40 * Vector256.Sum(finders);
        return (int)s + Balance((int)Vector256.Sum(dark), _size);
    }

    static int Balance(int blackModules, int size)
    {
        var percent = (blackModules / (double)(size * size)) * 100;
        var prev = Math.Abs((int)Math.Floor(percent / 5) * 5 - 50) / 5;
        var next = Math.Abs((int)Math.Ceiling(percent / 5) * 5 - 50) / 5;
        return Math.Min(prev, next) * 10;
    }

    /// <summary>Runs (rule 1) and finder windows (rule 3) along the array index of word k; rule 2 and the dark count when it is R.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    void Vertical(ulong[] arr, int k, ref Vector256<ulong> ones, ref Vector256<ulong> twos, ref Vector256<ulong> finders, ref Vector256<ulong> dark, bool rule2, ref Vector256<ulong> blocks)
    {
        var size = _size;
        ref var a = ref MemoryMarshal.GetArrayDataReference(arr);
        a = ref Unsafe.Add(ref a, k * _s);
        ref var n = ref MemoryMarshal.GetArrayDataReference(_n);
        ref var eq = ref MemoryMarshal.GetArrayDataReference(_eq); // eq(j) stored at index j + 4; eq(-4..-1) = 0
        ref var cv = ref MemoryMarshal.GetArrayDataReference(_cv);
        ref var n4 = ref MemoryMarshal.GetArrayDataReference(_n4);
        var valid = Vector256.Create(_valid[k]);

        // pass 1: light words, vertical equality, dark count
        Vector256<ulong>.Zero.StoreUnsafe(ref eq);
        var acc = dark;
        for (var i = 0; i < size; i += 4)
        {
            var va = Vector256.LoadUnsafe(ref a, (nuint)i);
            var va1 = Vector256.LoadUnsafe(ref a, (nuint)(i + 1));
            Vector256.AndNot(valid, va).StoreUnsafe(ref n, (nuint)i);
            Vector256.AndNot(valid, va ^ va1).StoreUnsafe(ref eq, (nuint)(i + 4));
            acc += Pop(va);
        }
        if (rule2) dark = acc;
        // nothing past the last row: no light, no equality (eq(size-1) compares against the padding)
        for (var i = size; i < size + 16; i++) Unsafe.Add(ref n, i) = 0;
        for (var i = size - 1; i < size + 16; i++) Unsafe.Add(ref eq, i + 4) = 0;

        // rule 1: v5(i) = eq(i-4..i-1), run starts vs v5(i-1)
        var o = ones; var t2 = twos;
        for (var i = 4; i < size; i += 4)
        {
            var em1 = Vector256.LoadUnsafe(ref eq, (nuint)(i - 1));
            var e0 = Vector256.LoadUnsafe(ref eq, (nuint)i);
            var e1 = Vector256.LoadUnsafe(ref eq, (nuint)(i + 1));
            var e2 = Vector256.LoadUnsafe(ref eq, (nuint)(i + 2));
            var e3 = Vector256.LoadUnsafe(ref eq, (nuint)(i + 3));
            var t = e0 & e1 & e2;
            var cur = t & e3;
            var prev = t & em1;
            o += Pop(cur);
            t2 += Pop(Vector256.AndNot(cur, prev));
        }
        ones = o; twos = t2;

        // rule 3: core (1011101) from row i, four light from row i; windows n4(b)&cv(b+4) | cv(b)&n4(b+7)
        for (var i = 0; i < size; i += 4)
        {
            var a0 = Vector256.LoadUnsafe(ref a, (nuint)i);
            var a2 = Vector256.LoadUnsafe(ref a, (nuint)(i + 2));
            var a3 = Vector256.LoadUnsafe(ref a, (nuint)(i + 3));
            var a4 = Vector256.LoadUnsafe(ref a, (nuint)(i + 4));
            var a6 = Vector256.LoadUnsafe(ref a, (nuint)(i + 6));
            var n0 = Vector256.LoadUnsafe(ref n, (nuint)i);
            var n1 = Vector256.LoadUnsafe(ref n, (nuint)(i + 1));
            var n2 = Vector256.LoadUnsafe(ref n, (nuint)(i + 2));
            var n3 = Vector256.LoadUnsafe(ref n, (nuint)(i + 3));
            var n5 = Vector256.LoadUnsafe(ref n, (nuint)(i + 5));
            (a0 & n1 & a2 & a3 & a4 & n5 & a6).StoreUnsafe(ref cv, (nuint)i);
            (n0 & n1 & n2 & n3).StoreUnsafe(ref n4, (nuint)i);
        }
        for (var i = size; i < size + 12; i++) { Unsafe.Add(ref cv, i) = 0; Unsafe.Add(ref n4, i) = 0; }
        var f = finders;
        for (var b = 0; b < size; b += 4)
        {
            var m = (Vector256.LoadUnsafe(ref n4, (nuint)b) & Vector256.LoadUnsafe(ref cv, (nuint)(b + 4)))
                  | (Vector256.LoadUnsafe(ref cv, (nuint)b) & Vector256.LoadUnsafe(ref n4, (nuint)(b + 7)));
            f += Pop(m);
        }
        finders = f;

        if (rule2)
        {
            // h(y) = M(y,x) == M(y,x+1); block(y) = h(y) & h(y+1) & eq(y)
            ref var h = ref MemoryMarshal.GetArrayDataReference(_h);
            var maskN1 = Vector256.Create(_maskN1[k]);
            ref var next = ref Unsafe.Add(ref a, _s);
            var hasNext = k + 1 < _w;
            for (var i = 0; i < size + 4; i += 4)
            {
                var r = Vector256.LoadUnsafe(ref a, (nuint)i);
                var sh = Vector256.ShiftRightLogical(r, 1);
                if (hasNext) sh |= Vector256.ShiftLeft(Vector256.LoadUnsafe(ref next, (nuint)i), 63);
                Vector256.AndNot(maskN1, r ^ sh).StoreUnsafe(ref h, (nuint)i);
            }
            var bl = blocks;
            for (var i = 0; i < size; i += 4)
            {
                var m = Vector256.LoadUnsafe(ref h, (nuint)i) & Vector256.LoadUnsafe(ref h, (nuint)(i + 1)) & Vector256.LoadUnsafe(ref eq, (nuint)(i + 4));
                bl += Pop(m);
            }
            blocks = bl;
        }
    }

    // ---------- verification helpers ----------

    /// <summary>Textbook byte-matrix penalty (ISO/IEC 18004 8.8.2, the library's balance rounding).</summary>
    public static int ReferenceScore(byte[] q, int size)
    {
        int s1 = 0, s2 = 0, s3 = 0, black = 0;
        for (var y = 0; y < size; y++)
        {
            int runR = 0, runC = 0; var lastR = q[y * size]; var lastC = q[y];
            for (var x = 0; x < size; x++)
            {
                var vr = q[y * size + x]; var vc = q[x * size + y];
                runR = vr == lastR ? runR + 1 : 1; lastR = vr;
                runC = vc == lastC ? runC + 1 : 1; lastC = vc;
                if (runR == 5) s1 += 3; else if (runR > 5) s1++;
                if (runC == 5) s1 += 3; else if (runC > 5) s1++;
            }
        }
        for (var y = 0; y < size - 1; y++)
            for (var x = 0; x < size - 1; x++)
                if (q[y * size + x] == q[y * size + x + 1] && q[y * size + x] == q[(y + 1) * size + x] && q[y * size + x] == q[(y + 1) * size + x + 1]) s2 += 3;
        const uint F = 0b00001011101, B = 0b10111010000, M = 0x7FF;
        for (var y = 0; y < size; y++)
        {
            uint rb = 0, cb = 0;
            for (var x = 0; x < size; x++)
            {
                rb = ((rb << 1) | (q[y * size + x] != 0 ? 1u : 0u)) & M;
                cb = ((cb << 1) | (q[x * size + y] != 0 ? 1u : 0u)) & M;
                if (x >= 10)
                {
                    if (rb == F || rb == B) s3 += 40;
                    if (cb == F || cb == B) s3 += 40;
                }
            }
        }
        foreach (var b in q) if (b != 0) black++;
        return s1 + s2 + s3 + Balance(black, size);
    }

    /// <summary>Candidate p of the unmasked buffer as the final symbol (mask, format, version).</summary>
    public byte[] Candidate(byte[] unmasked, int p, QREccLevel ecc)
    {
        var blocked = ModulePlacer.GetLayout(_version).BlockedMask;
        var q = (byte[])unmasked.Clone();
        for (var y = 0; y < _size; y++)
            for (var x = 0; x < _size; x++)
            {
                var idx = y * _size + x;
                if ((blocked[idx >> 3] & (1 << (idx & 7))) == 0 && MaskBit(p, y, x)) q[idx] ^= 1;
            }
        ModulePlacer.PlaceFormat(q, _size, QRCodeConstants.GetFormatBits(ecc, p));
        if (_version >= 7) ModulePlacer.PlaceVersion(q, _size, QRCodeConstants.GetVersionBits(_version));
        return q;
    }
}

public static class ProtoCheck
{
    /// <summary>The prototype against the textbook scorer (all eight scores) and against the library (winner and masked bytes).</summary>
    public static void Run()
    {
        var rng = new Random(5);
        var checkedCount = 0;
        for (var version = 1; version <= 40; version++)
        {
            var proto = new DualScorer(version);
            var size = proto.Size;
            var layout = ModulePlacer.GetLayout(version);
            for (var trial = 0; trial < 6; trial++)
            {
                var ecc = (QREccLevel)(trial % 4);
                var stream = new byte[layout.FreeModules / 8 + 1];
                rng.NextBytes(stream);
                if (trial == 5) Array.Fill(stream, (byte)0); // degenerate content: long runs everywhere
                var placed = new byte[size * size];
                layout.Template.AsSpan().CopyTo(placed);
                ModulePlacer.PlaceDataWords(placed, layout, stream);

                Span<int> scores = stackalloc int[8];
                var mine = (byte[])placed.Clone();
                var best = proto.Select(mine, ecc, scores);
                for (var p = 0; p < 8; p++)
                {
                    var reference = DualScorer.ReferenceScore(proto.Candidate(placed, p, ecc), size);
                    if (reference != scores[p]) throw new Exception($"v{version} t{trial} p{p}: proto {scores[p]} != reference {reference}");
                }
                var lib = (byte[])placed.Clone();
                var libBest = ModulePlacer.MaskCode(lib, size, version, layout.BlockedMask, ecc);
                if (libBest != best || !lib.AsSpan().SequenceEqual(mine)) throw new Exception($"v{version} t{trial}: library {libBest} vs proto {best}");
                checkedCount++;
            }
        }
        Console.WriteLine($"proto ok: {checkedCount} matrices, 8 scores each equal the textbook scorer, winner and bytes equal the library");
    }
}

/// <summary>Library mask selection vs the dual-orientation prototype, same input, both including the copy of the unmasked matrix.</summary>
public class ProtoStages
{
    [Params(1, 6, 10, 20, 27, 40)]
    public int Version;

    DualScorer _proto = null!;
    ModulePlacer.PlacementLayout _layout = null!;
    byte[] _placed = [];
    byte[] _work = [];
    int _size;

    [GlobalSetup]
    public void Setup()
    {
        _proto = new DualScorer(Version);
        _layout = ModulePlacer.GetLayout(Version);
        _size = _layout.Size;
        var stream = new byte[_layout.FreeModules / 8 + 1];
        new Random(3).NextBytes(stream);
        _placed = new byte[_size * _size];
        _layout.Template.AsSpan().CopyTo(_placed);
        ModulePlacer.PlaceDataWords(_placed, _layout, stream);
        _work = new byte[_size * _size];
    }

    [Benchmark(Baseline = true)]
    public int Library()
    {
        _placed.AsSpan().CopyTo(_work);
        return ModulePlacer.MaskCode(_work, _size, Version, _layout.BlockedMask, QREccLevel.M);
    }

    [Benchmark]
    public int Prototype()
    {
        _placed.AsSpan().CopyTo(_work);
        return _proto.Select(_work, QREccLevel.M);
    }
}
