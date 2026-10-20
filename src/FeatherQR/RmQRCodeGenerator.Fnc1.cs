using System.Buffers;
using System.Runtime.CompilerServices;
using FeatherQR.Internals;
using FeatherQR.Internals.RmQR;

namespace FeatherQR;

/// <summary>
/// The planning route of the generator (specs/rmqr-encoder.md, FNC1): a segmentation other than
/// <see cref="RmQRSegmentation.Single"/>, an FNC1 mode, or both. The single-mode route under FNC1 is the single-mode
/// route's steps with the indicator's bits in the version fit and the indicator in the stream, written as its own
/// methods so that route keeps its instructions. The mixed-mode route takes the mode as a parameter.
/// </summary>
public static partial class RmQRCodeGenerator
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RmQRCodeData CreatePlanned(ReadOnlySpan<char> textSpan, RmQREccLevel eccLevel, in RmQRCodeGeneratorOptions options)
    {
        if (options.Segmentation != RmQRSegmentation.Single)
            return CreateOptimal(textSpan, eccLevel, options.EciMode, options.AllowKanji, options.Version, options.FitStrategy, options.Height, options.QuietZoneSize, options.Segmentation, options.Fnc1);

        var config = PrepareConfigurationFnc1(textSpan, eccLevel, in options);
        var result = new RmQRCodeData(config.Version, options.QuietZoneSize);
        var coreWidth = result.GetCoreWidth();
        var coreLength = coreWidth * result.GetCoreHeight();
        var rented = ArrayPool<byte>.Shared.Rent(coreLength);
        try
        {
            var core = rented.AsSpan(0, coreLength);
            WriteCoreModulesFnc1(textSpan, in config, options.Fnc1, core, coreWidth);
            result.SetCoreData(core);
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CreatePlannedTo(ReadOnlySpan<char> textSpan, RmQREccLevel eccLevel, Span<byte> destination, in RmQRCodeGeneratorOptions options)
    {
        if (options.Segmentation != RmQRSegmentation.Single)
            return CreateOptimalTo(textSpan, eccLevel, options.EciMode, options.AllowKanji, destination, options.Version, options.FitStrategy, options.Height, options.QuietZoneSize, options.Segmentation, options.Fnc1);

        var quietZoneSize = options.QuietZoneSize;
        var config = PrepareConfigurationFnc1(textSpan, eccLevel, in options);
        var coreWidth = RmQRConstants.GetWidth(config.Version);
        var coreHeight = RmQRConstants.GetHeight(config.Version);
        var totalWidth = coreWidth + quietZoneSize * 2;
        var totalHeight = coreHeight + quietZoneSize * 2;
        var requiredSize = totalWidth * totalHeight;
        if (destination.Length < requiredSize)
            throw new ArgumentException($"Destination buffer too small: {requiredSize} bytes required (version {config.Version}, {totalWidth}x{totalHeight} modules), got {destination.Length} bytes. Use {nameof(TryGetRequiredBufferSize)} to calculate the required size.", nameof(destination));

        var target = destination.Slice(0, requiredSize);
        if (quietZoneSize == 0)
        {
            WriteCoreModulesFnc1(textSpan, in config, options.Fnc1, target, coreWidth);
            return requiredSize;
        }

        QuietZoneWindow.ClearMargins(target, coreWidth, coreHeight, quietZoneSize);
        WriteCoreModulesFnc1(textSpan, in config, options.Fnc1, target.Slice(quietZoneSize * totalWidth + quietZoneSize), totalWidth);
        return requiredSize;
    }

    /// <summary><see cref="TryGetRequiredBufferSize"/>'s fit on the planning route: the plan under a segmentation, or the single-mode fit under FNC1.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryGetRequiredBufferSizePlanned(ReadOnlySpan<char> text, RmQREccLevel eccLevel, in RmQRCodeGeneratorOptions options, out RmQRVersion version)
    {
        if (options.Segmentation != RmQRSegmentation.Single)
            return TryPlanOptimalVersion(text, eccLevel, options.EciMode, options.AllowKanji, options.Version, options.FitStrategy, options.Height, options.Segmentation, options.Fnc1, out version);

        ValidateEci(text, options.EciMode);
        var analysis = TextAnalyzer.AnalyzeFnc1(text, options.EciMode, options.AllowKanji);
        return RmQRVersionSelector.TrySelectFnc1(analysis.EncodingMode, analysis.DataLength, analysis.EciMode, eccLevel, options.Version, options.FitStrategy, options.Height, options.Fnc1.RmQrHeaderBits, out version);
    }

    /// <summary><see cref="PrepareConfiguration"/> under FNC1: the analysis under the FNC1 rule, and the version fit with the indicator's bits.</summary>
    private static RmQRConfiguration PrepareConfigurationFnc1(ReadOnlySpan<char> textSpan, RmQREccLevel eccLevel, in RmQRCodeGeneratorOptions options)
    {
        ValidateEci(textSpan, options.EciMode);
        var analysis = TextAnalyzer.AnalyzeFnc1(textSpan, options.EciMode, options.AllowKanji);
        var version = RmQRVersionSelector.SelectFnc1(analysis.EncodingMode, analysis.DataLength, analysis.EciMode, eccLevel, options.Version, options.FitStrategy, options.Height, options.Fnc1.RmQrHeaderBits);
        return new RmQRConfiguration(version, eccLevel, analysis);
    }

    /// <summary><see cref="WriteCoreModules"/> with the FNC1 headers in the stream: the same pipeline after the data codewords.</summary>
    private static void WriteCoreModulesFnc1(ReadOnlySpan<char> textSpan, in RmQRConfiguration config, Fnc1Mode fnc1, Span<byte> core, int stride)
    {
        Span<byte> dataCodewords = stackalloc byte[MaxDataCodewords];
        var analysis = config.Analysis;
        var dataCount = RmQRBinaryEncoder.EncodeDataCodewordsFnc1(textSpan, config.Version, config.EccLevel, in analysis, fnc1, dataCodewords);

        Span<byte> finalMessage = stackalloc byte[MaxFinalMessageBytes];
        finalMessage = finalMessage.Slice(0, RmQRCodewordEncoder.GetFinalMessageSize(config.Version));
        RmQRCodewordEncoder.AssembleFinalMessage(dataCodewords.Slice(0, dataCount), config.Version, config.EccLevel, finalMessage);

        RmQRModulePlacer.PlaceSymbol(core, stride, config.Version, config.EccLevel, finalMessage);
    }
}
