namespace FeatherQR.Tests;

/// <summary>
/// The exception a caller catches is part of the API, and no compiler checks it, so it is measured here.
/// Content that fits no version is an <see cref="ArgumentException"/> in every symbology and every overload.
/// Its <see cref="ArgumentException.ParamName"/> is <c>options</c> when an option narrowed the versions (a version, a range, an rMQR height),
/// and <see langword="null"/> when nothing did, since the content alone is too long.
/// A decoder's non-positive size is an <see cref="ArgumentOutOfRangeException"/> naming that size, for module matrices as for images.
/// </summary>
public class ErrorContractTest
{
    private static readonly string LongText = new('A', 8000);
    private static readonly byte[] LongBytes = new byte[8000];

    public static IEnumerable<(string Name, Action Create, string? ParamName)> DoesNotFitCases()
    {
        // Standard QR: the default route, a boost, single-mode segmentation and FNC1 each reach their own fit, and the byte overloads another
        yield return ("qr any", () => QRCodeGenerator.Create(LongText, QREccLevel.H), null);
        yield return ("qr any destination", () => QRCodeGenerator.Create(LongText.AsSpan(), QREccLevel.H, new byte[200_000]), null);
        yield return ("qr any boost", () => QRCodeGenerator.Create(LongText, QREccLevel.L, new QRCodeGeneratorOptions { BoostEccLevel = true }), null);
        yield return ("qr any single", () => QRCodeGenerator.Create(LongText, QREccLevel.H, new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Single }), null);
        yield return ("qr any fnc1", () => QRCodeGenerator.Create(LongText, QREccLevel.H, new QRCodeGeneratorOptions { Fnc1 = Fnc1Mode.FirstPosition }), null);
        yield return ("qr any fnc1 single", () => QRCodeGenerator.Create(LongText, QREccLevel.H, new QRCodeGeneratorOptions { Fnc1 = Fnc1Mode.FirstPosition, Segmentation = QRSegmentation.Single }), null);
        yield return ("qr any bytes", () => QRCodeGenerator.Create(LongBytes, QREccLevel.H), null);
        yield return ("qr any bytes destination", () => QRCodeGenerator.Create(LongBytes, QREccLevel.H, new byte[200_000]), null);
        yield return ("qr range", () => QRCodeGenerator.Create(LongText, QREccLevel.H, new QRCodeGeneratorOptions { Version = QRVersionRange.Between(1, 10) }), "options");
        yield return ("qr exact", () => QRCodeGenerator.Create(LongText, QREccLevel.H, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(5) }), "options");
        yield return ("qr exact fnc1", () => QRCodeGenerator.Create(LongText, QREccLevel.H, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(5), Fnc1 = Fnc1Mode.FirstPosition }), "options");
        yield return ("qr exact bytes", () => QRCodeGenerator.Create(LongBytes, QREccLevel.H, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(5) }), "options");

        yield return ("micro any", () => MicroQRCodeGenerator.Create(LongText, MicroQREccLevel.L), null);
        yield return ("micro any destination", () => MicroQRCodeGenerator.Create(LongText.AsSpan(), MicroQREccLevel.L, new byte[10_000]), null);
        yield return ("micro any single", () => MicroQRCodeGenerator.Create(LongText, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Single }), null);
        yield return ("micro any bytes", () => MicroQRCodeGenerator.Create(LongBytes, MicroQREccLevel.L), null);
        yield return ("micro range", () => MicroQRCodeGenerator.Create(LongText, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = MicroQRVersionRange.Between(MicroQRVersion.M2, MicroQRVersion.M3) }), "options");
        yield return ("micro exact", () => MicroQRCodeGenerator.Create(LongText, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M2 }), "options");
        yield return ("micro exact mode unavailable", () => MicroQRCodeGenerator.Create("AC-42", MicroQREccLevel.ErrorDetectionOnly, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M1 }), "options");
        yield return ("micro exact bytes", () => MicroQRCodeGenerator.Create(LongBytes, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M3 }), "options");

        yield return ("rmqr any", () => RmQRCodeGenerator.Create(LongText, RmQREccLevel.M), null);
        yield return ("rmqr any destination", () => RmQRCodeGenerator.Create(LongText.AsSpan(), RmQREccLevel.M, new byte[10_000]), null);
        yield return ("rmqr any single", () => RmQRCodeGenerator.Create(LongText, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Single }), null);
        yield return ("rmqr any bytes", () => RmQRCodeGenerator.Create(LongBytes, RmQREccLevel.M), null);
        yield return ("rmqr height", () => RmQRCodeGenerator.Create(LongText, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Height = RmQRHeight.H7 }), "options");
        yield return ("rmqr exact", () => RmQRCodeGenerator.Create(LongText, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43 }), "options");
        yield return ("rmqr exact single", () => RmQRCodeGenerator.Create(LongText, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43, Segmentation = RmQRSegmentation.Single }), "options");
        yield return ("rmqr exact bytes", () => RmQRCodeGenerator.Create(LongBytes, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43 }), "options");
    }

    [Test]
    [MethodDataSource(nameof(DoesNotFitCases))]
    public async Task ContentThatFitsNoVersion_IsAnArgumentException(string name, Action create, string? paramName)
    {
        var error = Catch(create);

        await Assert.That(error?.GetType()).IsEqualTo(typeof(ArgumentException)).Because(name);
        await Assert.That(((ArgumentException)error!).ParamName).IsEqualTo(paramName).Because(name);
        await Assert.That(error.Message).DoesNotContain("requestedVersion").Because(name);
    }

    /// <summary>An undefined option value names the option member, as rMQR's height and fit strategy do, on every route to the version check.</summary>
    public static IEnumerable<(string Name, Action Create)> UndefinedRmQRVersionCases()
    {
        var options = new RmQRCodeGeneratorOptions { Version = (RmQRVersion)99 };
        yield return ("create", () => RmQRCodeGenerator.Create("1", RmQREccLevel.M, options));
        yield return ("create single", () => RmQRCodeGenerator.Create("1", RmQREccLevel.M, options with { Segmentation = RmQRSegmentation.Single }));
        yield return ("create destination", () => RmQRCodeGenerator.Create("1".AsSpan(), RmQREccLevel.M, new byte[10_000], options));
        yield return ("create bytes", () => RmQRCodeGenerator.Create(new byte[] { 1 }, RmQREccLevel.M, options));
        yield return ("size", () => RmQRCodeGenerator.TryGetRequiredBufferSize("1", RmQREccLevel.M, out _, options));
    }

    [Test]
    [MethodDataSource(nameof(UndefinedRmQRVersionCases))]
    public async Task UndefinedRmQRVersion_NamesTheOptionMember(string name, Action create)
    {
        var error = Catch(create);

        await Assert.That(error?.GetType()).IsEqualTo(typeof(ArgumentOutOfRangeException)).Because(name);
        await Assert.That(((ArgumentException)error!).ParamName).IsEqualTo("version").Because(name);
    }

    public static IEnumerable<(string Name, Action Decode, string ParamName)> NonPositiveSizeCases()
    {
        var modules = new byte[100];
        yield return ("qr matrix 0", () => QRCodeDecoder.TryDecode(modules, 0, out _, out _), "size");
        yield return ("qr matrix -3", () => QRCodeDecoder.TryDecode(modules, -3, out _, out _), "size");
        yield return ("qr matrix span", () => QRCodeDecoder.TryDecode(modules, -3, new char[10], out _, out _), "size");
        yield return ("micro matrix 0", () => MicroQRCodeDecoder.TryDecode(modules, 0, out _, out _), "size");
        yield return ("micro matrix span", () => MicroQRCodeDecoder.TryDecode(modules, -3, new char[10], out _, out _), "size");
        yield return ("rmqr matrix width", () => RmQRCodeDecoder.TryDecode(modules, -1, 7, out _, out _), "width");
        yield return ("rmqr matrix height", () => RmQRCodeDecoder.TryDecode(modules, 43, 0, out _, out _), "height");
        yield return ("rmqr matrix span", () => RmQRCodeDecoder.TryDecode(modules, 0, 7, new char[10], out _, out _), "width");
        yield return ("qr image", () => QRCodeDecoder.TryDecodeImage(modules, 0, 10, out _, out _), "width");
        yield return ("rmqr image", () => RmQRCodeDecoder.TryDecodeImage(modules, 10, -1, out _, out _), "height");
    }

    [Test]
    [MethodDataSource(nameof(NonPositiveSizeCases))]
    public async Task NonPositiveSize_IsOutOfRange_ForMatricesAsForImages(string name, Action decode, string paramName)
    {
        var error = Catch(decode);

        await Assert.That(error?.GetType()).IsEqualTo(typeof(ArgumentOutOfRangeException)).Because(name);
        await Assert.That(((ArgumentException)error!).ParamName).IsEqualTo(paramName).Because(name);
        await Assert.That(error.Message).DoesNotContain("buffer too small").Because(name);
    }

    /// <summary>A positive size over a short buffer is still the buffer's fault.</summary>
    [Test]
    public async Task PositiveSizeOverAShortBuffer_NamesTheBuffer()
    {
        var error = Catch(() => QRCodeDecoder.TryDecode(new byte[100], 11, out _, out _));
        await Assert.That(error?.GetType()).IsEqualTo(typeof(ArgumentException));
        await Assert.That(((ArgumentException)error!).ParamName).IsEqualTo("modules");

        error = Catch(() => RmQRCodeDecoder.TryDecode(new byte[100], 43, 7, out _, out _));
        await Assert.That(error?.GetType()).IsEqualTo(typeof(ArgumentException));
        await Assert.That(((ArgumentException)error!).ParamName).IsEqualTo("modules");
    }

    /// <summary>The three decoders word an undefined version the same way.</summary>
    [Test]
    public async Task GetMaxDecodedLength_UndefinedVersion_SameWording()
    {
        var qr = (ArgumentOutOfRangeException)Catch(() => QRCodeDecoder.GetMaxDecodedLength(41))!;
        var micro = (ArgumentOutOfRangeException)Catch(() => MicroQRCodeDecoder.GetMaxDecodedLength((MicroQRVersion)5))!;
        var rmqr = (ArgumentOutOfRangeException)Catch(() => RmQRCodeDecoder.GetMaxDecodedLength((RmQRVersion)33))!;

        await Assert.That(qr.Message).StartsWith("Version must be 1 to 40, but was 41.");
        await Assert.That(micro.Message).StartsWith("Version must be M1 to M4, but was 5.");
        await Assert.That(rmqr.Message).StartsWith("Version must be a defined RmQRVersion, but was 33.");
    }

    private static Exception? Catch(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }
}
