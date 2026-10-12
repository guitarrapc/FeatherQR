using System.Buffers;
using System.Diagnostics;
using FeatherQR.Internals;
using FeatherQR.Internals.BinaryEncoders;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR;

/// <summary>
/// The FNC1 half of the generator (ISO/IEC 18004 6.4.8, specs/standardqr-encoder.md FNC1). The single-mode route under
/// FNC1 is the resolved route with the indicator's bits in every version fit and the indicator in the stream, written
/// as its own methods rather than as parameters of the route without FNC1, so that route keeps its instructions.
/// The mixed-mode route takes the mode from the options it already carries.
/// </summary>
public static partial class QRCodeGenerator
{
    /// <summary><see cref="QRSegmentation.Single"/> with an FNC1 mode: the resolved route's steps with the indicator's bits.</summary>
    private static QRCodeData CreateFnc1(ReadOnlySpan<char> textSpan, QREccLevel eccLevel, in QRCodeGeneratorOptions options)
    {
        var config = ResolveConfigurationFnc1(textSpan, eccLevel, in options);
        return CreateCoreFnc1(textSpan, in config, options.Fnc1, options.QuietZoneSize, options.MaskPattern ?? AutomaticMask);
    }

    private static int CreateFnc1To(ReadOnlySpan<char> textSpan, QREccLevel eccLevel, Span<byte> destination, in QRCodeGeneratorOptions options)
    {
        var config = ResolveConfigurationFnc1(textSpan, eccLevel, in options);
        return CreateCoreFnc1(textSpan, in config, options.Fnc1, destination, options.QuietZoneSize, options.MaskPattern ?? AutomaticMask);
    }

    /// <summary>The analysis under the FNC1 rule and the version and level it resolves to, the options already validated by the cold entry.</summary>
    private static QRConfiguration ResolveConfigurationFnc1(ReadOnlySpan<char> textSpan, QREccLevel eccLevel, in QRCodeGeneratorOptions options)
    {
        var analysis = TextAnalyzer.AnalyzeFnc1(textSpan, options.EciMode, AllowsKanji(in options));
        var (version, resolvedEcc) = ResolveVersionAndEccFnc1(in analysis, eccLevel, in options, options.Fnc1.StandardQrHeaderBits);
        return PrepareConfiguration(in analysis, resolvedEcc, options.Utf8Bom, version);
    }

    /// <summary><see cref="ResolveVersionAndEcc"/> with the FNC1 indicator's bits in every fit, and the same two errors when nothing fits.</summary>
    private static (int Version, QREccLevel EccLevel) ResolveVersionAndEccFnc1(in TextAnalysisResult analysis, QREccLevel eccLevel, in QRCodeGeneratorOptions options, int fnc1HeaderBits)
    {
        // Without FNC1 the route's own resolution, so its errors stay the captured ones to the letter.
        if (fnc1HeaderBits == 0)
            return ResolveVersionAndEcc(in analysis, eccLevel, in options);

        if (!TryGetVersionInRangeFnc1(analysis.DataLength, analysis.EncodingMode, eccLevel, analysis.EciMode, options.Utf8Bom, fnc1HeaderBits, options.Version.Min, options.Version.Max, out var version))
        {
            if (options.Version.IsAny)
            {
                throw new InvalidOperationException($"Data too large for QR code (exceeds Version 40 capacity). " +
                    $"Required: {analysis.EciMode.GetStandardQrHeaderBits() + fnc1HeaderBits + ModeIndicatorBits} header bits + {analysis.DataLength} data units, " +
                    $"Mode: {analysis.EncodingMode}, ECC: {eccLevel}, ECI: {analysis.EciMode}, FNC1: {fnc1HeaderBits} bits");
            }
            throw new ArgumentException(DoesNotFitMessage(options.Version, eccLevel, in analysis), nameof(options));
        }

        while (options.BoostEccLevel && eccLevel < QREccLevel.H
            && FitsVersionFnc1(analysis.DataLength, analysis.EncodingMode, eccLevel + 1, analysis.EciMode, options.Utf8Bom, fnc1HeaderBits, version))
        {
            eccLevel += 1;
        }

        return (version, eccLevel);
    }

    internal static bool FitsVersionFnc1(int length, EncodingMode encoding, QREccLevel eccLevel, EciMode eciMode, bool utf8BOM, int fnc1HeaderBits, int version)
        => TryGetVersionInRangeFnc1(length, encoding, eccLevel, eciMode, utf8BOM, fnc1HeaderBits, version, version, out _);

    /// <summary>
    /// <see cref="TryGetVersionInRange"/> with the FNC1 indicator's bits (4 in first position, 12 in second, 0 for none)
    /// in the header. A copy rather than a parameter on the original, which is inlined into the default route.
    /// </summary>
    internal static bool TryGetVersionInRangeFnc1(int length, EncodingMode encoding, QREccLevel eccLevel, EciMode eciMode, bool utf8BOM, int fnc1HeaderBits, int minVersion, int maxVersion, out int selectedVersion)
    {
        selectedVersion = 0;
        var headerBits = eciMode.GetStandardQrHeaderBits() + fnc1HeaderBits + ModeIndicatorBits;

        long effectiveLength = length;
        if (utf8BOM && encoding == EncodingMode.Byte && eciMode == EciMode.Utf8)
            effectiveLength += 3;

        long dataBits = encoding switch
        {
            EncodingMode.Numeric => length / 3 * 10L + (length % 3) switch { 2 => 7, 1 => 4, _ => 0 },
            EncodingMode.Alphanumeric => length / 2 * 11L + length % 2 * 6,
            EncodingMode.Byte => effectiveLength * 8L,
            EncodingMode.Kanji => length * 13L,
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), $"Unsupported encoding mode: {encoding}")
        };

        for (var version = minVersion; version <= maxVersion; version++)
        {
            var totalRequiredBits = headerBits + encoding.GetCountIndicatorLength(version) + dataBits;
            var capacityBits = QRCodeConstants.GetEccInfo(version, eccLevel).TotalDataCodewords * 8;
            if (capacityBits >= totalRequiredBits)
            {
                selectedVersion = version;
                return true;
            }
        }

        return false;
    }

    /// <summary><see cref="CreateCore(ReadOnlySpan{char}, in QRConfiguration, int, int)"/> with the FNC1 headers in the stream.</summary>
    private static QRCodeData CreateCoreFnc1(ReadOnlySpan<char> textSpan, in QRConfiguration config, Fnc1Mode fnc1, int quietZoneSize, int maskPattern)
    {
        var result = new QRCodeData(config.Version, quietZoneSize);
        var coreSize = result.GetCoreSize();
        var dataLength = coreSize * coreSize;

        byte[]? rentedWorkBuffer = null;
        try
        {
            rentedWorkBuffer = ArrayPool<byte>.Shared.Rent(dataLength);
            var workBuffer = rentedWorkBuffer.AsSpan(0, dataLength);
            WriteCoreModulesFnc1(textSpan, config, fnc1, workBuffer, coreSize, maskPattern);
            result.SetCoreData(workBuffer);
            return result;
        }
        finally
        {
            if (rentedWorkBuffer is not null)
                ArrayPool<byte>.Shared.Return(rentedWorkBuffer, clearArray: false);
        }
    }

    /// <summary><see cref="CreateCore(ReadOnlySpan{char}, in QRConfiguration, Span{byte}, int, int)"/> with the FNC1 headers in the stream.</summary>
    private static int CreateCoreFnc1(ReadOnlySpan<char> textSpan, in QRConfiguration config, Fnc1Mode fnc1, Span<byte> destination, int quietZoneSize, int maskPattern)
    {
        var coreSize = QRCodeData.SizeFromVersion(config.Version);
        var (totalSize, requiredSize) = CalculateMatrixSize(coreSize, quietZoneSize);
        if (destination.Length < requiredSize)
            throw new ArgumentException($"Destination buffer too small: {requiredSize} bytes required (version {config.Version}, {totalSize}x{totalSize} modules), got {destination.Length} bytes. Use {nameof(TryGetRequiredBufferSize)} to calculate the required size.", nameof(destination));

        var target = destination.Slice(0, requiredSize);
        if (quietZoneSize == 0)
        {
            WriteCoreModulesFnc1(textSpan, config, fnc1, target, coreSize, maskPattern);
        }
        else
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            WriteCoreModulesFnc1(textSpan, config, fnc1, target.Slice(0, coreSize * coreSize), coreSize, maskPattern);
            QuietZoneWindow.CenterCore(target, coreSize, coreSize, quietZoneSize);
#else
            target.Clear();
            var rented = ArrayPool<byte>.Shared.Rent(coreSize * coreSize);
            try
            {
                var core = rented.AsSpan(0, coreSize * coreSize);
                WriteCoreModulesFnc1(textSpan, config, fnc1, core, coreSize, maskPattern);
                CopyIntoWindow(core, target, coreSize, quietZoneSize);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
#endif
        }

        return requiredSize;
    }

    /// <summary><see cref="WriteCoreModules"/> with the FNC1 headers in the stream: the same pipeline after the data.</summary>
    private static void WriteCoreModulesFnc1(ReadOnlySpan<char> textSpan, in QRConfiguration config, Fnc1Mode fnc1, Span<byte> coreBuffer, int coreSize, int maskPattern)
    {
        var dataCapacity = CalculateMaxBitStringLength(config.Version, config.EccLevel, config.Encoding);
        var dataBufferSize = (dataCapacity + 7) / 8;
        var totalBlocks = config.EccInfo.BlocksInGroup1 + config.EccInfo.BlocksInGroup2;
        var eccBufferSize = totalBlocks * config.EccInfo.ECCPerBlock;
        var interleavedSize = BinaryInterleaver.CalculateInterleavedSize(config.EccInfo, QRCodeConstants.GetRemainderBits(config.Version));

        Span<byte> dataBuffer = stackalloc byte[dataBufferSize];
        Span<byte> eccBuffer = stackalloc byte[eccBufferSize];
        Span<byte> interleavedBuffer = stackalloc byte[interleavedSize];

        var encodedLength = EncodeDataFnc1(textSpan, config, fnc1, dataBuffer);
        var encodedData = dataBuffer.Slice(0, encodedLength);

        CalculateErrorCorrection(encodedData, config.EccInfo, eccBuffer);
        InterleaveCodewords(encodedData, eccBuffer, config.EccInfo, config.Version, interleavedBuffer);
        WriteQRMatrix(coreBuffer, coreSize, config.Version, interleavedBuffer, config.EccLevel, maskPattern);
    }

    /// <summary><see cref="EncodeData"/> under FNC1: ECI, the FNC1 indicator, the mode, the count, the data under the <c>%</c> rule, the padding. No Structured Append header: a set refuses FNC1.</summary>
    private static int EncodeDataFnc1(ReadOnlySpan<char> textSpan, in QRConfiguration config, Fnc1Mode fnc1, Span<byte> buffer)
    {
        Debug.Assert(config.StructuredAppend.IsEmpty, "a Structured Append set refuses FNC1");
        var encoder = new QRBinaryEncoder(buffer);
        encoder.WriteModeFnc1(config.Encoding, config.EciMode, fnc1);
        encoder.WriteCharacterCount(config.DataLength, config.Encoding.GetCountIndicatorLength(config.Version));
        encoder.WriteDataFnc1(textSpan, config.Encoding, config.EciMode, config.Utf8Bom);
        encoder.WritePadding(config.EccInfo.TotalDataCodewords * 8);
        return encoder.ByteCount;
    }
}
