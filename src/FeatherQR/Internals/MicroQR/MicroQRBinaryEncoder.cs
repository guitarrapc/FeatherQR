using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace FeatherQR.Internals.MicroQR;

/// <summary>
/// Encodes text into Micro QR data codewords: mode indicator, character count, data bits, terminator and padding (ISO/IEC 18004 Micro QR bit stream rules).
/// </summary>
/// <remarks>
/// Micro QR differences from Standard QR handled here:
/// <list type="bullet">
/// <item>Mode indicator is 0-3 bits wide (version − 1); M1 has none.</item>
/// <item>Character count indicator is 3-6 bits wide (version-dependent).</item>
/// <item>Terminator is 3/5/7/9 zero bits, shortened at capacity.</item>
/// <item>No ECI: non-Latin-1 text is emitted as raw UTF-8 bytes in Byte mode
/// (Micro QR has no ECI mode; readers detect UTF-8 heuristically).</item>
/// <item>M1/M3 capacities end on a half byte: the final data codeword is 4 bits
/// stored in the byte's high nibble with a forced-zero low nibble, and a final 4-bit pad codeword is 0000 (never part of the 0xEC/0x11 cycle).</item>
/// </list>
/// Reed-Solomon ECC is computed by the shared <see cref="BinaryEncoders.EccBinaryEncoder"/> over the returned codeword bytes as-is (the half codeword participates as its high-nibble byte value).
///
/// Performance design: Micro QR data codewords top out at 16 bytes (M4-L), so the whole bit stream is accumulated MSB-first in two ulong registers (hi = output bytes 0-7, lo = bytes 8-15) with no intermediate buffer.
/// The UTF-8 fallback runs as a fully separate cold function because sharing the accumulator by ref with a non-inlined callee would address-expose it and force every hot-path append through the stack.
/// Terminator/alignment zeros are position arithmetic only, and the 0xEC/0x11 pad run is OR-ed in from a phase-selected 128-bit constant.
///
/// Byte-mode Latin-1 SIMD tiers: x64 narrows 8 chars per SSE2 pack into one 64-bit append behind the scalar OR-reduction validity scan.
/// ARM64 goes further, because payloads never exceed 15 chars, one aligned load plus one end-overlapped load cover the whole text, serving both the validity check (UMAXV) and the appends (XTN narrow; the overlapped vector's low bytes are the tail), with a 64-bit SWAR variant of the same overlap for 4-7 chars.
/// </remarks>
internal static partial class MicroQRBinaryEncoder
{
    /// <summary>
    /// Encodes <paramref name="text"/> into data codewords (padding included) and writes them to <paramref name="destination"/>.
    /// </summary>
    /// <param name="text">Input text; must satisfy the mode's alphabet and the version's capacity (validated by the caller).</param>
    /// <param name="version">Micro QR version (M1-M4).</param>
    /// <param name="eccLevel">Error correction level (valid for the version).</param>
    /// <param name="mode">Data encoding mode (Numeric / Alphanumeric / Byte).</param>
    /// <param name="destination">Destination for the data codewords. When at least 16 bytes long the full accumulator is stored (bytes beyond the returned count are zero); shorter destinations receive exactly the codeword bytes.</param>
    /// <returns>Number of codeword bytes written (= data codeword count for the version/ECC).</returns>
    public static int EncodeDataCodewords(ReadOnlySpan<char> text, MicroQRVersion version, MicroQREccLevel eccLevel, EncodingMode mode, Span<byte> destination)
    {
        var capacityBits = MicroQRConstants.GetDataBitCapacity(version, eccLevel);
        var codewordCount = MicroQRConstants.GetDataCodewordCount(version, eccLevel);
        Debug.Assert(capacityBits > 0, "invalid version/ECC combination must be rejected by the caller");

        // Pipeline:
        //   1. mode + count header  ->  2. mode-specific data bits
        //   -> (128-bit accumulator hi/lo)
        //   -> 3. terminator position adjustment -> 4. 0xEC/0x11 padding by mask
        //   -> 5. big-endian store
        // (steps 3-5 live in FinishAndStore)

        // 1. Mode indicator (version - 1 bits; M1 carries numeric implicitly),
        //    fused with the character count indicator into a single append.
        var countBits = MicroQRConstants.GetCountIndicatorLength(version, mode);
        var headerBits = (int)version - 1 + countBits;
        var modeValue = MicroQRConstants.GetModeIndicatorValue(mode) << countBits;

        // 128-bit fixed accumulator: Micro QR data codewords top out at 16 bytes
        // (M4-L), so the whole bit stream fits in two registers, MSB-first:
        //   hi = output bytes 0-7, lo = bytes 8-15,
        //   pos = bits written from the start of the stream.
        // Both words start at zero, so all-zero fields are never written, the
        // terminator, byte alignment, the forced-zero low nibble of the M1/M3
        // half codeword, and unused tail bytes fall out by advancing pos only
        // (where the previous BitWriter design issued explicit Write(0, n) calls).
        ulong hi = 0, lo = 0;
        var pos = 0;

        // 2. Character count indicator + data bits.
        switch (mode)
        {
            case EncodingMode.Numeric:
                Append(ref hi, ref lo, ref pos, modeValue | text.Length, headerBits);
                WriteNumericData(ref hi, ref lo, ref pos, text);
                break;

            case EncodingMode.Alphanumeric:
                Append(ref hi, ref lo, ref pos, modeValue | text.Length, headerBits);
                WriteAlphanumericData(ref hi, ref lo, ref pos, text);
                break;

            case EncodingMode.Byte:
                {
#if NET8_0_OR_GREATER
                if (AdvSimd.Arm64.IsSupported && text.Length >= 8)
                {
                    // NEON tier: byte payloads top out at 15 chars (M4-L), so ONE
                    // aligned 8-char load plus ONE load overlapped to END at the
                    // last char cover the whole payload. The same two vectors
                    // serve BOTH the Latin-1 validity check (OR + UMAXV replacing
                    // the serial per-char OR chain) and the data appends (XTN
                    // narrow -> big-endian ulong): chars 0-7 land in one Append64,
                    // and the overlapped vector's low (length - 8) bytes are
                    // exactly chars 8..length, landing in one masked AppendWide.
                    var length = text.Length;
                    Debug.Assert(length <= 15, "byte payloads beyond any Micro QR capacity must be rejected by the caller");
                    ref var t0 = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(text));
                    var v0 = Vector128.LoadUnsafe(ref t0);
                    var v1 = Vector128.LoadUnsafe(ref t0, (nuint)(length - 8));
                    if (AdvSimd.Arm64.MaxAcross(v0 | v1).ToScalar() > 0xFF)
                    {
                        return EncodeUtf8Codewords(text, version, capacityBits, codewordCount, modeValue, headerBits, destination);
                    }

                    Append(ref hi, ref lo, ref pos, modeValue | length, headerBits);
                    Append64(ref hi, ref lo, ref pos,
                        BinaryPrimitives.ReverseEndianness(AdvSimd.ExtractNarrowingLower(v0).AsUInt64().ToScalar()));
                    var tailBits = (length - 8) * 8;
                    if (tailBits > 0)
                    {
                        var tail = BinaryPrimitives.ReverseEndianness(AdvSimd.ExtractNarrowingLower(v1).AsUInt64().ToScalar());
                        AppendWide(ref hi, ref lo, ref pos, tail & ((1UL << tailBits) - 1), tailBits);
                    }
                    break;
                }
                if (AdvSimd.Arm64.IsSupported && text.Length >= 4)
                {
                    // 64-bit SWAR tier for 4-7 chars (below the vector grain):
                    // the same overlap idea on two 4-char ulong reads. Validity
                    // is one AND against the per-lane high-byte mask; SwarPack4
                    // compacts the four 16-bit lanes to 4 bytes. Gated to ARM64
                    // alongside the NEON tier (measured there; x64 keeps its
                    // shipped SSE2 shape below), the lane math is little-endian,
                    // which every AdvSimd runtime satisfies.
                    var length = text.Length;
                    ref var b0 = ref Unsafe.As<char, byte>(ref MemoryMarshal.GetReference(text));
                    var u0 = Unsafe.ReadUnaligned<ulong>(ref b0);
                    var u1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b0, (length - 4) * 2));
                    if (((u0 | u1) & 0xFF00_FF00_FF00_FF00UL) != 0)
                    {
                        return EncodeUtf8Codewords(text, version, capacityBits, codewordCount, modeValue, headerBits, destination);
                    }

                    Append(ref hi, ref lo, ref pos, modeValue | length, headerBits);
                    Append(ref hi, ref lo, ref pos, (int)BinaryPrimitives.ReverseEndianness(SwarPack4(u0)), 32);
                    var tailBits = (length - 4) * 8;
                    if (tailBits > 0)
                    {
                        // Append masks to tailBits, dropping the overlap bytes
                        Append(ref hi, ref lo, ref pos, (int)BinaryPrimitives.ReverseEndianness(SwarPack4(u1)), tailBits);
                    }
                    break;
                }
#endif
                    // Latin-1 validity, branch-free: if every char is <= 0x00FF the
                    // OR of all code units stays <= 0xFF; any char with high bits
                    // pushes it above. The loop only ORs and the compare happens once
                    // at the end, no per-char `if (text[i] > 0xFF)` branch. Trade-off:
                    // no early exit, so a non-Latin-1 char up front still scans the
                    // whole text; Micro QR inputs are <= 15 chars, so avoiding the
                    // data-dependent branch wins over bailing out early.
                    var acc = 0;
                    for (var i = 0; i < text.Length; i++)
                    {
                        acc |= text[i];
                    }
                    if (acc > 0xFF)
                    {
                        return EncodeUtf8Codewords(text, version, capacityBits, codewordCount, modeValue, headerBits, destination);
                    }

                    // Byte mode: the count indicator counts encoded BYTES; on the
                    // Latin-1 path every char narrows to one byte, so count = length.
                    Append(ref hi, ref lo, ref pos, modeValue | text.Length, headerBits);
                    var j = 0;
#if NET8_0_OR_GREATER
                if (Sse2.IsSupported && text.Length >= 8)
                {
                    // Narrow 8 chars -> 8 bytes, appended as one 64-bit batch.
                    // PackUnsignedSaturate narrows 16-bit lanes to 8-bit; passing
                    // the same vector twice duplicates the result into both
                    // halves, and ToScalar() takes the low 64 bits we need.
                    // Saturation cannot corrupt data: the Latin-1 check above
                    // guarantees every lane is <= 0xFF. ReverseEndianness aligns
                    // conventions, the pack result read as a ulong puts the
                    // FIRST char in the LOWEST byte, while Append64 emits the
                    // HIGHEST byte first.
                    for (; j + 8 <= text.Length; j += 8)
                    {
                        var chars = Vector128.LoadUnsafe(in Unsafe.As<char, ushort>(ref Unsafe.AsRef(in text[j])));
                        var packed = Sse2.PackUnsignedSaturate(chars.AsInt16(), chars.AsInt16()).AsUInt64().ToScalar();
                        Append64(ref hi, ref lo, ref pos, BinaryPrimitives.ReverseEndianness(packed));
                    }
                }
#endif
                    for (; j < text.Length; j++)
                    {
                        Append(ref hi, ref lo, ref pos, (byte)text[j], 8);
                    }
                    break;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), $"Encoding mode {mode} is not supported by Micro QR.");
        }

        Debug.Assert(pos <= capacityBits, "capacity must be validated by the caller");

        FinishAndStore(hi, lo, pos, version, capacityBits, codewordCount, destination);
        return codewordCount;
    }

    /// <summary>
    /// Numeric segment: 10/7/4 bits per 3/2/1 digits (ISO/IEC 18004 7.4.3).
    /// </summary>
    /// <remarks>
    /// The spec writes one 10-bit field per 3-digit group.
    /// Here three groups (9 digits) are combined into a single 30-bit append:
    /// <code>
    /// "123456789"  ->  123 / 456 / 789 (10 bits each)
    ///              ->  [group0: 10 bits][group1: 10 bits][group2: 10 bits]
    ///              ->  (123 &lt;&lt; 20) | (456 &lt;&lt; 10) | 789
    /// </code>
    /// Group values come from <see cref="SwarGroup"/>, whose 8-byte load covers 4 chars for a 3-digit group, so every SWAR grain needs one readable char beyond it (hence the i+9 / i+3 guards); tails without that headroom use scalar group math with the '0' bias folded into one constant.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteNumericData(ref ulong hi, ref ulong lo, ref int pos, ReadOnlySpan<char> digits)
    {
        // contract: digits must be '0'-'9' only (validated upstream). SWAR/scalar
        // group math produces a wrong, but memory-safe, stream otherwise.
        Debug.Assert(AllNumeric(digits), "caller must validate the numeric alphabet");

        ref var c = ref MemoryMarshal.GetReference(digits);
        var length = digits.Length;
        var i = 0;
        // 9 digits -> one 30-bit append; the SWAR load at i+6 reads chars
        // i+6..i+9, so a 10th char must exist: i + 9 < length
        for (; i + 9 < length; i += 9)
        {
            var g = (SwarGroup(ref c, i) << 20) | (SwarGroup(ref c, i + 3) << 10) | SwarGroup(ref c, i + 6);
            Append(ref hi, ref lo, ref pos, g, 30);
        }
        // 3 digits per append while a 4th readable char exists
        for (; i + 3 < length; i += 3)
        {
            Append(ref hi, ref lo, ref pos, SwarGroup(ref c, i), 10);
        }
        // scalar tail: 0-3 digits left, no headroom for an 8-byte load
        if (i + 2 < length)
        {
            // chars stay unbiased through the multiply; the per-digit '0' offsets
            // fold into one subtract: 5328 = '0' * (100 + 10 + 1)
            Append(ref hi, ref lo, ref pos, digits[i] * 100 + digits[i + 1] * 10 + digits[i + 2] - 5328, 10);
            i += 3;
        }
        if (i + 1 < length)
        {
            Append(ref hi, ref lo, ref pos, digits[i] * 10 + digits[i + 1] - 528, 7); // folded bias: 528 = '0' * (10 + 1)
        }
        else if (i < length)
        {
            Append(ref hi, ref lo, ref pos, digits[i] - '0', 4);
        }
    }

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

    private const ulong SwarDigitBias = 0x0030_0030_0030_0030UL;
    private const ulong SwarGroupMagic = (100UL << 32) | (10UL << 16) | 1UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SwarGroup(ref char c, int i)
    {
        if (BitConverter.IsLittleEndian)
        {
            var chunk = Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<char, byte>(ref Unsafe.Add(ref c, i)));
            return (int)(((chunk - SwarDigitBias) * SwarGroupMagic) >> 32) & 0x3FF;
        }

        // Big-endian runtimes (e.g. s390x): the little-endian lane layout above
        // does not hold, and a whole-ulong byte reversal (the NormalizeEndianness
        // approach used for byte-lane SWAR elsewhere) would also swap the bytes
        // INSIDE each 16-bit char lane, so fall back to scalar group math.
        // IsLittleEndian is a JIT-time constant; this branch vanishes from
        // little-endian codegen.
        return Unsafe.Add(ref c, i) * 100 + Unsafe.Add(ref c, i + 1) * 10 + Unsafe.Add(ref c, i + 2) - 5328; // folded bias: 5328 = '0' * 111
    }

#if NET8_0_OR_GREATER
    /// <summary>
    /// Compacts four little-endian 16-bit char lanes (high bytes zero, verified by the caller's validity mask) to their four low bytes: <c>chunk | chunk &gt;&gt; 8</c> yields bytes <c>[c0 c1 c1 c2 c2 c3 c3 0]</c> (LSB first); bytes 0-1 and 4-5 are the contiguous pairs.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint SwarPack4(ulong chunk)
    {
        var r = chunk | (chunk >> 8);
        return (uint)((r & 0xFFFF) | ((r >> 16) & 0xFFFF0000));
    }
#endif

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

    /// <summary>
    /// Alphanumeric segment: 11 bits per pair (value0 * 45 + value1), 6 bits for a trailing odd character (ISO/IEC 18004 7.4.4).
    /// </summary>
    /// <remarks>
    /// Two pairs (4 chars) are combined into a single 22-bit append:
    /// <code>
    /// [pair0: 11 bits][pair1: 11 bits]  ->  (p0 &lt;&lt; 11) | p1
    /// </code>
    /// Like the numeric 9-digit batch, the point is fewer Append calls, each one is a variable-shift OR into the 128-bit accumulator, so halving the call count halves that work.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteAlphanumericData(ref ulong hi, ref ulong lo, ref int pos, ReadOnlySpan<char> chars)
    {
        // contract: the & 0x7F mask below guarantees MEMORY safety for any input,
        // but it does not sanitize: out-of-alphabet chars silently encode wrong
        // values. Alphabet validation is the caller's job (internal-only method).
        Debug.Assert(AllAlphanumeric(chars), "caller must validate the alphanumeric alphabet");

        var i = 0;
        // 4 chars = 2 pairs -> one 22-bit append
        for (; i + 3 < chars.Length; i += 4)
        {
            var p0 = AlnumValues[chars[i] & 0x7F] * 45 + AlnumValues[chars[i + 1] & 0x7F];
            var p1 = AlnumValues[chars[i + 2] & 0x7F] * 45 + AlnumValues[chars[i + 3] & 0x7F];
            Append(ref hi, ref lo, ref pos, (p0 << 11) | p1, 22);
        }
        // remaining full pair, then a trailing odd character
        if (i + 1 < chars.Length)
        {
            var v = AlnumValues[chars[i] & 0x7F] * 45 + AlnumValues[chars[i + 1] & 0x7F];
            Append(ref hi, ref lo, ref pos, v, 11);
            i += 2;
        }
        if (i < chars.Length)
        {
            Append(ref hi, ref lo, ref pos, AlnumValues[chars[i] & 0x7F], 6);
        }
    }

    /// <summary>
    /// Byte segment for non-Latin-1 text: full encode on a private accumulator.
    /// The count indicator counts encoded BYTES.
    /// Not inlined by design, see the class remarks on address exposure.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int EncodeUtf8Codewords(ReadOnlySpan<char> text, MicroQRVersion version, int capacityBits, int codewordCount, int modeValue, int headerBits, Span<byte> destination)
    {
        ulong hi = 0, lo = 0;
        var pos = 0;

        // Contract: the caller has validated the ENCODED length against the
        // capacity, which tops out at 15 bytes (M4-L). Every char yields at
        // least one byte, so text.Length <= 15 follows, and the worst-case
        // expansion (3 bytes per char: non-ASCII BMP or lone surrogates) stays
        // within 45 <= 64 buffer bytes. If the contract is ever violated the
        // Span bounds check in EncodeUtf8 throws, the stack buffer cannot be
        // overrun.
        Debug.Assert(text.Length <= 15, "byte payloads beyond any Micro QR capacity must be rejected by the caller");
        Span<byte> utf8 = stackalloc byte[64];
        var n = EncodeUtf8(text, utf8);

        Append(ref hi, ref lo, ref pos, modeValue | n, headerBits);
        var i = 0;
        for (; i + 8 <= n; i += 8)
        {
            Append64(ref hi, ref lo, ref pos, BinaryPrimitives.ReadUInt64BigEndian(utf8.Slice(i)));
        }
        for (; i < n; i++)
        {
            Append(ref hi, ref lo, ref pos, utf8[i], 8);
        }

        FinishAndStore(hi, lo, pos, version, capacityBits, codewordCount, destination);
        return codewordCount;
    }

    /// <summary>
    /// Hand-rolled UTF-8 encoder matching Encoding.UTF8.GetBytes semantics, including U+FFFD replacement for lone surrogates.
    /// Payloads are tiny (≤ 15 encoded bytes), where Encoding's fixed dispatch cost dominates.
    /// This also replaces the old netstandard2.0 path, <c>Encoding.UTF8.GetBytes(text.ToString())</c> — which allocated both a string and a byte array per call; this loop allocates nothing.
    /// </summary>
    private static int EncodeUtf8(ReadOnlySpan<char> text, Span<byte> utf8)
    {
        var n = 0;
        for (var i = 0; i < text.Length; i++)
        {
            int c = text[i];
            if (c < 0x80)
            {
                // ASCII -> 1 byte
                utf8[n++] = (byte)c;
            }
            else if (c < 0x800)
            {
                // U+0080..U+07FF -> 2 bytes
                utf8[n++] = (byte)(0xC0 | (c >> 6));
                utf8[n++] = (byte)(0x80 | (c & 0x3F));
            }
            else if (c is >= 0xD800 and <= 0xDFFF)
            {
                // surrogate range: a valid high+low pair encodes a supplementary
                // code point (U+10000..U+10FFFF) as 4 bytes
                if (c <= 0xDBFF && i + 1 < text.Length && text[i + 1] is >= (char)0xDC00 and <= (char)0xDFFF)
                {
                    var cp = 0x10000 + ((c - 0xD800) << 10) + (text[i + 1] - 0xDC00);
                    i++;
                    utf8[n++] = (byte)(0xF0 | (cp >> 18));
                    utf8[n++] = (byte)(0x80 | ((cp >> 12) & 0x3F));
                    utf8[n++] = (byte)(0x80 | ((cp >> 6) & 0x3F));
                    utf8[n++] = (byte)(0x80 | (cp & 0x3F));
                }
                else
                {
                    // lone surrogate -> U+FFFD, matching Encoding.UTF8 replacement
                    utf8[n++] = 0xEF;
                    utf8[n++] = 0xBF;
                    utf8[n++] = 0xBD;
                }
            }
            else
            {
                // remaining BMP chars (U+0800..U+FFFF outside surrogates) -> 3 bytes
                utf8[n++] = (byte)(0xE0 | (c >> 12));
                utf8[n++] = (byte)(0x80 | ((c >> 6) & 0x3F));
                utf8[n++] = (byte)(0x80 | (c & 0x3F));
            }
        }
        return n;
    }

    // ---------------------------------------------------------------
    // 128-bit register accumulator (MSB-first: hi = output bytes 0-7,
    // lo = bytes 8-15).
    // ---------------------------------------------------------------

    /// <summary>
    /// Appends the low <paramref name="bitCount"/> bits of <paramref name="value"/> (1-32 bits) at the current position.
    /// Internal (not private) so boundary tests can drive it directly.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Append(ref ulong hi, ref ulong lo, ref int pos, int value, int bitCount)
    {
        Debug.Assert(bitCount >= 1 && bitCount <= 32, "bitCount must be between 1 and 32");
        Debug.Assert(pos + bitCount <= 128, "the stream never exceeds 16 codeword bytes");

        var v = (ulong)(uint)value & ((1UL << bitCount) - 1);
        var end = pos + bitCount;
        if (end <= 64)
        {
            hi |= v << (64 - end);
        }
        else if (pos >= 64)
        {
            lo |= v << (128 - end);
        }
        else
        {
            hi |= v >> (end - 64);
            lo |= v << (128 - end);
        }
        pos = end;
    }

    /// <summary>Appends 64 bits (MSB-first). Valid only while pos ≤ 64.
    /// Internal (not private) so boundary tests can drive it directly.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Append64(ref ulong hi, ref ulong lo, ref int pos, ulong value)
    {
        Debug.Assert(pos <= 64, "a 64-bit append must fit the remaining stream");

        if (pos < 64)
        {
            hi |= value >> pos;
            // two-step shift avoids the &63 wrap at pos == 0
            lo |= (value << 1) << (63 - pos);
        }
        else
        {
            lo |= value;
        }
        pos += 64;
    }

    /// <summary>
    /// Appends the low <paramref name="bitCount"/> bits (1-56) of an already-masked ulong, Append generalized past 32 bits for the NEON byte tail (up to 7 bytes in one call).
    /// Unlike Append this does not mask internally: the caller needs the mask anyway to strip the overlap bytes of its tail load, so re-masking here would be a redundant AND, the pre-masked contract is asserted instead.
    /// Internal (not private) so boundary tests can drive it directly.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AppendWide(ref ulong hi, ref ulong lo, ref int pos, ulong value, int bitCount)
    {
        Debug.Assert(bitCount >= 1 && bitCount <= 56, "bitCount must be between 1 and 56");
        Debug.Assert(pos + bitCount <= 128, "the stream never exceeds 16 codeword bytes");
        Debug.Assert(value >> bitCount == 0, "value must be pre-masked to bitCount bits");

        var end = pos + bitCount;
        if (end <= 64)
        {
            hi |= value << (64 - end);
        }
        else if (pos >= 64)
        {
            lo |= value << (128 - end);
        }
        else
        {
            hi |= value >> (end - 64);
            lo |= value << (128 - end);
        }
        pos = end;
    }

    // Prefix masks over whole bytes: entry n = first n bytes of the 16-byte
    // stream set to FF (hi covers bytes 0-7, lo covers bytes 8-15):
    //
    //   n = 0:   hi = 00 00 00 00 00 00 00 00   lo = 00 ...
    //   n = 3:   hi = FF FF FF 00 00 00 00 00   lo = 00 ...
    //   n = 8:   hi = FF FF FF FF FF FF FF FF   lo = 00 ...
    //   n = 10:  hi = FF FF FF FF FF FF FF FF   lo = FF FF 00 00 00 00 00 00
    //
    // Two prefixes combine into any byte range:
    //   prefixMask[end] & ~prefixMask[start]  ==  [0, end) - [0, start)  ==  [start, end)
    private static readonly ulong[] prefixMaskHi = BuildPrefixMasks(highWord: true);
    private static readonly ulong[] prefixMaskLo = BuildPrefixMasks(highWord: false);

    private static ulong[] BuildPrefixMasks(bool highWord)
    {
        var table = new ulong[17];
        for (var n = 0; n <= 16; n++)
        {
            var bits = n * 8;
            table[n] = highWord
                ? bits == 0 ? 0UL : bits >= 64 ? ulong.MaxValue : ulong.MaxValue << (64 - bits)
                : bits <= 64 ? 0UL : bits >= 128 ? ulong.MaxValue : ulong.MaxValue << (128 - bits);
        }
        return table;
    }

    /// <summary>
    /// Terminator + byte alignment (all zero bits: position arithmetic only), alternating 0xEC/0x11 pad codewords via a phase-selected constant, then the store.
    /// The M1/M3 final 4-bit pad codeword and any trailing zero fill are already zeros in the accumulator.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FinishAndStore(ulong hi, ulong lo, int pos, MicroQRVersion version, int capacityBits, int codewordCount, Span<byte> destination)
    {
        // 3. Terminator (all-zero, shortened when the data reaches capacity) and
        //    zero-fill to the byte boundary: the accumulator already holds zeros
        //    there, so only the position advances.
        pos += Math.Min(MicroQRConstants.GetTerminatorLength(version), capacityBits - pos);

        // 4. Alternating full pad codewords (0xEC, 0x11, ...) up to the last
        //    full data codeword. The old design looped one codeword at a time;
        //    here the whole run lands in at most two 64-bit ORs:
        //    - phase: the pattern constant is picked by the pad start's byte
        //      parity so that byte i gets 0xEC when (i - padStartByte) is even —
        //      even start: EC 11 EC 11 ...,  odd start: 11 EC 11 EC ...
        //    - range: prefixMask[end] & ~prefixMask[start] masks the pattern to
        //      exactly [padStartByte, padEndByte) (see the table comment).
        var padStartByte = (pos + 7) >> 3;
        var padEndByte = capacityBits >> 3;
        if (padStartByte < padEndByte)
        {
            var pattern = (padStartByte & 1) == 0 ? 0xEC11EC11EC11EC11UL : 0x11EC11EC11EC11ECUL;
            hi |= pattern & (prefixMaskHi[padEndByte] & ~prefixMaskHi[padStartByte]);
            lo |= pattern & (prefixMaskLo[padEndByte] & ~prefixMaskLo[padStartByte]);
        }

        // M1/M3 tails need no writes at all: the old design zero-filled to
        // codewordCount * 8 with explicit Write(0, 8) calls, but the accumulator
        // starts at zero and the pad pattern is masked to end at the last FULL
        // codeword (padEndByte), so the final 4-bit pad codeword and the
        // forced-zero low nibble stay zero by construction, the half-codeword
        // handling is expressed by NOT writing, not by a special case.

        // 5. Store big-endian: hi = codeword bytes 0-7, lo = bytes 8-15.
        Debug.Assert(destination.Length >= codewordCount, "destination must hold at least the data codewords");
        if (destination.Length >= 16)
        {
            BinaryPrimitives.WriteUInt64BigEndian(destination, hi);
            BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(8), lo);
        }
        else
        {
            Span<byte> tmp = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64BigEndian(tmp, hi);
            BinaryPrimitives.WriteUInt64BigEndian(tmp.Slice(8), lo);
            tmp.Slice(0, codewordCount).CopyTo(destination);
        }
    }
}
