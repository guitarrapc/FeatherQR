namespace FeatherQR.Tests;

/// <summary>
/// When an argument is invalid <em>and</em> the content does not fit, the argument error is
/// the one reported, and every entry point reports the same one.
/// </summary>
/// <remarks>
/// specs/rmqr-encoder.md states that argument errors are raised "with the same type, message
/// and precedence" across the surface. The options <c>Create</c> overloads originally broke
/// that: they resolve the version as an argument expression, so the fit ran before the
/// overload they forwarded to could validate anything, and a negative quiet zone was reported
/// as "content does not fit". The sizing overloads were already correct, which made the
/// options surface inconsistent with itself. The parameter list overloads this was once
/// compared against were removed in 2.0.0; the agreement is now between <c>Create</c>, the
/// destination overload and <c>TryGetRequiredBufferSize</c>.
/// </remarks>
public class ArgumentErrorPrecedenceTest
{
    private const string TooLongForVersion1 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string ByteContent = "hello";   // needs M3/M4, so M1 rules it out on mode

    [Test]
    public async Task StandardQr_NegativeQuietZoneWins_OverAContentThatDoesNotFit()
    {
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(1), QuietZoneSize = -1 };

        await AssertQuietZone(() => QRCodeGenerator.Create(TooLongForVersion1, QREccLevel.M, options));
        await AssertQuietZone(() => QRCodeGenerator.Create(TooLongForVersion1.AsSpan(), QREccLevel.M, options));
        await AssertQuietZone(() => QRCodeGenerator.Create(TooLongForVersion1, QREccLevel.M, new byte[10_000], options));
        await AssertQuietZone(() => QRCodeGenerator.Create(TooLongForVersion1.AsSpan(), QREccLevel.M, new byte[10_000], options));
        await AssertQuietZone(() => QRCodeGenerator.TryGetRequiredBufferSize(TooLongForVersion1.AsSpan(), QREccLevel.M, out _, options));
    }

    [Test]
    public async Task MicroQr_NegativeQuietZoneWins_OverAVersionThatCannotCarryTheMode()
    {
        // M1 offers neither Byte mode nor ECC L, so both a "does not fit" and an ECC
        // contradiction are available; the quiet zone is still the first thing reported.
        var options = new MicroQRCodeGeneratorOptions { Version = MicroQRVersionRange.Exactly(MicroQRVersion.M1), QuietZoneSize = -1 };

        await AssertQuietZone(() => MicroQRCodeGenerator.Create(ByteContent, MicroQREccLevel.L, options));
        await AssertQuietZone(() => MicroQRCodeGenerator.Create(ByteContent.AsSpan(), MicroQREccLevel.L, options));
        await AssertQuietZone(() => MicroQRCodeGenerator.Create(ByteContent.AsSpan(), MicroQREccLevel.L, new byte[10_000], options));
        await AssertQuietZone(() => MicroQRCodeGenerator.TryGetRequiredBufferSize(ByteContent.AsSpan(), MicroQREccLevel.L, out _, options));
    }

    [Test]
    public async Task RmQr_NegativeQuietZoneWins_OverAContentThatDoesNotFit()
    {
        var options = new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43, QuietZoneSize = -1 };
        var tooLong = new string('A', 500);

        await AssertQuietZone(() => RmQRCodeGenerator.Create(tooLong, RmQREccLevel.M, options));
        await AssertQuietZone(() => RmQRCodeGenerator.Create(tooLong.AsSpan(), RmQREccLevel.M, options));
        await AssertQuietZone(() => RmQRCodeGenerator.Create(tooLong.AsSpan(), RmQREccLevel.M, new byte[10_000], options));
        await AssertQuietZone(() => RmQRCodeGenerator.TryGetRequiredBufferSize(tooLong.AsSpan(), RmQREccLevel.M, out _, options));
    }

    [Test]
    public async Task EveryEntryPoint_ReportsTheSameArgumentError()
    {
        // The contract is not merely "an argument error" but the same one, so a caller
        // moving between the entry points debugs the same problem. Until 2.0.0 the
        // comparison was against the parameter list overloads; they are gone, and the
        // surviving spellings are what has to agree now.
        var options = new QRCodeGeneratorOptions { Version = 1, QuietZoneSize = -1 };

        var viaCreate = Assert.Throws<ArgumentOutOfRangeException>(
            () => QRCodeGenerator.Create(TooLongForVersion1, QREccLevel.M, options));
        var viaDestination = Assert.Throws<ArgumentOutOfRangeException>(
            () => QRCodeGenerator.Create(TooLongForVersion1, QREccLevel.M, new byte[10_000], options));
        var viaSizing = Assert.Throws<ArgumentOutOfRangeException>(
            () => QRCodeGenerator.TryGetRequiredBufferSize(TooLongForVersion1.AsSpan(), QREccLevel.M, out _, options));

        await Assert.That(viaDestination!.ParamName).IsEqualTo(viaCreate!.ParamName);
        await Assert.That(viaDestination.Message).IsEqualTo(viaCreate.Message);
        await Assert.That(viaSizing!.ParamName).IsEqualTo(viaCreate.ParamName);
        await Assert.That(viaSizing.Message).IsEqualTo(viaCreate.Message);
    }

    private static async Task AssertQuietZone(Action call)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(call);
        await Assert.That(error!.ParamName).IsEqualTo("quietZoneSize");
    }
}
