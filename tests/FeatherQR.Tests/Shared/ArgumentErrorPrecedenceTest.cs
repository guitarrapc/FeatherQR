namespace FeatherQR.Tests;

/// <summary>
/// When an argument is invalid <em>and</em> the content does not fit, the argument error is
/// the one reported, and it is the same one the parameter list overloads report.
/// </summary>
/// <remarks>
/// specs/rmqr-encoder.md states that argument errors are raised by the non-throwing and
/// options paths "with the same type, message and precedence" as the throwing parameter
/// list overloads. The options <c>Create</c> overloads originally broke that: they resolve
/// the version as an argument expression, so the fit ran before the overload they forward
/// to could validate anything, and a negative quiet zone was reported as "content does not
/// fit". The sizing overloads were already correct, which made the options surface
/// inconsistent with itself.
/// </remarks>
public class ArgumentErrorPrecedenceTest
{
    private const string TooLongForVersion1 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string ByteContent = "hello";   // needs M3/M4, so M1 rules it out on mode

    [Test]
    public async Task StandardQr_NegativeQuietZoneWins_OverAContentThatDoesNotFit()
    {
        var options = new QRCodeGeneratorOptions { Version = QRCodeVersionRange.Exactly(1), QuietZoneSize = -1 };

        await AssertQuietZone(() => QRCodeGenerator.CreateQrCode(TooLongForVersion1, ECCLevel.M, options));
        await AssertQuietZone(() => QRCodeGenerator.CreateQrCode(TooLongForVersion1.AsSpan(), ECCLevel.M, options));
        await AssertQuietZone(() => QRCodeGenerator.CreateQrCode(TooLongForVersion1, ECCLevel.M, new byte[10_000], options));
        await AssertQuietZone(() => QRCodeGenerator.CreateQrCode(TooLongForVersion1.AsSpan(), ECCLevel.M, new byte[10_000], options));
        await AssertQuietZone(() => QRCodeGenerator.TryGetRequiredBufferSize(TooLongForVersion1.AsSpan(), ECCLevel.M, out _, options));
    }

    [Test]
    public async Task MicroQr_NegativeQuietZoneWins_OverAVersionThatCannotCarryTheMode()
    {
        // M1 offers neither Byte mode nor ECC L, so both a "does not fit" and an ECC
        // contradiction are available; the quiet zone is still the first thing reported.
        var options = new MicroQRCodeGeneratorOptions { Version = MicroQRVersionRange.Exactly(MicroQRVersion.M1), QuietZoneSize = -1 };

        await AssertQuietZone(() => MicroQRCodeGenerator.CreateMicroQRCode(ByteContent, MicroQREccLevel.L, options));
        await AssertQuietZone(() => MicroQRCodeGenerator.CreateMicroQRCode(ByteContent.AsSpan(), MicroQREccLevel.L, options));
        await AssertQuietZone(() => MicroQRCodeGenerator.CreateMicroQRCode(ByteContent.AsSpan(), MicroQREccLevel.L, new byte[10_000], options));
        await AssertQuietZone(() => MicroQRCodeGenerator.TryGetRequiredBufferSize(ByteContent.AsSpan(), MicroQREccLevel.L, out _, options));
    }

    [Test]
    public async Task RmQr_NegativeQuietZoneWins_OverAContentThatDoesNotFit()
    {
        var options = new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43, QuietZoneSize = -1 };
        var tooLong = new string('A', 500);

        await AssertQuietZone(() => RmQRCodeGenerator.CreateRmQRCode(tooLong, RmQREccLevel.M, options));
        await AssertQuietZone(() => RmQRCodeGenerator.CreateRmQRCode(tooLong.AsSpan(), RmQREccLevel.M, options));
        await AssertQuietZone(() => RmQRCodeGenerator.CreateRmQRCode(tooLong.AsSpan(), RmQREccLevel.M, new byte[10_000], options));
        await AssertQuietZone(() => RmQRCodeGenerator.TryGetRequiredBufferSize(tooLong.AsSpan(), RmQREccLevel.M, out _, options));
    }

    [Test]
    public async Task OptionsAndParameterList_ReportTheSameArgumentError()
    {
        // The contract is not merely "an argument error" but the same one, so a caller
        // moving between the two spellings debugs the same problem.
        var viaParameters = Assert.Throws<ArgumentOutOfRangeException>(
            () => QRCodeGenerator.CreateQrCode(TooLongForVersion1, ECCLevel.M, new QRCodeGeneratorOptions { Version = 1, QuietZoneSize = -1 }));
        var viaOptions = Assert.Throws<ArgumentOutOfRangeException>(
            () => QRCodeGenerator.CreateQrCode(TooLongForVersion1, ECCLevel.M, new QRCodeGeneratorOptions { Version = 1, QuietZoneSize = -1 }));

        await Assert.That(viaOptions!.ParamName).IsEqualTo(viaParameters!.ParamName);
        await Assert.That(viaOptions.Message).IsEqualTo(viaParameters.Message);
    }

    private static async Task AssertQuietZone(Action call)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(call);
        await Assert.That(error!.ParamName).IsEqualTo("quietZoneSize");
    }
}
