using System.Buffers;
using System.Runtime.CompilerServices;
using FeatherQR.Internals;
using FeatherQR.Internals.BinaryEncoders;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR;

/// <summary>
/// QR code generator based on ISO/IEC 18004 standard.
/// Supports QR code versions 1-40 with multiple encoding modes and error correction levels.
/// </summary>
/// <remarks>
/// Encoding modes written here are Numeric, Alphanumeric and Byte (ISO-8859-1 / UTF-8,
/// with ECI). Kanji mode is never written, Japanese text goes out as UTF-8 in Byte
/// mode; <see cref="QRCodeDecoder"/> does read Kanji segments other encoders produce,
/// so the two directions are deliberately asymmetric.
/// </remarks>
public static class QRCodeGenerator
{
    // -----------------------------------------------------
    // QR Code Data Structure
    // -----------------------------------------------------
    //
    // 1. Header
    // ┌─────────────────┬───────────────┬────────────────┐
    // │ ECI (0 or 12b)  │ Mode (4b)     │ Count (8-16b)  │
    // └─────────────────┴───────────────┴────────────────┘
    // 2. Data
    // ┌──────────────────────────────────────────────────┐
    // │ Encoded data (variable length)                   │
    // └──────────────────────────────────────────────────┘
    // 3. Padding
    // ┌──────┬────────┬──────────────────────────────────┐
    // │ Term │ Align  │ Pad bytes (0xEC, 0x11...)        │
    // │ (4b) │ (0-7b) │ (until dataCapacityBits reached) │
    // └──────┴────────┴──────────────────────────────────┘

    private const int ModeIndicatorBits = 4;

    /// <summary>The `requestedVersion` value meaning "pick the smallest version that fits".</summary>
    private const int AutomaticVersion = -1;

    /// <summary>The internal mask-pattern value meaning "select the lowest-penalty pattern".</summary>
    private const int AutomaticMask = -1;

    private static QRCodeData CreateCore(ReadOnlySpan<char> textSpan, QREccLevel eccLevel, bool utf8BOM, EciMode eciMode, int requestedVersion, int quietZoneSize, int maskPattern)
    {
        // QR code generation process:
        // ------------------------------------------------
        // 1. Validate input parameters (version range, quiet zone size)
        // 2. Prepare configuration:
        //    - Analyze text to determine optimal encoding mode (Numeric/Alphanumeric/Byte)
        //    - Select QR code version based on data length and ECC level
        //    - Get error correction info for the selected version
        // 3. Calculate buffer sizes (data capacity, ECC capacity, interleaved size)
        // 4. Encode data:
        //    - Write mode indicator (4 bits) and character count indicator
        //    - Write actual data content
        //    - Add padding to fill code word capacity (terminator + alignment + 0xEC/0x11 pattern)
        // 5. Calculate error correction codewords using Reed-Solomon (per block)
        // 6. Interleave data and ECC codewords (according to QR code specification)
        // 7. Write QR matrix:
        //    - Place fixed patterns (finder, separators, alignment, timing, dark module)
        //    - Reserve areas for format and version information
        //    - Take the version's cached function-pattern template and blocked-module bitmask
        //    - Place data modules in zigzag pattern
        //    - Apply optimal mask pattern (test all 8 patterns, select best)
        //    - Place format information (ECC level + mask pattern)
        //    - Place version information (version 7+ only)
        // 8. Return QRCodeData (quiet zone handled by QRCodeData class)

        if (requestedVersion != -1 && (requestedVersion < 1 || requestedVersion > 40))
            throw new ArgumentOutOfRangeException(nameof(requestedVersion), $"Version must be 1-40 or -1(auto), but was {requestedVersion}");
        if (quietZoneSize < 0)
            throw new ArgumentOutOfRangeException(nameof(quietZoneSize), $"Quiet zone size must be non-negative, got {quietZoneSize}");

        // Prepare configuration
        var config = PrepareConfiguration(textSpan, eccLevel, utf8BOM, eciMode, requestedVersion);

        var result = new QRCodeData(config.Version, quietZoneSize);
        var coreSize = result.GetCoreSize();
        var dataLength = coreSize * coreSize;

        // Allocate buffers
        byte[]? rentedWorkBuffer = null;

        try
        {
            // Work buffer (without quiet zone)
            rentedWorkBuffer = ArrayPool<byte>.Shared.Rent(dataLength);
            // No clear: the placement template covers every core module.
            var workBuffer = rentedWorkBuffer.AsSpan(0, dataLength);

            WriteCoreModules(textSpan, config, workBuffer, coreSize, maskPattern);

            result.SetCoreData(workBuffer);

            return result;
        }
        finally
        {
            if (rentedWorkBuffer is not null)
                ArrayPool<byte>.Shared.Return(rentedWorkBuffer, clearArray: false);
        }
    }

    private static int CreateCore(ReadOnlySpan<char> textSpan, QREccLevel eccLevel, Span<byte> destination, bool utf8BOM, EciMode eciMode, int requestedVersion, int quietZoneSize, int maskPattern)
    {
        if (requestedVersion != -1 && (requestedVersion < 1 || requestedVersion > 40))
            throw new ArgumentOutOfRangeException(nameof(requestedVersion), $"Version must be 1-40 or -1(auto), but was {requestedVersion}");
        if (quietZoneSize < 0)
            throw new ArgumentOutOfRangeException(nameof(quietZoneSize), $"Quiet zone size must be non-negative, got {quietZoneSize}");

        // Prepare configuration
        var config = PrepareConfiguration(textSpan, eccLevel, utf8BOM, eciMode, requestedVersion);

        var coreSize = QRCodeData.SizeFromVersion(config.Version);
        var (totalSize, requiredSize) = CalculateMatrixSize(coreSize, quietZoneSize);
        if (destination.Length < requiredSize)
            throw new ArgumentException($"Destination buffer too small: {requiredSize} bytes required (version {config.Version}, {totalSize}x{totalSize} modules), got {destination.Length} bytes. Use {nameof(TryGetRequiredBufferSize)} to calculate the required size.", nameof(destination));

        // The placement template covers every core module, so only the quiet zone
        // needs zeroed memory.
        var target = destination.Slice(0, requiredSize);

        if (quietZoneSize == 0)
        {
            WriteCoreModules(textSpan, config, target, coreSize, maskPattern);
        }
        else
        {
            target.Clear();
            // The placement pipeline requires a contiguous coreSize-stride matrix,
            // so build the core in a rented buffer and center it in the destination.
            byte[]? rentedWorkBuffer = null;
            try
            {
                var dataLength = coreSize * coreSize;
                rentedWorkBuffer = ArrayPool<byte>.Shared.Rent(dataLength);
                var workBuffer = rentedWorkBuffer.AsSpan(0, dataLength);

                WriteCoreModules(textSpan, config, workBuffer, coreSize, maskPattern);

                for (var row = 0; row < coreSize; row++)
                {
                    var destOffset = (row + quietZoneSize) * totalSize + quietZoneSize;
                    workBuffer.Slice(row * coreSize, coreSize).CopyTo(target.Slice(destOffset, coreSize));
                }
            }
            finally
            {
                if (rentedWorkBuffer is not null)
                    ArrayPool<byte>.Shared.Return(rentedWorkBuffer, clearArray: false);
            }
        }

        return requiredSize;
    }

    /// <summary>
    /// Runs the encode → ECC → interleave → module placement pipeline and writes
    /// the core module matrix (one byte per module, no quiet zone) into <paramref name="coreBuffer"/>.
    /// </summary>
    /// <param name="textSpan">The text span to encode.</param>
    /// <param name="config">Prepared QR configuration.</param>
    /// <param name="coreBuffer">Output buffer of coreSize × coreSize bytes (every module is written; no zeroing required).</param>
    /// <param name="coreSize">Module count per side without quiet zone.</param>
    /// <param name="maskPattern">Pinned mask pattern (0-7), or <see cref="AutomaticMask"/> for penalty-scored selection.</param>
    private static void WriteCoreModules(ReadOnlySpan<char> textSpan, in QRConfiguration config, Span<byte> coreBuffer, int coreSize, int maskPattern)
    {
        // Calculate buffer sizes
        var dataCapacity = CalculateMaxBitStringLength(config.Version, config.EccLevel, config.Encoding);
        var dataBufferSize = (dataCapacity + 7) / 8; // bits to bytes, rounded up
        var totalBlocks = config.EccInfo.BlocksInGroup1 + config.EccInfo.BlocksInGroup2;
        var eccBufferSize = totalBlocks * config.EccInfo.ECCPerBlock;
        var interleavedSize = BinaryInterleaver.CalculateInterleavedSize(config.EccInfo, QRCodeConstants.GetRemainderBits(config.Version));

        Span<byte> dataBuffer = stackalloc byte[dataBufferSize];
        Span<byte> eccBuffer = stackalloc byte[eccBufferSize];
        Span<byte> interleavedBuffer = stackalloc byte[interleavedSize];

        // Encode data
        var encodedLength = EncodeData(textSpan, config, dataBuffer);
        var encodedData = dataBuffer.Slice(0, encodedLength);

        // Calculate Error Correction
        CalculateErrorCorrection(encodedData, config.EccInfo, eccBuffer);

        // Interleave data
        InterleaveCodewords(encodedData, eccBuffer, config.EccInfo, config.Version, interleavedBuffer);

        // QR matrix in core buffer
        WriteQRMatrix(coreBuffer, coreSize, config.Version, interleavedBuffer, config.EccLevel, maskPattern);
    }

    // ---- the Create surface -----------------------------------------------------------
    //
    // Two overloads, one shape. The 1.1.1 parameter lists (utf8BOM, eciMode,
    // requestedVersion, quietZoneSize) were frozen through 1.2.0 and removed in 2.0.0,
    // which is what lets `options` carry a default here: while both sets existed,
    // Create(text, ecc) would have been ambiguous between them. The string convenience
    // overloads went with them: `string` converts implicitly to ReadOnlySpan<char> on every
    // target framework, so Create("text", ecc) still binds here.
    //
    // Sizing is deliberately not paired: only TryGetRequiredBufferSize is offered, because
    // "does not fit" is a data-dependent answer rather than a defect.

    /// <summary>
    /// Creates a QR code from the provided plain text.
    /// </summary>
    /// <param name="textSpan">The text to encode in the QR code. A <see cref="string"/> converts implicitly.</param>
    /// <param name="eccLevel">Error correction level (L: 7%, M: 15%, Q: 25%, H: 30%).</param>
    /// <param name="options">Encoding, version, quiet zone and segmentation settings. Omit it, or pass <see cref="QRCodeGeneratorOptions.Default"/>, for the defaults.</param>
    /// <returns>QRCodeData containing the generated QR code matrix.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <see cref="QRCodeGeneratorOptions.Segmentation"/> is not a defined value.</exception>
    public static QRCodeData Create(ReadOnlySpan<char> textSpan, QREccLevel eccLevel, in QRCodeGeneratorOptions options = default)
    {
        // One compare on the default path; validation of the value itself lives in the
        // cold method so Single costs a predicted not-taken branch and nothing else.
        if (options.Segmentation != QRSegmentation.Single)
            return CreateOptimal(textSpan, eccLevel, in options);

        var (version, resolvedEcc) = ResolveVersionAndEcc(textSpan, eccLevel, options);
        return CreateCore(textSpan, resolvedEcc, options.Utf8Bom, options.EciMode, version, options.QuietZoneSize, options.MaskPattern ?? AutomaticMask);
    }

    /// <summary>
    /// Creates a QR code from the provided plain text and writes the module matrix into the caller-provided buffer without per-call heap allocation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Output format: one byte per module (0 = light, 1 = dark), flat row-major order, quiet zone included.
    /// Module at (row, col) is <c>destination[row * qrSize + col]</c> where qrSize is
    /// <see cref="QRCodeCalculatedSize.Size"/> returned by <see cref="TryGetRequiredBufferSize"/>.
    /// </para>
    /// <para>
    /// Usage flow for allocation-free generation:
    /// <code>
    /// if (!QRCodeGenerator.TryGetRequiredBufferSize(text, QREccLevel.M, out var calculated))
    ///     return; // content does not fit version 40 at this ECC level
    /// var buffer = ArrayPool&lt;byte&gt;.Shared.Rent(calculated.BufferSize);
    /// var written = QRCodeGenerator.Create(text, QREccLevel.M, buffer);
    /// var matrix = buffer.AsSpan(0, written);
    /// // ... consume matrix ...
    /// ArrayPool&lt;byte&gt;.Shared.Return(buffer);
    /// </code>
    /// </para>
    /// <para>
    /// Only the first <see cref="QRCodeCalculatedSize.BufferSize"/> bytes of <paramref name="destination"/> are written
    /// (every byte of that region is written, so a dirty pooled buffer is fine); any remaining bytes are left untouched.
    /// </para>
    /// </remarks>
    /// <param name="textSpan">The text to encode in the QR code. A <see cref="string"/> converts implicitly.</param>
    /// <param name="eccLevel">Error correction level (L: 7%, M: 15%, Q: 25%, H: 30%).</param>
    /// <param name="destination">The buffer to write the QR code module matrix into. Must be at least <see cref="QRCodeCalculatedSize.BufferSize"/> bytes, as reported by <see cref="TryGetRequiredBufferSize"/>.</param>
    /// <param name="options">Encoding, version, quiet zone and segmentation settings. Size <paramref name="destination"/> with the same options.</param>
    /// <returns>The number of bytes written to <paramref name="destination"/> (always qrSize × qrSize).</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="destination"/> is smaller than the required buffer size.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <see cref="QRCodeGeneratorOptions.Segmentation"/> is not a defined value.</exception>
    public static int Create(ReadOnlySpan<char> textSpan, QREccLevel eccLevel, Span<byte> destination, in QRCodeGeneratorOptions options = default)
    {
        if (options.Segmentation != QRSegmentation.Single)
            return CreateOptimalTo(textSpan, eccLevel, destination, in options);

        var (version, resolvedEcc) = ResolveVersionAndEcc(textSpan, eccLevel, options);
        return CreateCore(textSpan, resolvedEcc, destination, options.Utf8Bom, options.EciMode, version, options.QuietZoneSize, options.MaskPattern ?? AutomaticMask);
    }

    /// <summary>
    /// Calculates the required buffer size, matrix size and version for encoding the
    /// specified text as a QR code, reporting content that does not fit as <c>false</c>
    /// rather than as an exception.
    /// </summary>
    /// <param name="text">The text to encode in the QR code.</param>
    /// <param name="eccLevel">Error correction level.</param>
    /// <param name="size">Buffer size, matrix size and version on success; <c>default</c> when the content does not fit.</param>
    /// <param name="options">Encoding, version, quiet zone and segmentation settings.</param>
    /// <returns><c>true</c> when the content fits.</returns>
    /// <remarks>
    /// <para>
    /// <c>false</c> means the content does not fit, and nothing else: argument errors
    /// throw (rationale: specs/rmqr-encoder.md). When
    /// <see cref="QRCodeGeneratorOptions.Version"/> is narrower than
    /// <see cref="QRVersionRange.Any"/>, that means no version <em>in that range</em>
    /// holds the content, not merely that it exceeds version 40.
    /// <see cref="QRCodeGeneratorOptions.BoostEccLevel"/> has no effect here: the boost
    /// never changes the version, and the buffer size depends only on the version.
    /// </para>
    /// <para>
    /// Pass the same <paramref name="options"/> you will encode with:
    /// <see cref="QRCodeGeneratorOptions.Segmentation"/> and
    /// <see cref="QRCodeGeneratorOptions.EciMode"/> can select different versions, so a
    /// buffer sized for one can be too small for the other.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <see cref="QRCodeGeneratorOptions.QuietZoneSize"/> is negative, or large enough that the resulting matrix would exceed <see cref="int.MaxValue"/> bytes, or when <see cref="QRCodeGeneratorOptions.Segmentation"/> is not a defined value.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="eccLevel"/> is not a defined value. Content that does not fit is <em>not</em> an exception here; it is <c>false</c>.</exception>
    public static bool TryGetRequiredBufferSize(ReadOnlySpan<char> text, QREccLevel eccLevel, out QRCodeCalculatedSize size, in QRCodeGeneratorOptions options = default)
    {
        size = default;
        ValidateQuietZoneSize(options.QuietZoneSize);
        if (options.Segmentation != QRSegmentation.Single)
            ValidateOptimalEntry(options.Segmentation);

        var analysisResult = TextAnalyzer.Analyze(text, options.EciMode);
        int version;
        if (options.Segmentation != QRSegmentation.Single && !(options.Utf8Bom && analysisResult.EciMode == EciMode.Utf8 && analysisResult.EncodingMode == EncodingMode.Byte))
        {
            // Mirrors the encode path (SelectOptimalVersion + BuildPlanOrFallback),
            // single-mode fallback included, so the version reported here is the
            // version an encode with the same options would use.
            if (!TryPlanOptimalVersion(text, eccLevel, in analysisResult, in options, out version))
                return false;
        }
        else if (!TryGetVersionInRange(analysisResult.DataLength, analysisResult.EncodingMode, eccLevel, analysisResult.EciMode, options.Utf8Bom, options.Version.Min, options.Version.Max, out version))
        {
            return false;
        }

        var (totalSize, bufferSize) = CalculateMatrixSize(QRCodeData.SizeFromVersion(version), options.QuietZoneSize);
        size = new QRCodeCalculatedSize(bufferSize, totalSize, version);
        return true;
    }

    private static string DoesNotFitMessage(QRVersionRange range, QREccLevel eccLevel, in TextAnalysisResult analysis)
        => $"Content does not fit {(range.IsExact ? $"a version {range.Min}" : $"any version in {range}")} QR code at ECC level {eccLevel} " +
           $"(mode: {analysis.EncodingMode}, ECI: {analysis.EciMode}, {analysis.DataLength} data units). " +
           $"Widen the version range, lower the ECC level, or leave it at QRVersionRange.Any for automatic selection.";

    /// <summary>
    /// The smallest version in the range that holds the content (or the automatic marker
    /// when nothing forces a resolution here, so the default path is unchanged), and the
    /// error correction level after an optional boost. A constrained range or a boost
    /// costs one extra text analysis, since the overload this feeds analyses again.
    /// </summary>
    /// <remarks>
    /// The boost never changes the version: the version is chosen for the requested
    /// (minimum) level first, then the level is raised while the next one still fits
    /// that version. Content that fits no version keeps the exact exception of the
    /// boost-free path, unconstrained overflow included, so turning boost on cannot
    /// reclassify an error.
    /// </remarks>
    private static (int Version, QREccLevel EccLevel) ResolveVersionAndEcc(ReadOnlySpan<char> textSpan, QREccLevel eccLevel, in QRCodeGeneratorOptions options)
    {
        if (options.Version.IsAny && !options.BoostEccLevel)
            return (AutomaticVersion, eccLevel);   // the overload this feeds validates the quiet zone itself

        ValidateQuietZoneSize(options.QuietZoneSize);

        var analysisResult = TextAnalyzer.Analyze(textSpan, options.EciMode);
        if (!TryGetVersionInRange(analysisResult.DataLength, analysisResult.EncodingMode, eccLevel, analysisResult.EciMode, options.Utf8Bom, options.Version.Min, options.Version.Max, out var version))
        {
            if (options.Version.IsAny)
            {
                // Unconstrained overflow is InvalidOperationException on every released
                // path; GetVersion recomputes only to throw that exact exception.
                GetVersion(analysisResult.DataLength, analysisResult.EncodingMode, eccLevel, analysisResult.EciMode, options.Utf8Bom);
            }
            throw new ArgumentException(DoesNotFitMessage(options.Version, eccLevel, analysisResult), nameof(options));
        }

        while (options.BoostEccLevel && eccLevel < QREccLevel.H
            && FitsVersion(analysisResult.DataLength, analysisResult.EncodingMode, eccLevel + 1, analysisResult.EciMode, options.Utf8Bom, version))
        {
            eccLevel += 1;
        }

        return (version, eccLevel);
    }

    private static void ValidateQuietZoneSize(int quietZoneSize)
    {
        if (quietZoneSize < 0)
            throw new ArgumentOutOfRangeException(nameof(quietZoneSize), $"Quiet zone size must be non-negative, got {quietZoneSize}");
    }

    /// <summary>
    /// Computes the matrix side length (core + quiet zone) and the byte-per-module
    /// buffer size, guarding against <see cref="int"/> overflow from oversized
    /// quiet zones.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the buffer size would exceed <see cref="int.MaxValue"/>.</exception>
    private static (int TotalSize, int BufferSize) CalculateMatrixSize(int coreSize, int quietZoneSize)
    {
        // long arithmetic: quietZoneSize is caller-controlled, and totalSize² can
        // exceed int.MaxValue long before totalSize itself does. The first check
        // also keeps the squaring below long.MaxValue.
        var totalSize = coreSize + 2L * quietZoneSize;
        if (totalSize > int.MaxValue || totalSize * totalSize > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(quietZoneSize), $"Quiet zone size {quietZoneSize} makes the matrix ({totalSize}x{totalSize} modules) exceed the maximum supported buffer size ({int.MaxValue} bytes).");

        return ((int)totalSize, (int)(totalSize * totalSize));
    }

    // Pipelines

    /// <summary>
    /// Prepares QR configuration by determining encoding, ECI mode, and version.
    /// </summary>
    /// <param name="textSpan">The text to encode.</param>
    /// <param name="eccLevel">How much damage the symbol can survive.</param>
    /// <param name="utf8BOM">Whether a UTF-8 byte order mark precedes the content. It costs three bytes of capacity, and only in Byte mode with UTF-8 ECI.</param>
    /// <param name="eciMode">The character encoding to declare, or <see cref="EciMode.Default"/> to choose one from the text.</param>
    /// <param name="requestedVersion">The symbol size to use (1 to 40), or -1 to pick the smallest size the text fits in.</param>
    /// <returns>The version, encoding mode, ECI mode and error correction layout to encode with.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static QRConfiguration PrepareConfiguration(ReadOnlySpan<char> textSpan, QREccLevel eccLevel, bool utf8BOM, EciMode eciMode, int requestedVersion)
    {
        var analysisResult = TextAnalyzer.Analyze(textSpan, eciMode);

        // Select QR code version (auto or manual)
        var version = requestedVersion == -1
            ? GetVersion(analysisResult.DataLength, analysisResult.EncodingMode, eccLevel, analysisResult.EciMode, utf8BOM)
            : requestedVersion;

        // Create ECCInfo
        var eccInfo = QRCodeConstants.GetEccInfo(version, eccLevel);

        // UTF-8 BOM bytes ([0xEF, 0xBB, 0xBF]) are written into the Byte-mode data stream,
        // so the character count indicator must include them (ISO/IEC 18004 7.4.5)
        var dataLength = analysisResult.DataLength;
        if (utf8BOM && analysisResult.EncodingMode == EncodingMode.Byte && analysisResult.EciMode == EciMode.Utf8)
        {
            dataLength += 3;
        }

        return new QRConfiguration(version, eccLevel, analysisResult.EncodingMode, analysisResult.EciMode, utf8BOM, eccInfo, dataLength);
    }

    /// <summary>
    /// Encodes the input text into a binary format and writes it to the provided buffer.
    /// </summary>
    /// <param name="textSpan"></param>
    /// <param name="config"></param>
    /// <param name="buffer">Output buffer for encoded data.</param>
    /// <returns>Number of bytes written to the buffer.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int EncodeData(ReadOnlySpan<char> textSpan, in QRConfiguration config, Span<byte> buffer)
    {
        var encoder = new QRBinaryEncoder(buffer);

        encoder.WriteMode(config.Encoding, config.EciMode);
        encoder.WriteCharacterCount(config.DataLength, config.Encoding.GetCountIndicatorLength(config.Version));
        encoder.WriteData(textSpan, config.Encoding, config.EciMode, config.Utf8Bom);
        encoder.WritePadding(config.EccInfo.TotalDataCodewords * 8);

        return encoder.ByteCount;
    }

    /// <summary>
    /// Calculates Reed-Solomon error correction codewords and writes them to the provided ECC buffer.
    /// </summary>
    /// <param name="encodedBytes">The byte representing the encoded QR code data.</param>
    /// <param name="eccInfo">Error correction information for the QR code version and ECC level.</param>
    /// <param name="eccBuffer">Output buffer for ECC codewords <c>(eccInfo.BlocksInGroup1 + eccInfo.BlocksInGroup2) * eccInfo.ECCPerBlock</c> bytes.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CalculateErrorCorrection(ReadOnlySpan<byte> encodedBytes, in ECCInfo eccInfo, Span<byte> eccBuffer)
    {
        var dataOffset = 0;
        var eccOffset = 0;

        // Process group 1 blocks
        for (var i = 0; i < eccInfo.BlocksInGroup1; i++)
        {
            var blockData = encodedBytes.Slice(dataOffset, eccInfo.CodewordsInGroup1);
            var blockEcc = eccBuffer.Slice(eccOffset, eccInfo.ECCPerBlock);

            EccBinaryEncoder.CalculateECC(blockData, blockEcc, eccInfo.ECCPerBlock);

            dataOffset += eccInfo.CodewordsInGroup1;
            eccOffset += eccInfo.ECCPerBlock;
        }

        // Process group 2 blocks
        for (var i = 0; i < eccInfo.BlocksInGroup2; i++)
        {
            var blockData = encodedBytes.Slice(dataOffset, eccInfo.CodewordsInGroup2);
            var blockEcc = eccBuffer.Slice(eccOffset, eccInfo.ECCPerBlock);

            EccBinaryEncoder.CalculateECC(blockData, blockEcc, eccInfo.ECCPerBlock);

            dataOffset += eccInfo.CodewordsInGroup2;
            eccOffset += eccInfo.ECCPerBlock;
        }
    }

    /// <summary>
    /// Interleaves data and error correction codewords according to QR code specification.
    /// </summary>
    /// <param name="dataBuffer">The buffer containing encoded data codewords</param>
    /// <param name="eccBuffer">The buffer containing error correction codewords</param>
    /// <param name="eccInfo">Error correction information for the QR code version and ECC level</param>
    /// <param name="version">The QR code version (1-40)</param>
    /// <param name="output">Output buffer to write interleaved codewords</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InterleaveCodewords(ReadOnlySpan<byte> dataBuffer, ReadOnlySpan<byte> eccBuffer, in ECCInfo eccInfo, int version, Span<byte> output)
    {
        BinaryInterleaver.InterleaveCodewords(dataBuffer, eccBuffer, output, eccInfo);
    }

    /// <summary>
    /// Writes the QR code matrix (as a 1D byte array) into the provided buffer by placing patterns, data, applying mask, and adding format/version information.
    /// </summary>
    /// <param name="buffer">The buffer to write the QR code matrix into.</param>
    /// <param name="size">The size of the QR code matrix (number of modules per side).</param>
    /// <param name="version">The QR code version (1-40) to generate.</param>
    /// <param name="interleavedData">The encoded and interleaved data bytes to be placed in the QR code.</param>
    /// <param name="eccLevel">The error correction level to use for the QR code.</param>
    /// <param name="maskPattern">Pinned mask pattern (0-7), or <see cref="AutomaticMask"/> for penalty-scored selection.</param>
    /// <returns>A <see cref="QRCodeData"/> object containing the generated QR code matrix.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteQRMatrix(Span<byte> buffer, int size, int version, ReadOnlySpan<byte> interleavedData, QREccLevel eccLevel, int maskPattern)
    {
        // Function patterns, the blocked-module bitmask and the zigzag order all come
        // from the version's cached placement tables (ModulePlacer.PlacementLayout):
        // the template copy paints every function module and zeros the rest, the data
        // placement writes only the stream bits, and mask selection reads the cached
        // blocked mask directly (no per-call bitmask build).
        var layout = ModulePlacer.GetLayout(version);
        layout.Template.AsSpan().CopyTo(buffer);

        // Place data
        ModulePlacer.PlaceDataWords(buffer, layout, interleavedData);

        // Apply mask and format
        int maskVersion;
        if (maskPattern != AutomaticMask)
        {
            ModulePlacer.ApplyMaskPattern(buffer, size, layout.BlockedMask, maskPattern);
            maskVersion = maskPattern;
        }
        else
        {
            maskVersion = ModulePlacer.MaskCode(buffer, size, version, layout.BlockedMask, eccLevel);
        }
        var formatBit = QRCodeConstants.GetFormatBits(eccLevel, maskVersion);
        ModulePlacer.PlaceFormat(buffer, size, formatBit);

        // Place version information (version 7+)
        if (version >= 7)
        {
            var versionBits = QRCodeConstants.GetVersionBits(version);
            ModulePlacer.PlaceVersion(buffer, size, versionBits);
        }
    }

    /// <summary>
    /// Places all function patterns (finder, separators, alignment, timing, dark module)
    /// into <paramref name="buffer"/> and builds the blocked-module bitmask covering them
    /// plus the reserved format/version areas.
    /// </summary>
    /// <remarks>
    /// Fast path: copies the version's cached template and bitmask
    /// (<see cref="ModulePlacer.GetLayout"/>), which are built once by
    /// <see cref="PlaceFunctionModulesReference"/>. The same tables serve the encoder
    /// (WriteQRMatrix) and the decoder (QRMatrixDecoder reads the cached bitmask), so
    /// both sides always agree on the exact blocked region layout. The template covers
    /// the whole core, so <paramref name="buffer"/> need not be zeroed; the data
    /// modules are written as 0.
    /// </remarks>
    /// <param name="buffer">Core matrix buffer (size × size bytes) to place patterns into.</param>
    /// <param name="size">Matrix size in modules (no quiet zone).</param>
    /// <param name="version">QR code version (1-40).</param>
    /// <param name="blockedMask">Output bitmask buffer of at least (size*size+7)/8 bytes; overwritten with the version's cached blocked-module mask.</param>

    internal static void PlaceFunctionModules(Span<byte> buffer, int size, int version, Span<byte> blockedMask)
    {
        var layout = ModulePlacer.GetLayout(version);
        if (size != layout.Size)
            throw new ArgumentException($"size {size} does not match version {version} ({layout.Size} modules)", nameof(size));
        layout.Template.AsSpan().CopyTo(buffer);
        layout.BlockedMask.AsSpan().CopyTo(blockedMask);
    }

    /// <summary>
    /// Reference (per-module) function-pattern placement: the source of truth that
    /// builds the cached tables and that the parity tests hold the fast path to.
    /// </summary>
    internal static void PlaceFunctionModulesReference(Span<byte> buffer, int size, int version, Span<byte> blockedMask)
    {
        // Version 1-16: stack allocation (covers 95%+ use cases)
        // Version 17+: heap allocation (large/rare QR codes)
        const int StackAllocThreshold = 16;
        const int StackAllocSize = 40; // Sufficient for version 16 (33 modules needed)

        // Version 1:  approximately  9
        // Version 7:  approximately 27
        // Version 40: approximately 57
        Rectangle[]? rentedBlockedModules = null;

        try
        {
            var blockedModulesSize = CalculateBlockedModulesSize(version);
            Span<Rectangle> blockedModulesBuffer = version > StackAllocThreshold
                ? (rentedBlockedModules = ArrayPool<Rectangle>.Shared.Rent(blockedModulesSize)).AsSpan(0, blockedModulesSize)
                : stackalloc Rectangle[StackAllocSize];
            blockedModulesBuffer.Clear();
            var blockedCount = 0;

            // Place all patterns
            var alignmentPatternLocations = GetAlignmentPatternPositions(version);
            ModulePlacer.PlaceFinderPatterns(buffer, size, blockedModulesBuffer, ref blockedCount);
            ModulePlacer.ReserveSeparatorAreas(size, blockedModulesBuffer, ref blockedCount);
            ModulePlacer.PlaceAlignmentPatterns(buffer, size, alignmentPatternLocations, blockedModulesBuffer, ref blockedCount);
            ModulePlacer.PlaceTimingPatterns(buffer, size, blockedModulesBuffer, ref blockedCount);
            ModulePlacer.PlaceDarkModule(buffer, size, version, blockedModulesBuffer, ref blockedCount);
            ModulePlacer.ReserveVersionAreas(size, version, blockedModulesBuffer, ref blockedCount);

            // Generate BitMask
            blockedMask.Clear();
            BuildBlockedMask(blockedMask, size, blockedModulesBuffer.Slice(0, blockedCount));
        }
        finally
        {
            if (rentedBlockedModules is not null)
                ArrayPool<Rectangle>.Shared.Return(rentedBlockedModules, clearArray: false);
        }
    }

    /// <summary>
    /// Builds a bitmask from blocked module rectangles for O(1) lookup.
    /// Each bit in the mask represents whether a module is blocked (1) or free (0).
    /// </summary>
    /// <param name="mask">Output bitmask buffer</param>
    /// <param name="size">The size of the QR code matrix</param>
    /// <param name="blockedModules">List of rectangular areas</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void BuildBlockedMask(Span<byte> mask, int size, ReadOnlySpan<Rectangle> blockedModules)
    {
        foreach (var rect in blockedModules)
        {
            for (var y = rect.Y; y < rect.Y + rect.Height; y++)
            {
                var rowOffset = y * size;
                for (var x = rect.X; x < rect.X + rect.Width; x++)
                {
                    var bitIndex = rowOffset + x;
                    mask[bitIndex >> 3] |= (byte)(1 << (bitIndex & 7));
                }
            }
        }
    }

    /// <summary>
    /// Retrieves alignment pattern positions for the specified version.
    /// </summary>
    /// <param name="version">The QR code version for which to retrieve alignment pattern positions.</param>
    /// <returns>A list of alignment pattern positions as points for the specified QR code version.</returns>
    /// <exception cref="InvalidOperationException"></exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static List<Point> GetAlignmentPatternPositions(int version)
    {
        var table = QRCodeConstants.AlignmentPatternTable;
        for (var i = 0; i < table.Count; i++)
        {
            var item = table[i];
            if (item.Version == version)
                return item.PatternPositions;
        }

        throw new InvalidOperationException($"Alignment pattern positions not found for version {version}");
    }

    /// <summary>
    /// Calculates the maximum bit string length for the given encoding mode, data, and version.
    /// Used to pre-allocate StringBuilder capacity to avoid resizing.
    /// </summary>
    /// <param name="version">QR code version (1-40).</param>
    /// <param name="eccLevel">Error correction level.</param>
    /// <param name="encoding">Encoding mode (Numeric, Alphanumeric, Byte, Kanji).</param>
    /// <returns>Maximum bit string length in characters.</returns>
    internal static int CalculateMaxBitStringLength(int version, QREccLevel eccLevel, EncodingMode encoding)
    {
        if (version is < 1 or > 40)
            throw new ArgumentOutOfRangeException(nameof(version), $"Version must be 1-40, but was {version}");

        // QR codes are always padded to full capacity with 0xEC/0x11 bytes
        // So the final bit string length = data capacity in bits
        // ECCInfo contains the actual byte capacity (TotalDataCodewords)
        var eccInfo = QRCodeConstants.GetEccInfo(version, eccLevel);
        return eccInfo.TotalDataCodewords * 8; // Convert bytes to bits
    }

    // Utilities

    /// <summary>
    /// Calculates the number of blocked modules (reserved areas) for the given version.
    /// </summary>
    /// <param name="version">The QR code version (1-40)</param>
    /// <returns>The number of <see cref="Rectangle"/> elements needed to store all blocked module areas.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CalculateBlockedModulesSize(int version)
    {
        // Blocked modules are reserved areas in the QR matrix that contain fixed patterns:
        // - Finder patterns (3x)
        // - Separators (3x)
        // - Timing patterns (2x)
        // - Dark module (1x)
        // - Format information areas (varies by version)
        // - Version information areas (version 7+)
        // - Alignment patterns (varies by version, 0 for version 1, increases with version)
        // The calculation ensures sufficient buffer space for all reserved areas.

        const int basePatterns = 12;
        var formatVersionAreas = version >= 7 ? 8 : 6;
        var alignmentPatternCount = CalculateAlignmentPatternCount(version);
        return basePatterns + formatVersionAreas + alignmentPatternCount;
    }

    /// <summary>
    /// Calculates the number of alignment pattern positions for a given QR code version.
    /// </summary>
    /// <param name="version">The QR code version (1-40)</param>
    /// <returns>The total number of alignment patterns required for the specified version.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CalculateAlignmentPatternCount(int version)
    {
        // Alignment patterns help QR code readers correct for distortion:
        // - Version 1: No alignment patterns (0)
        // - Version 2+: Multiple alignment patterns arranged in a grid
        // - Pattern count = (positions × positions) - overlaps with finder patterns
        // - Overlaps: 3 corners where alignment patterns would conflict with finder patterns
        // The result is used to allocate buffer space for blocked module tracking.

        if (version == 1) return 0;
        var positions = GetAlignmentPatternPositions(version);
        var posCount = positions.Count;
        var totalCombinations = posCount * posCount;
        var overlaps = posCount >= 2 ? 3 : 0;
        return totalCombinations - overlaps;
    }

    /// <summary>
    /// Determines the minimum QR code version required for given data length.
    /// Searches capacity table for smallest version that can hold the data.
    /// </summary>
    /// <param name="length">Data length (in characters or bytes).</param>
    /// <param name="encoding">Encoding mode being used.</param>
    /// <param name="eccLevel">Error correction level.</param>
    /// <param name="eciMode">The character encoding declared in the symbol. Anything other than Default adds a 12-bit header, leaving less room for the text.</param>
    /// <param name="utf8BOM">Whether a UTF-8 byte order mark precedes the content. It costs three bytes of capacity, and only in Byte mode with UTF-8 ECI.</param>
    /// <returns>Version number (1-40).</returns>
    private static int GetVersion(int length, EncodingMode encoding, QREccLevel eccLevel, EciMode eciMode, bool utf8BOM)
    {
        if (TryGetVersion(length, encoding, eccLevel, eciMode, utf8BOM, out var version))
            return version;

        throw new InvalidOperationException($"Data too large for QR code (exceeds Version 40 capacity). " +
            $"Required: {eciMode.GetStandardQrHeaderBits() + ModeIndicatorBits} header bits + {length} data units, " +
            $"Mode: {encoding}, ECC: {eccLevel}, ECI: {eciMode}");
    }

    /// <summary>
    /// <see cref="GetVersion"/> without the throw; <c>false</c> means no version holds
    /// the content at this ECC level.
    /// </summary>
    /// <remarks>
    /// Calculates required bits including:
    /// - ECI header (0 or 12 bits)
    /// - Mode indicator (4 bits)
    /// - Character count indicator (8-16 bits, version-dependent)
    /// - Data (variable)
    /// </remarks>
    internal static bool TryGetVersion(int length, EncodingMode encoding, QREccLevel eccLevel, EciMode eciMode, bool utf8BOM, out int selectedVersion)
        => TryGetVersionInRange(length, encoding, eccLevel, eciMode, utf8BOM, QRVersionRange.MinVersion, QRVersionRange.MaxVersion, out selectedVersion);

    /// <summary>
    /// Whether the content fits the given version at this ECC level. Exposed for the
    /// monotonicity check the version range relies on being able to state.
    /// </summary>
    internal static bool FitsVersion(int length, EncodingMode encoding, QREccLevel eccLevel, EciMode eciMode, bool utf8BOM, int version)
        => TryGetVersionInRange(length, encoding, eccLevel, eciMode, utf8BOM, version, version, out _);

    /// <summary>
    /// <see cref="TryGetVersion"/> restricted to <paramref name="minVersion"/> through
    /// <paramref name="maxVersion"/>: the smallest version in that window that holds the
    /// content, or <c>false</c> when none does.
    /// </summary>
    /// <remarks>
    /// Scanning the window rather than comparing against the overall minimum keeps this
    /// correct without depending on the fit predicate being monotone in the version. It is
    /// monotone in practice (the capacity growth between adjacent versions dwarfs the
    /// character count indicator widening at versions 10 and 27), and
    /// <c>VersionRangeTest.StandardQr_FitsIsMonotoneInVersion</c> keeps that a checked
    /// fact rather than an assumption, but the search does not need it.
    /// </remarks>
    internal static bool TryGetVersionInRange(int length, EncodingMode encoding, QREccLevel eccLevel, EciMode eciMode, bool utf8BOM, int minVersion, int maxVersion, out int selectedVersion)
    {
        selectedVersion = 0;

        // ECI header overhead if eci specified
        var eciHeaderBits = eciMode.GetStandardQrHeaderBits();
        var modeIndicatorBits = ModeIndicatorBits;

        // UTF-8 BOM overhead ([0xEF, 0xBB, 0xBF] = 3 bytes = 24 bits) if specified.
        // Widened before the addition, not after: length + 3 wraps on its own near
        // int.MaxValue, and the later multiply would then price a maximal payload as tiny.
        long effectiveLength = length;
        if (utf8BOM && encoding == EncodingMode.Byte && eciMode == EciMode.Utf8)
        {
            effectiveLength += 3;
        }

        // Iterate through versions to find the minimum suitable version
        // Character count indicator size changes at version 10 and 27
        for (var version = minVersion; version <= maxVersion; version++)
        {
            var countIndicatorBits = encoding.GetCountIndicatorLength(version);

            // Data bits (already in length for Byte mode as byte count). Priced in long:
            // 8 × effectiveLength wraps int past ~268M bytes and would read as a fit, and
            // an early length guard here would skip the validation below it.
            long dataBits = encoding switch
            {
                EncodingMode.Numeric => CalculateNumericBits(length),
                EncodingMode.Alphanumeric => CalculateAlphanumericBits(length),
                EncodingMode.Byte => effectiveLength * 8L,
                _ => throw new ArgumentOutOfRangeException(nameof(encoding), $"Unsupported encoding mode: {encoding}")
            };

            // Total required bits
            var totalRequiredBits = eciHeaderBits + modeIndicatorBits + countIndicatorBits + dataBits;

            // Get actual capacity for this version and ECC level
            // Use CapacityTable (which has VersionInfo structure)
            var eccInfo = QRCodeConstants.GetEccInfo(version, eccLevel);
            var capacityBits = eccInfo.TotalDataCodewords * 8; // convert bytes to bits

            if (capacityBits >= totalRequiredBits)
            {
                selectedVersion = version;
                return true;
            }
        }

        return false;

        // Calculates actual bit count for numeric encoding.
        // 3 digits → 10 bits, 2 digits → 7 bits, 1 digit → 4 bits.
        static long CalculateNumericBits(int length)
        {
            var bits = length / 3 * 10L; // Groups of 3
            var remainder = length % 3;

            if (remainder == 2)
                bits += 7;
            else if (remainder == 1)
                bits += 4;

            return bits;
        }

        // Calculates actual bit count for alphanumeric encoding.
        // 2 characters → 11 bits, 1 character → 6 bits.
        static long CalculateAlphanumericBits(int length)
        {
            var bits = length / 2 * 11L; // Groups of 2

            if (length % 2 == 1)
                bits += 6; // Remaining 1 character

            return bits;
        }
    }

    // ---------------------------------------------------------------
    // Mixed-mode segmentation (QRSegmentation.Optimal).
    //
    // Kept in its own non-inlined methods so the single-mode entry points above keep
    // their frame and codegen. The plan buffer lives here rather than in the planner
    // because a plan is a caller-lent Span<ModeSegment> that never escapes: stack for
    // short content, pooled for long (a plan can never hold more runs than the
    // content has characters, so a text-length buffer always suffices).
    // ---------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static QRCodeData CreateOptimal(ReadOnlySpan<char> textSpan, QREccLevel eccLevel, in QRCodeGeneratorOptions options)
    {
        // Negative quiet zone first, then segmentation: the same precedence as
        // TryGetRequiredBufferSize and the rMQR generator, so every surface reports
        // the same error first for the same broken options. (An oversized quiet zone
        // is caught later by CalculateMatrixSize, as on every Standard QR path.)
        ValidateQuietZoneSize(options.QuietZoneSize);
        ValidateOptimalEntry(options.Segmentation);

        var analysis = TextAnalyzer.Analyze(textSpan, options.EciMode);

        // The BOM is a stream-level prefix written only into UTF-8 Byte-mode streams:
        // a split would relocate it into the middle of the decoded text, so that
        // combination emits the single-mode stream. Content whose single mode is
        // Numeric or Alphanumeric never carries a BOM, so it still splits.
        if (options.Utf8Bom && analysis.EciMode == EciMode.Utf8 && analysis.EncodingMode == EncodingMode.Byte)
        {
            var (bomVersion, bomEcc) = ResolveVersionAndEcc(textSpan, eccLevel, options);
            return CreateCore(textSpan, bomEcc, options.Utf8Bom, options.EciMode, bomVersion, options.QuietZoneSize, options.MaskPattern ?? AutomaticMask);
        }

        var version = SelectOptimalVersion(textSpan, eccLevel, in analysis, in options, out var useSegments);
        if (!useSegments)
            return CreateCore(textSpan, ResolveSingleLevel(in analysis, eccLevel, version, options.BoostEccLevel), options.Utf8Bom, options.EciMode, version, options.QuietZoneSize, options.MaskPattern ?? AutomaticMask);

        // The plan buffer is acquired only once a split is known to pay: content no
        // split can help (all-Numeric included) never rents it.
        ModeSegment[]? rentedPlan = null;
        Span<ModeSegment> plan = textSpan.Length <= QRSegmentPlanner.MaxStackSegments
            ? stackalloc ModeSegment[QRSegmentPlanner.MaxStackSegments]
            : (rentedPlan = ArrayPool<ModeSegment>.Shared.Rent(textSpan.Length));
        try
        {
            var resolvedEcc = BuildPlanOrFallback(textSpan, eccLevel, in analysis, in options, plan, ref version, out var segmentCount);
            if (segmentCount == 0)
                return CreateCore(textSpan, resolvedEcc, options.Utf8Bom, options.EciMode, version, options.QuietZoneSize, options.MaskPattern ?? AutomaticMask);

            var config = new QRConfiguration(version, resolvedEcc, analysis.EncodingMode, analysis.EciMode, false, QRCodeConstants.GetEccInfo(version, resolvedEcc), analysis.DataLength);
            var result = new QRCodeData(version, options.QuietZoneSize);
            var coreSize = result.GetCoreSize();
            var dataLength = coreSize * coreSize;
            byte[]? rentedWorkBuffer = null;
            try
            {
                rentedWorkBuffer = ArrayPool<byte>.Shared.Rent(dataLength);
                var workBuffer = rentedWorkBuffer.AsSpan(0, dataLength);
                WriteCoreModulesPlanned(textSpan, in config, plan.Slice(0, segmentCount), workBuffer, coreSize, options.MaskPattern ?? AutomaticMask);
                result.SetCoreData(workBuffer);
                return result;
            }
            finally
            {
                if (rentedWorkBuffer is not null)
                    ArrayPool<byte>.Shared.Return(rentedWorkBuffer, clearArray: false);
            }
        }
        finally
        {
            if (rentedPlan is not null)
                ArrayPool<ModeSegment>.Shared.Return(rentedPlan, clearArray: false);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CreateOptimalTo(ReadOnlySpan<char> textSpan, QREccLevel eccLevel, Span<byte> destination, in QRCodeGeneratorOptions options)
    {
        ValidateQuietZoneSize(options.QuietZoneSize);
        ValidateOptimalEntry(options.Segmentation);

        var analysis = TextAnalyzer.Analyze(textSpan, options.EciMode);

        if (options.Utf8Bom && analysis.EciMode == EciMode.Utf8 && analysis.EncodingMode == EncodingMode.Byte)
        {
            var (bomVersion, bomEcc) = ResolveVersionAndEcc(textSpan, eccLevel, options);
            return CreateCore(textSpan, bomEcc, destination, options.Utf8Bom, options.EciMode, bomVersion, options.QuietZoneSize, options.MaskPattern ?? AutomaticMask);
        }

        var version = SelectOptimalVersion(textSpan, eccLevel, in analysis, in options, out var useSegments);
        if (!useSegments)
            return CreateCore(textSpan, ResolveSingleLevel(in analysis, eccLevel, version, options.BoostEccLevel), destination, options.Utf8Bom, options.EciMode, version, options.QuietZoneSize, options.MaskPattern ?? AutomaticMask);

        ModeSegment[]? rentedPlan = null;
        Span<ModeSegment> plan = textSpan.Length <= QRSegmentPlanner.MaxStackSegments
            ? stackalloc ModeSegment[QRSegmentPlanner.MaxStackSegments]
            : (rentedPlan = ArrayPool<ModeSegment>.Shared.Rent(textSpan.Length));
        try
        {
            var resolvedEcc = BuildPlanOrFallback(textSpan, eccLevel, in analysis, in options, plan, ref version, out var segmentCount);
            if (segmentCount == 0)
                return CreateCore(textSpan, resolvedEcc, destination, options.Utf8Bom, options.EciMode, version, options.QuietZoneSize, options.MaskPattern ?? AutomaticMask);

            var config = new QRConfiguration(version, resolvedEcc, analysis.EncodingMode, analysis.EciMode, false, QRCodeConstants.GetEccInfo(version, resolvedEcc), analysis.DataLength);
            var segments = plan.Slice(0, segmentCount);
            var maskPattern = options.MaskPattern ?? AutomaticMask;
            var quietZoneSize = options.QuietZoneSize;

            var coreSize = QRCodeData.SizeFromVersion(version);
            var (totalSize, requiredSize) = CalculateMatrixSize(coreSize, quietZoneSize);
            if (destination.Length < requiredSize)
                throw new ArgumentException($"Destination buffer too small: {requiredSize} bytes required (version {version}, {totalSize}x{totalSize} modules), got {destination.Length} bytes. Use {nameof(TryGetRequiredBufferSize)} to calculate the required size.", nameof(destination));

            var target = destination.Slice(0, requiredSize);
            if (quietZoneSize == 0)
            {
                WriteCoreModulesPlanned(textSpan, in config, segments, target, coreSize, maskPattern);
            }
            else
            {
                target.Clear();
                byte[]? rentedWorkBuffer = null;
                try
                {
                    var dataLength = coreSize * coreSize;
                    rentedWorkBuffer = ArrayPool<byte>.Shared.Rent(dataLength);
                    var workBuffer = rentedWorkBuffer.AsSpan(0, dataLength);

                    WriteCoreModulesPlanned(textSpan, in config, segments, workBuffer, coreSize, maskPattern);

                    for (var row = 0; row < coreSize; row++)
                    {
                        var destOffset = (row + quietZoneSize) * totalSize + quietZoneSize;
                        workBuffer.Slice(row * coreSize, coreSize).CopyTo(target.Slice(destOffset, coreSize));
                    }
                }
                finally
                {
                    if (rentedWorkBuffer is not null)
                        ArrayPool<byte>.Shared.Return(rentedWorkBuffer, clearArray: false);
                }
            }

            return requiredSize;
        }
        finally
        {
            if (rentedPlan is not null)
                ArrayPool<ModeSegment>.Shared.Return(rentedPlan, clearArray: false);
        }
    }

    /// <summary>
    /// Everything the mixed-mode entry points must reject, gathered off the default
    /// path so <see cref="QRSegmentation.Single"/> pays only one compare. The
    /// parameter name matches the rMQR generator and the builder, so the three
    /// surfaces report the same argument for the same mistake.
    /// </summary>
    private static void ValidateOptimalEntry(QRSegmentation segmentation)
    {
        if (segmentation != QRSegmentation.Optimal)
            throw new ArgumentOutOfRangeException(nameof(segmentation), $"Invalid segmentation: {segmentation}");
    }

    /// <summary>
    /// Fits a version under mixed-mode segmentation. Throws the canonical "does not
    /// fit" errors when nothing fits. When <paramref name="useSegments"/> is false
    /// the caller emits the single-mode stream without ever acquiring a plan buffer.
    /// </summary>
    private static int SelectOptimalVersion(ReadOnlySpan<char> textSpan, QREccLevel eccLevel, in TextAnalysisResult analysis, in QRCodeGeneratorOptions options, out bool useSegments)
    {
        if (!QRSegmentPlanner.TrySelectVersion(textSpan, in analysis, eccLevel, options.Version.Min, options.Version.Max, out var version, out useSegments))
            ThrowDoesNotFit(in analysis, eccLevel, in options);
        return version;
    }

    /// <summary>
    /// The single-mode ECC boost of <see cref="ResolveVersionAndEcc"/>: the version
    /// stays, the level rises while the single-mode stream still fits it.
    /// </summary>
    private static QREccLevel ResolveSingleLevel(in TextAnalysisResult analysis, QREccLevel eccLevel, int version, bool boost)
    {
        while (boost && eccLevel < QREccLevel.H && FitsVersion(analysis.DataLength, analysis.EncodingMode, eccLevel + 1, analysis.EciMode, false, version))
            eccLevel += 1;
        return eccLevel;
    }

    /// <summary>
    /// Builds the plan for the selected version and resolves the ECC boost. A zero
    /// segment count means the single-mode stream is what gets emitted. Throws the
    /// canonical "does not fit" errors when the fallback does not fit either.
    /// </summary>
    private static QREccLevel BuildPlanOrFallback(ReadOnlySpan<char> textSpan, QREccLevel eccLevel, in TextAnalysisResult analysis, in QRCodeGeneratorOptions options, Span<ModeSegment> plan, ref int version, out int segmentCount)
    {
        if (!QRSegmentPlanner.TryBuildPlan(textSpan, analysis.EciMode, version, eccLevel, plan, out segmentCount))
        {
            // The plan that justified this version could not be rebuilt (it needed
            // more runs than the buffer holds, the decoder would misread it, or the
            // exact re-cost disagreed with the dynamic program). Fall back to the
            // single-mode fit, which reports the ordinary "does not fit" error when
            // there is no such fit — the honest outcome, because with the plan gone
            // nothing else can be emitted.
            segmentCount = 0;
            if (!TryGetVersionInRange(analysis.DataLength, analysis.EncodingMode, eccLevel, analysis.EciMode, false, options.Version.Min, options.Version.Max, out version))
                ThrowDoesNotFit(in analysis, eccLevel, in options);
            return ResolveSingleLevel(in analysis, eccLevel, version, options.BoostEccLevel);
        }

        // Boost keeps the single-path contract: the version stays, the level rises
        // while the stream still fits it. The plan itself is unaffected — its cost
        // depends only on the version — so only the capacity side of the compare moves.
        if (options.BoostEccLevel)
        {
            var streamBits = QRSegmentPlanner.MeasurePlan(version, plan.Slice(0, segmentCount)) + analysis.EciMode.GetStandardQrHeaderBits();
            while (eccLevel < QREccLevel.H && streamBits <= QRCodeConstants.GetEccInfo(version, eccLevel + 1).TotalDataCodewords * 8)
                eccLevel += 1;
        }

        return eccLevel;
    }

    /// <summary>
    /// <see cref="SelectOptimalVersion"/> and <see cref="BuildPlanOrFallback"/>
    /// without the boost and the throw: the version an Optimal encode would use, for
    /// buffer sizing. The two must agree, fallback included, or a buffer sized here
    /// can be too small for the encode.
    /// </summary>
    private static bool TryPlanOptimalVersion(ReadOnlySpan<char> textSpan, QREccLevel eccLevel, in TextAnalysisResult analysis, in QRCodeGeneratorOptions options, out int version)
    {
        if (!QRSegmentPlanner.TrySelectVersion(textSpan, in analysis, eccLevel, options.Version.Min, options.Version.Max, out version, out var useSegments))
            return false;
        if (!useSegments)
            return true;

        ModeSegment[]? rentedPlan = null;
        Span<ModeSegment> plan = textSpan.Length <= QRSegmentPlanner.MaxStackSegments
            ? stackalloc ModeSegment[QRSegmentPlanner.MaxStackSegments]
            : (rentedPlan = ArrayPool<ModeSegment>.Shared.Rent(textSpan.Length));
        try
        {
            if (QRSegmentPlanner.TryBuildPlan(textSpan, analysis.EciMode, version, eccLevel, plan, out _))
                return true;
        }
        finally
        {
            if (rentedPlan is not null)
                ArrayPool<ModeSegment>.Shared.Return(rentedPlan, clearArray: false);
        }

        return TryGetVersionInRange(analysis.DataLength, analysis.EncodingMode, eccLevel, analysis.EciMode, false, options.Version.Min, options.Version.Max, out version);
    }

    /// <summary>
    /// The canonical "does not fit" errors of the single-mode path, so turning
    /// segmentation on cannot reclassify an error: unconstrained overflow is
    /// <see cref="InvalidOperationException"/>, a constrained range is
    /// <see cref="ArgumentException"/>.
    /// </summary>
    private static void ThrowDoesNotFit(in TextAnalysisResult analysis, QREccLevel eccLevel, in QRCodeGeneratorOptions options)
    {
        if (options.Version.IsAny)
        {
            // A mixed plan encodes at least what a single mode does, so overflow under
            // Optimal implies single-mode overflow; GetVersion recomputes only to
            // throw that exact exception.
            GetVersion(analysis.DataLength, analysis.EncodingMode, eccLevel, analysis.EciMode, false);
        }
        throw new ArgumentException(DoesNotFitMessage(options.Version, eccLevel, in analysis), nameof(options));
    }

    /// <summary>
    /// <see cref="WriteCoreModules"/> for a planned mixed-mode split: identical
    /// pipeline, with the segmented data stream in place of the single-mode one.
    /// </summary>
    private static void WriteCoreModulesPlanned(ReadOnlySpan<char> textSpan, in QRConfiguration config, ReadOnlySpan<ModeSegment> segments, Span<byte> coreBuffer, int coreSize, int maskPattern)
    {
        var dataCapacity = CalculateMaxBitStringLength(config.Version, config.EccLevel, config.Encoding);
        var dataBufferSize = (dataCapacity + 7) / 8;
        var totalBlocks = config.EccInfo.BlocksInGroup1 + config.EccInfo.BlocksInGroup2;
        var eccBufferSize = totalBlocks * config.EccInfo.ECCPerBlock;
        var interleavedSize = BinaryInterleaver.CalculateInterleavedSize(config.EccInfo, QRCodeConstants.GetRemainderBits(config.Version));

        Span<byte> dataBuffer = stackalloc byte[dataBufferSize];
        Span<byte> eccBuffer = stackalloc byte[eccBufferSize];
        Span<byte> interleavedBuffer = stackalloc byte[interleavedSize];

        var encodedLength = EncodeDataSegmented(textSpan, in config, segments, dataBuffer);
        var encodedData = dataBuffer.Slice(0, encodedLength);

        CalculateErrorCorrection(encodedData, config.EccInfo, eccBuffer);
        InterleaveCodewords(encodedData, eccBuffer, config.EccInfo, config.Version, interleavedBuffer);
        WriteQRMatrix(coreBuffer, coreSize, config.Version, interleavedBuffer, config.EccLevel, maskPattern);
    }

    /// <summary>
    /// <see cref="EncodeData"/> for a planned mixed-mode split: ECI prefix (when
    /// any), then per run mode indicator + count indicator + payload, then the
    /// shared terminator / padding tail.
    /// </summary>
    private static int EncodeDataSegmented(ReadOnlySpan<char> textSpan, in QRConfiguration config, ReadOnlySpan<ModeSegment> segments, Span<byte> buffer)
    {
        var encoder = new QRBinaryEncoder(buffer);
        encoder.WriteSegments(textSpan, segments, config.Version, config.EciMode);
        encoder.WritePadding(config.EccInfo.TotalDataCodewords * 8);
        return encoder.ByteCount;
    }

    /// <summary>
    /// Holds QR configuration parameters determined during setup.
    /// </summary>
    /// <param name="Version">QR code version (1-40) selected for encoding.</param>
    /// <param name="EccLevel">Error correction level used for the QR code.</param>
    /// <param name="Encoding">Encoding mode (Numeric, Alphanumeric, Byte, etc.).</param>
    /// <param name="EciMode">ECI mode specifying character encoding.</param>
    /// <param name="Utf8Bom">Indicates if UTF-8 BOM is included in the encoded data.</param>
    /// <param name="EccInfo">Error correction information for the selected version and ECC level.</param>
    /// <param name="DataLength">How much content there is to encode, counted in the units the encoding mode uses.</param>
    private readonly record struct QRConfiguration(int Version, QREccLevel EccLevel, EncodingMode Encoding, EciMode EciMode, bool Utf8Bom, in ECCInfo EccInfo, int DataLength);
}
