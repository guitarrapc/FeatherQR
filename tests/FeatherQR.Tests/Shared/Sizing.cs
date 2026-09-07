namespace FeatherQR.Tests;

/// <summary>
/// Buffer sizing for call sites where the fit is a <em>precondition</em> of the test
/// rather than the thing under test.
/// </summary>
/// <remarks>
/// The generators answer "how big is this" only as a <c>Try</c>
/// (specs/qrcode-symbologies.md, Public API direction), because for a caller handling
/// arbitrary content "it does not fit" is an ordinary answer. A test that has chosen its
/// own content knows it fits, and would rather crash than branch — so the throw lives
/// here, once, where it is obviously a test assertion and not a library contract.
///
/// Do not use this in a test whose subject <em>is</em> the fit: assert on
/// <c>TryGetRequiredBufferSize</c> directly there, so a wrong answer reads as a failed
/// assertion rather than an exception from a helper.
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
        => new($"Test precondition failed: {length} characters do not fit any {symbology} symbol at ECC level {eccLevel}.");

    public static QRCodeCalculatedSize Required(ReadOnlySpan<char> text, QREccLevel eccLevel, int quietZoneSize)
        => Required(text, eccLevel, new QRCodeGeneratorOptions { QuietZoneSize = quietZoneSize });

    public static MicroQRCodeCalculatedSize Required(ReadOnlySpan<char> text, MicroQREccLevel eccLevel, int quietZoneSize)
        => Required(text, eccLevel, new MicroQRCodeGeneratorOptions { QuietZoneSize = quietZoneSize });

    public static RmQRCodeCalculatedSize Required(ReadOnlySpan<char> text, RmQREccLevel eccLevel, int quietZoneSize)
        => Required(text, eccLevel, new RmQRCodeGeneratorOptions { QuietZoneSize = quietZoneSize });
}
