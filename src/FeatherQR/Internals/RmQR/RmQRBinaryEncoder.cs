using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif

namespace FeatherQR.Internals.RmQr;

/// <summary>
/// rMQR data-codeword stream (ISO/IEC 23941 7.4): 3-bit mode indicator, per-version
/// character count indicator, segment payload bits, terminator (000, shortened at
/// capacity), zero bits to the byte boundary, then alternating 0xEC / 0x11 pad
/// codewords up to the data codeword count. Single data segment; ISO-8859-1 and
/// UTF-8 ECI prefixes are emitted before it when selected by the analyzer.
/// Allocation-free: bits are written straight into the caller's
/// destination and byte-mode transcoding uses a fixed stack budget with a pool
/// fallback.
/// </summary>
/// <remarks>
/// Performance design (benchmark-driven, parity-pinned by
/// <c>RmQRBinaryEncoderParityTest</c> / <c>RmQRBinaryEncoderKernelParityTest</c>):
/// <list type="bullet">
/// <item>Writer state is three raw locals (64-bit MSB-first accumulator, pending bit
/// count, byte position) plus a <c>ref byte</c> into the destination, threaded by
/// ref through inlined helpers. Every flushed word is real data inside the
/// capacity that version selection guarantees, so stores need no per-flush slice
/// bounds checks (measured ~2x over the shared <c>BitWriter</c> at this size).</item>
/// <item>Numeric: 3-digit group values by 64-bit SWAR (one load + one multiply),
/// 9 digits per 30-bit append; on x64 with SSSE3/SSE4.1, 12 digits per iteration
/// via <c>pmaddwd</c> (one group per 8-char load), <c>phaddd</c>, <c>packusdw</c>,
/// <c>pmaddwd</c> into one 40-bit append.</item>
/// <item>Alphanumeric: unchecked 128-entry value table, 2 pairs per 22-bit append;
/// on x64 with SSSE3/SSE4.1, 8 chars per iteration: <c>pshufb</c> offset classes
/// (row 0x2_ specials, row 0x3_ digits + ':', letters) then
/// <c>pmaddubsw</c>(45,1) and <c>pmaddwd</c>(2048,1) into one 44-bit append.</item>
/// <item>Byte (Latin-1): SSE2 narrows 8 chars per 64-bit append; UTF-8 runs as a
/// separate cold function with its own writer (sharing the accumulator by ref with
/// a non-inlined callee would address-expose it, Micro QR lesson).</item>
/// <item>Terminator / alignment are bit-count arithmetic; the pad run is written as
/// 8-byte 0xEC11 pattern stores.</item>
/// </list>
/// The mode switch computes the header for its own segment (one dispatch instead of
/// three chained switches). Vector paths are <c>NET8_0_OR_GREATER</c> + capability
/// gated; netstandard and non-x86 runtimes take the scalar SWAR / table paths.
/// </remarks>
internal static partial class RmQRBinaryEncoder
{
    private const int ModeEci = 0b111;
    private const int EciDesignatorBits = 8;
    private const int EciHeaderBits = RmQRConstants.ModeIndicatorLength + EciDesignatorBits;

    // rMQR byte-mode capacity tops out at 150 bytes (R17x139-M), so any content
    // that passed version selection transcodes into this budget (the branch is on
    // the analyzer's exact UTF-8 byte count, not a per-char worst case); the pool
    // path only exists for callers that bypass selection.
    private const int StackByteBudget = 160;

    /// <summary>
    /// Writes the data codewords for <paramref name="text"/> into
    /// <paramref name="destination"/> and returns the number of codewords written
    /// (always the version × ECC data codeword count). The caller guarantees the
    /// content fits (see <see cref="RmQRVersionSelector"/>).
    /// </summary>
    /// <param name="text">Content, already analyzed.</param>
    /// <param name="version">Target version.</param>
    /// <param name="eccLevel">Target ECC level.</param>
    /// <param name="analysis">Mode, effective charset (Default / ISO-8859-1 narrow, UTF-8 transcode) and encoded data length.</param>
    /// <param name="destination">At least the data codeword count in size.</param>
    public static int EncodeDataCodewords(ReadOnlySpan<char> text, RmQRVersion version, RmQREccLevel eccLevel, in TextAnalysisResult analysis, Span<byte> destination)
    {
        var codewordCount = RmQRConstants.GetDataCodewordCount(version, eccLevel);
        var capacityBits = codewordCount * 8;
        var mode = analysis.EncodingMode;
        if (destination.Length < codewordCount)
            throw new ArgumentException($"Destination too small: {codewordCount} data codewords required, got {destination.Length} bytes.", nameof(destination));
        Debug.Assert(RmQRVersionSelector.GetRequiredBits(version, mode, analysis.DataLength, analysis.EciMode) <= capacityBits, "content must fit (version selection guarantees this)");

        if (analysis.EciMode is not (EciMode.Default or EciMode.Iso8859_1 or EciMode.Utf8))
            throw new ArgumentOutOfRangeException(nameof(analysis), $"Unsupported charset {analysis.EciMode} for rMQR.");

        // Writer state: 64-bit MSB-first accumulator, pending bit count (0..32
        // between appends), byte position; every store below lands inside
        // [0, codewordCount) because all stored bits are real data bits within the
        // capacity asserted above.
        ref var dest = ref MemoryMarshal.GetReference(destination);
        ulong acc = 0;
        var accBits = 0;
        var bytePos = 0;

        // The UTF-8 branch owns a separate cold writer to keep this hot method's
        // accumulator promoted by the JIT; that branch writes its ECI prefix there.
        if (analysis.EciMode != EciMode.Default && !(mode == EncodingMode.Byte && analysis.EciMode == EciMode.Utf8))
            WriteEciHeader(ref dest, ref acc, ref accBits, ref bytePos, analysis.EciMode);

        switch (mode)
        {
            case EncodingMode.Numeric:
                {
                    var countBits = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric);
                    Append(ref dest, ref acc, ref accBits, ref bytePos, (0b001 << countBits) | analysis.DataLength, RmQRConstants.ModeIndicatorLength + countBits);
                    WriteNumeric(ref dest, ref acc, ref accBits, ref bytePos, text, vectorized: true);
                    break;
                }
            case EncodingMode.Alphanumeric:
                {
                    var countBits = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric);
                    Append(ref dest, ref acc, ref accBits, ref bytePos, (0b010 << countBits) | analysis.DataLength, RmQRConstants.ModeIndicatorLength + countBits);
                    WriteAlphanumeric(ref dest, ref acc, ref accBits, ref bytePos, text, vectorized: true);
                    break;
                }
            case EncodingMode.Byte:
                {
                    var countBits = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte);
                    var headerValue = (0b011 << countBits) | analysis.DataLength;
                    var headerBits = RmQRConstants.ModeIndicatorLength + countBits;
                    if (analysis.EciMode is EciMode.Default or EciMode.Iso8859_1)
                    {
                        Append(ref dest, ref acc, ref accBits, ref bytePos, headerValue, headerBits);
                        WriteLatin1(ref dest, ref acc, ref accBits, ref bytePos, text, vectorized: true);
                        break;
                    }
                    if (analysis.EciMode != EciMode.Utf8)
                        throw new ArgumentOutOfRangeException(nameof(analysis), $"Unsupported charset {analysis.EciMode} for rMQR Byte mode.");
                    return EncodeUtf8Codewords(text, codewordCount, capacityBits, headerValue, headerBits, analysis.DataLength, destination);
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(analysis), $"Encoding mode {mode} is not supported by rMQR.");
        }

        Finish(ref dest, acc, accBits, bytePos, codewordCount, capacityBits);
        return codewordCount;
    }

    /// <summary>
    /// Original no-ECI hot path, kept separate so the extra ECI validation and
    /// header branches do not change the JIT layout for ASCII production calls.
    /// The caller guarantees <see cref="TextAnalysisResult.EciMode"/> is Default.
    /// </summary>
    internal static int EncodeDataCodewordsWithoutEci(ReadOnlySpan<char> text, RmQRVersion version, RmQREccLevel eccLevel, in TextAnalysisResult analysis, Span<byte> destination)
    {
        Debug.Assert(analysis.EciMode == EciMode.Default);
        var codewordCount = RmQRConstants.GetDataCodewordCount(version, eccLevel);
        var capacityBits = codewordCount * 8;
        var mode = analysis.EncodingMode;
        if (destination.Length < codewordCount)
            throw new ArgumentException($"Destination too small: {codewordCount} data codewords required, got {destination.Length} bytes.", nameof(destination));
        Debug.Assert(RmQRVersionSelector.GetRequiredBits(version, mode, analysis.DataLength) <= capacityBits, "content must fit (version selection guarantees this)");

        ref var dest = ref MemoryMarshal.GetReference(destination);
        ulong acc = 0;
        var accBits = 0;
        var bytePos = 0;

        switch (mode)
        {
            case EncodingMode.Numeric:
                {
                    var countBits = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric);
                    Append(ref dest, ref acc, ref accBits, ref bytePos, (0b001 << countBits) | analysis.DataLength, RmQRConstants.ModeIndicatorLength + countBits);
                    WriteNumeric(ref dest, ref acc, ref accBits, ref bytePos, text, vectorized: true);
                    break;
                }
            case EncodingMode.Alphanumeric:
                {
                    var countBits = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric);
                    Append(ref dest, ref acc, ref accBits, ref bytePos, (0b010 << countBits) | analysis.DataLength, RmQRConstants.ModeIndicatorLength + countBits);
                    WriteAlphanumeric(ref dest, ref acc, ref accBits, ref bytePos, text, vectorized: true);
                    break;
                }
            case EncodingMode.Byte:
                {
                    var countBits = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte);
                    Append(ref dest, ref acc, ref accBits, ref bytePos, (0b011 << countBits) | analysis.DataLength, RmQRConstants.ModeIndicatorLength + countBits);
                    WriteLatin1(ref dest, ref acc, ref accBits, ref bytePos, text, vectorized: true);
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(analysis), $"Encoding mode {mode} is not supported by rMQR.");
        }

        Finish(ref dest, acc, accBits, bytePos, codewordCount, capacityBits);
        return codewordCount;
    }

    // ---------------------------------------------------------------
    // Segment writers. Internal + AggressiveInlining: the production call sites
    // above inline them (so the writer locals stay enregistered), while the kernel
    // parity tests call them directly with vectorized = true / false.
    // ---------------------------------------------------------------

    /// <summary>
    /// Numeric segment: 10/7/4 bits per 3/2/1 digits. Contract: digits are '0'-'9'
    /// (validated by the analyzer); the SWAR / SIMD group math produces a wrong but
    /// memory-safe stream otherwise.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteNumeric(ref byte dest, ref ulong acc, ref int accBits, ref int bytePos, ReadOnlySpan<char> digits, bool vectorized)
    {
        Debug.Assert(AllNumeric(digits), "caller must validate the numeric alphabet");

        ref var c = ref MemoryMarshal.GetReference(digits);
        var length = digits.Length;
        var i = 0;

#if NET8_0_OR_GREATER
        if (vectorized && Sse41.IsSupported && Ssse3.IsSupported)
        {
            // pmaddwd pairs are fixed at lanes (0,1),(2,3),... so a 3-digit group cannot
            // straddle a pair: one group per 8-char load (weights 100,10 | 1,0 | 0,0 | 0,0)
            // from four loads at i, i+3, i+6, i+9. The last load reads chars i+9..i+16,
            // hence the i+17 <= length guard; the SWAR loops below take the tail.
            //   phaddd x3      -> [G0, G1, G2, G3] (each group + '0' bias 5328)
            //   packusdw       -> 16-bit lanes
            //   pmaddwd(1024,1)-> [G0*1024+G1, G2*1024+G3] = two 20-bit halves + 5328*1025 each
            ref var t = ref Unsafe.As<char, short>(ref c);
            var w = Vector128.Create((short)100, 10, 1, 0, 0, 0, 0, 0);
            var pw = Vector128.Create((short)1024, 1, 1024, 1, 1024, 1, 1024, 1);
            for (; i + 17 <= length; i += 12)
            {
                var a = Sse2.MultiplyAddAdjacent(Vector128.LoadUnsafe(ref t, (nuint)i), w);
                var b = Sse2.MultiplyAddAdjacent(Vector128.LoadUnsafe(ref t, (nuint)(i + 3)), w);
                var c2 = Sse2.MultiplyAddAdjacent(Vector128.LoadUnsafe(ref t, (nuint)(i + 6)), w);
                var d = Sse2.MultiplyAddAdjacent(Vector128.LoadUnsafe(ref t, (nuint)(i + 9)), w);
                var s = Ssse3.HorizontalAdd(Ssse3.HorizontalAdd(a, b), Ssse3.HorizontalAdd(c2, d));
                var p = Sse41.PackUnsignedSaturate(s, s);
                var q = Sse2.MultiplyAddAdjacent(p.AsInt16(), pw).AsUInt64().ToScalar() - SimdNumericBiasPair;
                AppendWide(ref dest, ref acc, ref accBits, ref bytePos, ((q & 0xFFFFF) << 20) | (q >> 32), 40);
            }
        }
#endif

        // 9 digits -> one 30-bit append; the SWAR load at i+6 reads chars i+6..i+9,
        // so a 10th char must exist: i + 9 < length
        for (; i + 9 < length; i += 9)
        {
            var g = (SwarGroup(ref c, i) << 20) | (SwarGroup(ref c, i + 3) << 10) | SwarGroup(ref c, i + 6);
            Append(ref dest, ref acc, ref accBits, ref bytePos, g, 30);
        }
        // 3 digits per append while a 4th readable char exists
        for (; i + 3 < length; i += 3)
        {
            Append(ref dest, ref acc, ref accBits, ref bytePos, SwarGroup(ref c, i), 10);
        }
        // scalar tail: 0-3 digits left, no headroom for an 8-byte load; the per-digit
        // '0' offsets fold into one subtract (5328 = '0' * 111, 528 = '0' * 11)
        if (i + 2 < length)
        {
            Append(ref dest, ref acc, ref accBits, ref bytePos, digits[i] * 100 + digits[i + 1] * 10 + digits[i + 2] - 5328, 10);
            i += 3;
        }
        if (i + 1 < length)
        {
            Append(ref dest, ref acc, ref accBits, ref bytePos, digits[i] * 10 + digits[i + 1] - 528, 7);
        }
        else if (i < length)
        {
            Append(ref dest, ref acc, ref accBits, ref bytePos, digits[i] - '0', 4);
        }
    }

    /// <summary>
    /// Alphanumeric segment: 11 bits per pair (45·a + b), 6 bits for a trailing
    /// character. Contract: the alphabet is validated by the analyzer; the
    /// <c>&amp; 0x7F</c> table index is memory-safe for any input but does not
    /// sanitize.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteAlphanumeric(ref byte dest, ref ulong acc, ref int accBits, ref int bytePos, ReadOnlySpan<char> chars, bool vectorized)
    {
        Debug.Assert(AllAlphanumeric(chars), "caller must validate the alphanumeric alphabet");

        var i = 0;

#if NET8_0_OR_GREATER
        if (vectorized && Sse41.IsSupported && Ssse3.IsSupported)
        {
            // value = char + offset, offset chosen by the char's row (high nibble):
            //   0x2_: ' '=36 (+4), '$'=37 '%'=38 (+1), '*'=39 '+'=40 (-3), '-'=41 '.'=42 '/'=43 (-4)
            //   0x3_: '0'..'9' -> 0..9 (-48), ':' -> 44 (-14)
            //   0x4_/0x5_: 'A'..'Z' -> 10..35 (-55)
            // Two pshufb tables indexed by the low nibble cover rows 0x2_/0x3_, one
            // constant covers letters; then pmaddubsw(45,1) forms the 4 pair values and
            // pmaddwd(2048,1) two 22-bit quads -> one 44-bit append per 8 chars.
            ref var t = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(chars));
            var row2 = Vector128.Create((sbyte)4, 0, 0, 0, 1, 1, 0, 0, 0, 0, -3, -3, 0, -4, -4, -4);
            var row3 = Vector128.Create((sbyte)-48, -48, -48, -48, -48, -48, -48, -48, -48, -48, -14, 0, 0, 0, 0, 0);
            var pairW = Vector128.Create((sbyte)45, 1, 45, 1, 45, 1, 45, 1, 45, 1, 45, 1, 45, 1, 45, 1);
            var quadW = Vector128.Create((short)2048, 1, 2048, 1, 2048, 1, 2048, 1);
            var three = Vector128.Create((byte)3);
            var lowNibble = Vector128.Create((byte)0x0F);
            var minus55 = Vector128.Create((sbyte)-55);
            for (; i + 8 <= chars.Length; i += 8)
            {
                var v = Vector128.LoadUnsafe(ref t, (nuint)i);
                var b = Sse2.PackUnsignedSaturate(v.AsInt16(), v.AsInt16());   // 8 chars -> 8 bytes (duplicated)
                var lo = b & lowNibble;
                var hi = (b.AsUInt16() >> 4).AsByte() & lowNibble;
                var off = Ssse3.Shuffle(row2, lo.AsSByte());
                var off3 = Ssse3.Shuffle(row3, lo.AsSByte());
                off = Sse41.BlendVariable(off, off3, Sse2.CompareEqual(hi, three).AsSByte());
                off = Sse41.BlendVariable(off, minus55, Sse2.CompareGreaterThan(hi.AsSByte(), three.AsSByte()));
                var values = (b.AsSByte() + off).AsByte();                                    // 0..44
                var pairs = Ssse3.MultiplyAddAdjacent(values, pairW);                        // 4 x (v0*45 + v1), 16-bit
                var quads = Sse2.MultiplyAddAdjacent(pairs, quadW).AsUInt64().ToScalar();    // [p0*2048+p1 | (p2*2048+p3) << 32]
                AppendWide(ref dest, ref acc, ref accBits, ref bytePos, ((quads & 0x3FFFFF) << 22) | (quads >> 32), 44);
            }
        }
#endif

        // 4 chars = 2 pairs -> one 22-bit append
        for (; i + 3 < chars.Length; i += 4)
        {
            var p0 = AlnumValues[chars[i] & 0x7F] * 45 + AlnumValues[chars[i + 1] & 0x7F];
            var p1 = AlnumValues[chars[i + 2] & 0x7F] * 45 + AlnumValues[chars[i + 3] & 0x7F];
            Append(ref dest, ref acc, ref accBits, ref bytePos, (p0 << 11) | p1, 22);
        }
        if (i + 1 < chars.Length)
        {
            Append(ref dest, ref acc, ref accBits, ref bytePos, AlnumValues[chars[i] & 0x7F] * 45 + AlnumValues[chars[i + 1] & 0x7F], 11);
            i += 2;
        }
        if (i < chars.Length)
        {
            Append(ref dest, ref acc, ref accBits, ref bytePos, AlnumValues[chars[i] & 0x7F], 6);
        }
    }

    /// <summary>
    /// Byte segment for Default / ISO-8859-1 text (every char ≤ 0xFF, validated by
    /// the analyzer): 8 chars narrow to one 64-bit append on SSE2, 16 chars to two on
    /// any other 128-bit vector target (ARM64, WASM), scalar otherwise.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteLatin1(ref byte dest, ref ulong acc, ref int accBits, ref int bytePos, ReadOnlySpan<char> text, bool vectorized)
    {
        var i = 0;
#if NET8_0_OR_GREATER
        if (vectorized && Sse2.IsSupported)
        {
            // PackUnsignedSaturate narrows 16-bit lanes to bytes (the same vector twice
            // duplicates the result; ToScalar takes the low 8 bytes). Saturation cannot
            // corrupt data because every lane is <= 0xFF. The pack puts the FIRST char in
            // the LOWEST byte while the stream wants it first, so byte-swap.
            ref var t = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(text));
            for (; i + 8 <= text.Length; i += 8)
            {
                var v = Vector128.LoadUnsafe(ref t, (nuint)i);
                var packed = Sse2.PackUnsignedSaturate(v.AsInt16(), v.AsInt16()).AsUInt64().ToScalar();
                Append64(ref dest, ref acc, ref accBits, ref bytePos, BinaryPrimitives.ReverseEndianness(packed));
            }
        }
        else if (vectorized && Vector128.IsHardwareAccelerated)
        {
            // Targets with 128-bit vectors but no SSE2 (ARM64 NEON, WASM) previously fell
            // all the way to the per-character loop below, which is one accumulator update
            // per byte — measured 9x slower than this on Apple M2 (RmQrSegmentWriteArm
            // findings log). Vector128.Narrow is the portable form of the same idea (XTN +
            // XTN2 on ARM64); it truncates rather than saturates, which is exactly right
            // because the analyzer has already proved every char <= 0xFF.
            //
            // Two vectors per iteration rather than one: narrowing a vector against
            // itself discards half the result, so 16 chars per iteration halves the
            // vector work per character. The two Append64 calls are NOT independent —
            // they chain through acc/accBits/bytePos — so the win is the halved narrow
            // count, not store-level parallelism.
            //
            // Note the two vector tiers disagree out of contract: PackUnsignedSaturate
            // above saturates a char > 0xFF to 0xFF, Vector128.Narrow truncates it, as
            // the scalar loop below does — so a char this method should never see would
            // produce a DIFFERENT payload per architecture rather than a uniformly wrong
            // one. The precondition is enforced where it is decided, not here: explicit
            // ECI is rejected by CharacterSets.IsValidISO88591, and auto-detection is
            // pinned by TextAnalyzerAdvSimdParityTest.AutoDetect_NeverDeclaresLatin1_ForCharsAboveFF.
            ref var t = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(text));
            for (; i + 16 <= text.Length; i += 16)
            {
                var packed = Vector128.Narrow(
                    Vector128.LoadUnsafe(ref t, (nuint)i),
                    Vector128.LoadUnsafe(ref t, (nuint)(i + 8))).AsUInt64();
                Append64(ref dest, ref acc, ref accBits, ref bytePos, BinaryPrimitives.ReverseEndianness(packed.GetElement(0)));
                Append64(ref dest, ref acc, ref accBits, ref bytePos, BinaryPrimitives.ReverseEndianness(packed.GetElement(1)));
            }
            if (i + 8 <= text.Length)
            {
                var v = Vector128.LoadUnsafe(ref t, (nuint)i);
                Append64(ref dest, ref acc, ref accBits, ref bytePos, BinaryPrimitives.ReverseEndianness(Vector128.Narrow(v, v).AsUInt64().ToScalar()));
                i += 8;
            }
        }
#endif
        for (; i < text.Length; i++)
        {
            Append(ref dest, ref acc, ref accBits, ref bytePos, (byte)text[i], 8);
        }
    }

    /// <summary>
    /// Byte segment for UTF-8 text: ECI assignment 26 + UTF-8 transcode on a
    /// private writer. <paramref name="byteCount"/> is the analyzer's exact encoded
    /// length, which picks the stack budget for every payload that passed version
    /// selection. Not inlined by design (see the class remarks).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int EncodeUtf8Codewords(ReadOnlySpan<char> text, int codewordCount, int capacityBits, int headerValue, int headerBits, int byteCount, Span<byte> destination)
    {
        ref var dest = ref MemoryMarshal.GetReference(destination);
        ulong acc = 0;
        var accBits = 0;
        var bytePos = 0;
        WriteEciHeader(ref dest, ref acc, ref accBits, ref bytePos, EciMode.Utf8);
        Append(ref dest, ref acc, ref accBits, ref bytePos, headerValue, headerBits);

        if (byteCount <= StackByteBudget)
        {
            Span<byte> utf8 = stackalloc byte[StackByteBudget];
            var length = GetUtf8Bytes(text, utf8);
            WriteBytes(ref dest, ref acc, ref accBits, ref bytePos, utf8.Slice(0, length));
        }
        else
        {
            var maxByteCount = text.Length * 3; // UTF-16 → UTF-8 worst case is 3 bytes per char (4-byte sequences come from surrogate pairs)
            var rented = ArrayPool<byte>.Shared.Rent(maxByteCount);
            try
            {
                var length = GetUtf8Bytes(text, rented);
                WriteBytes(ref dest, ref acc, ref accBits, ref bytePos, rented.AsSpan(0, length));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        Finish(ref dest, acc, accBits, bytePos, codewordCount, capacityBits);
        return codewordCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteBytes(ref byte dest, ref ulong acc, ref int accBits, ref int bytePos, ReadOnlySpan<byte> bytes)
    {
        var i = 0;
        for (; i + 8 <= bytes.Length; i += 8)
        {
            Append64(ref dest, ref acc, ref accBits, ref bytePos, BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(i)));
        }
        for (; i < bytes.Length; i++)
        {
            Append(ref dest, ref acc, ref accBits, ref bytePos, bytes[i], 8);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteEciHeader(ref byte dest, ref ulong acc, ref int accBits, ref int bytePos, EciMode eciMode)
        => Append(ref dest, ref acc, ref accBits, ref bytePos, (ModeEci << EciDesignatorBits) | (int)eciMode, EciHeaderBits);

    private static int GetUtf8Bytes(ReadOnlySpan<char> text, Span<byte> destination)
    {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
        return Encoding.UTF8.GetBytes(text, destination);
#else
        // netstandard2.0 has no span overload; this path is the documented
        // exception to the allocation-free contract on that TFM.
        var bytes = Encoding.UTF8.GetBytes(text.ToString());
        bytes.CopyTo(destination);
        return bytes.Length;
#endif
    }

    // ---------------------------------------------------------------
    // Group / value helpers
    // ---------------------------------------------------------------

    // SWAR (SIMD Within A Register): one ulong holds four 16-bit UTF-16 lanes,
    // turning a 3-digit group into its numeric value with a single 64-bit
    // multiply instead of per-digit subtracts and multiplies.
    //
    //   chunk = 4 chars, unaligned 8-byte read; "1234" lays out little-endian as
    //           lanes  '1' | '2' | '3' | '4'
    //   chunk - SwarDigitBias   subtracts '0' from every lane:
    //           lanes   1  |  2  |  3  |  4
    //   * SwarGroupMagic (100<<32 | 10<<16 | 1): the partial products
    //           d0*100<<32, d1*10<<32, d2*1<<32
    //   all land in bit window [32..47], so that window holds d0*100 + d1*10 + d2.
    //   The group value is <= 999 < 2^10 (mask 0x3FF); lower lanes cannot carry
    //   into the window, and every product of the 4th lane lands at bit 48+ or
    //   overflows out of the 64-bit register, so its value never matters.
    //
    // Only 3 digits contribute, but the 8-byte load spans 4 chars, callers must
    // guarantee one readable char beyond each group (see the loop guards above).
    private const ulong SwarDigitBias = 0x0030_0030_0030_0030UL;
    private const ulong SwarGroupMagic = (100UL << 32) | (10UL << 16) | 1UL;

#if NET8_0_OR_GREATER
    // pmaddwd(1024,1) over the biased 16-bit groups leaves 5328 * 1025 in each 32-bit half.
    private const ulong SimdNumericBiasPair = (5328UL * 1025UL) | ((5328UL * 1025UL) << 32);
#endif

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SwarGroup(ref char c, int i)
    {
        if (BitConverter.IsLittleEndian)
        {
            var chunk = Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<char, byte>(ref Unsafe.Add(ref c, i)));
            return (int)(((chunk - SwarDigitBias) * SwarGroupMagic) >> 32) & 0x3FF;
        }

        // Big-endian runtimes: the little-endian lane layout above does not hold and a
        // whole-ulong byte reversal would also swap the bytes INSIDE each 16-bit char
        // lane, so fall back to scalar group math. IsLittleEndian is a JIT-time
        // constant; this branch vanishes from little-endian codegen.
        return Unsafe.Add(ref c, i) * 100 + Unsafe.Add(ref c, i + 1) * 10 + Unsafe.Add(ref c, i + 2) - 5328; // folded bias: 5328 = '0' * 111
    }

    // Direct value table replacing the per-char CharacterSets.GetAlphanumericValue
    // call (and its two compare+throw branches). QR alphanumeric values are 0-44,
    // so byte entries suffice; invalid slots hold 0 and are never read because the
    // caller validates the alphabet. Lookups index as AlnumValues[c & 0x7F]: the
    // 7-bit mask keeps ANY UTF-16 char inside 0-127, so the access is memory-safe
    // without a bounds check even for out-of-alphabet input.
    private static ReadOnlySpan<byte> AlnumValues =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        36, 0, 0, 0, 37, 38, 0, 0, 0, 0, 39, 40, 0, 41, 42, 43,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 44, 0, 0, 0, 0, 0,
        0, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24,
        25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    // Debug-only contract checks (evaluated solely inside Debug.Assert).
    private static bool AllNumeric(ReadOnlySpan<char> chars)
    {
        foreach (var c in chars)
        {
            if (!CharacterSets.IsNumeric(c)) return false;
        }
        return true;
    }

    private static bool AllAlphanumeric(ReadOnlySpan<char> chars)
    {
        foreach (var c in chars)
        {
            if (!CharacterSets.IsAlphanumeric(c)) return false;
        }
        return true;
    }

    // ---------------------------------------------------------------
    // Writer: 64-bit MSB-first accumulator; a 32-bit big-endian word is stored once
    // 32+ bits are pending, so 0..32 bits stay pending between appends. Stores use
    // ref arithmetic without per-store range checks: every stored bit is real data
    // (or terminator/alignment/pad) inside the capacity, so bytePos + width never
    // exceeds the codeword count.
    // ---------------------------------------------------------------

    /// <summary>Appends the low <paramref name="bitCount"/> bits (1-32) of <paramref name="value"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Append(ref byte dest, ref ulong acc, ref int accBits, ref int bytePos, int value, int bitCount)
    {
        Debug.Assert(bitCount >= 1 && bitCount <= 32, "bitCount must be between 1 and 32");
        Debug.Assert(accBits <= 32, "at most 32 bits may be pending between appends");

        var v = (ulong)(uint)value & ((1UL << bitCount) - 1);
        acc |= v << (64 - accBits - bitCount);
        accBits += bitCount;
        if (accBits >= 32)
        {
            StoreBigEndian(ref dest, bytePos, (uint)(acc >> 32));
            bytePos += 4;
            acc <<= 32;
            accBits -= 32;
        }
    }

    /// <summary>
    /// Appends 1-56 pre-masked bits (the SIMD numeric / alphanumeric quads); unlike
    /// <see cref="Append"/> the pending bits plus the value may exceed 64 bits, in
    /// which case a full 8-byte word is stored and the remainder stays pending.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AppendWide(ref byte dest, ref ulong acc, ref int accBits, ref int bytePos, ulong value, int bitCount)
    {
        Debug.Assert(bitCount >= 1 && bitCount <= 56, "bitCount must be between 1 and 56");
        Debug.Assert(value >> bitCount == 0, "value must be pre-masked to bitCount bits");

        var end = accBits + bitCount;
        if (end <= 64)
        {
            acc |= value << (64 - end);
            accBits = end;
            if (accBits >= 32)
            {
                StoreBigEndian(ref dest, bytePos, (uint)(acc >> 32));
                bytePos += 4;
                acc <<= 32;
                accBits -= 32;
            }
        }
        else
        {
            StoreBigEndian(ref dest, bytePos, acc | (value >> (end - 64)));
            bytePos += 8;
            accBits = end - 64;
            acc = value << (128 - end);
        }
    }

    /// <summary>Appends 64 bits (MSB first): pending bits + the head of the value are stored as one 8-byte word, the displaced tail stays pending.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Append64(ref byte dest, ref ulong acc, ref int accBits, ref int bytePos, ulong value)
    {
        StoreBigEndian(ref dest, bytePos, acc | (value >> accBits));
        bytePos += 8;
        acc = accBits == 0 ? 0UL : value << (64 - accBits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreBigEndian(ref byte dest, int bytePos, uint value)
        => Unsafe.WriteUnaligned(ref Unsafe.Add(ref dest, bytePos), BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreBigEndian(ref byte dest, int bytePos, ulong value)
        => Unsafe.WriteUnaligned(ref Unsafe.Add(ref dest, bytePos), BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value);

    /// <summary>
    /// Terminator (000, shortened at capacity) + zero bits to the byte boundary as
    /// bit-count arithmetic (the accumulator's unused low bits are already zero),
    /// drain of the pending whole bytes, then alternating 0xEC / 0x11 pads written
    /// as 8-byte pattern stores (pads always start with 0xEC).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Finish(ref byte dest, ulong acc, int accBits, int bytePos, int codewordCount, int capacityBits)
    {
        var bitPos = bytePos * 8 + accBits;
        var terminator = Math.Min(RmQRConstants.TerminatorLength, capacityBits - bitPos);
        bitPos += terminator;
        accBits += terminator + ((8 - (bitPos & 7)) & 7);   // <= 32 + 3 + 7 pending, drained below
        Debug.Assert((accBits & 7) == 0, "pending bits must be byte aligned after the terminator");

        while (accBits >= 8)
        {
            Unsafe.Add(ref dest, bytePos++) = (byte)(acc >> 56);
            acc <<= 8;
            accBits -= 8;
        }

        var pads = codewordCount - bytePos;
        Debug.Assert(pads >= 0, "the stream must not exceed the data codeword count");
        var k = 0;
        for (; k + 8 <= pads; k += 8)
        {
            // little-endian ulong 0x11EC11EC11EC11EC lays out as bytes EC 11 EC 11 ...
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dest, bytePos + k), BitConverter.IsLittleEndian ? 0x11EC11EC11EC11ECUL : 0xEC11EC11EC11EC11UL);
        }
        for (; k < pads; k++)
        {
            Unsafe.Add(ref dest, bytePos + k) = (k & 1) == 0 ? (byte)0xEC : (byte)0x11;
        }
    }
}
