using FeatherQR;

namespace QRInteropFixtures;

/// <summary>
/// Buffer sizing for fixture generation, where the content is chosen by the tool and
/// therefore known to fit.
/// </summary>
/// <remarks>
/// The generators answer sizing only as a <c>Try</c>: for a caller handling arbitrary
/// content, "does not fit" is an ordinary answer rather than a defect. A fixture tool picks
/// its own payloads, so a miss is a broken fixture and should stop the run loudly.
/// </remarks>
internal static class Sizing
{
    public static QRCodeCalculatedSize Required(ReadOnlySpan<char> text, QREccLevel eccLevel, in QRCodeGeneratorOptions options = default)
        => QRCodeGenerator.TryGetRequiredBufferSize(text, eccLevel, out var size, options)
            ? size
            : throw DoesNotFit("Standard QR", text.Length, eccLevel);

    public static MicroQRCodeCalculatedSize Required(ReadOnlySpan<char> text, MicroQREccLevel eccLevel, in MicroQRCodeGeneratorOptions options = default)
        => MicroQRCodeGenerator.TryGetRequiredBufferSize(text, eccLevel, out var size, options)
            ? size
            : throw DoesNotFit("Micro QR", text.Length, eccLevel);

    public static RmQRCodeCalculatedSize Required(ReadOnlySpan<char> text, RmQREccLevel eccLevel, in RmQRCodeGeneratorOptions options = default)
        => RmQRCodeGenerator.TryGetRequiredBufferSize(text, eccLevel, out var size, options)
            ? size
            : throw DoesNotFit("rMQR", text.Length, eccLevel);

    private static InvalidOperationException DoesNotFit<TEcc>(string symbology, int length, TEcc eccLevel)
        => new($"Fixture generation is wrong: {length} characters do not fit any {symbology} symbol at ECC level {eccLevel}.");

    public static QRCodeCalculatedSize Required(ReadOnlySpan<char> text, QREccLevel eccLevel, int quietZoneSize)
        => Required(text, eccLevel, new QRCodeGeneratorOptions { QuietZoneSize = quietZoneSize });

    public static MicroQRCodeCalculatedSize Required(ReadOnlySpan<char> text, MicroQREccLevel eccLevel, int quietZoneSize)
        => Required(text, eccLevel, new MicroQRCodeGeneratorOptions { QuietZoneSize = quietZoneSize });

    public static RmQRCodeCalculatedSize Required(ReadOnlySpan<char> text, RmQREccLevel eccLevel, int quietZoneSize)
        => Required(text, eccLevel, new RmQRCodeGeneratorOptions { QuietZoneSize = quietZoneSize });
}
