using ZXing;
using ZXingEncoder = ZXing.QrCode.Internal.Encoder;

namespace QRInteropFixtures;

/// <summary>
/// FNC1 fixture generator backed by ZXing.Net's Standard QR encoder with the <c>GS1_FORMAT</c> hint:
/// FNC1 in first position, Standard QR only, the ECI header (when <c>CHARACTER_SET</c> asks for one)
/// before FNC1 as the standard orders them. Runs in-process from the pinned package. It does not
/// double a literal <c>%</c> (0.16.11), which <see cref="Fnc1Corpus.ZXingNetCases"/> turns into a
/// fixture of a lone <c>%</c> meaning the separator.
/// </summary>
public sealed class ZXingNetFnc1FixtureGenerator : IFnc1FixtureGenerator
{
    public string Name => "zxing-net";

    public bool IsAvailable => true;

    public GeneratedFixture Generate(Fnc1CaseDefinition caseDefinition)
    {
        if (caseDefinition.IsRmQR || caseDefinition.Position != Fnc1Position.First)
            throw new NotSupportedException($"ZXing.Net writes FNC1 in first position in Standard QR only (case {caseDefinition.Id}).");

        var hints = new Dictionary<EncodeHintType, object> { [EncodeHintType.GS1_FORMAT] = true };
        if (caseDefinition.Utf8)
            hints[EncodeHintType.CHARACTER_SET] = "UTF-8";

        var qr = ZXingEncoder.encode(caseDefinition.Input, ZXingNetFixtureGenerator.ToZXingEccLevel(caseDefinition.ErrorCorrectionLevel), hints);
        var matrix = Fnc1Writers.FromZXingNet(qr);
        if (matrix.Width != matrix.Height)
            throw new InvalidOperationException($"ZXing produced a non-square matrix ({matrix.Width}x{matrix.Height}) for case {caseDefinition.Id}.");

        return new GeneratedFixture(Fnc1Manifests.Build(Name, Fnc1Writers.ZXingNetVersion, caseDefinition, matrix, qr.Version.VersionNumber, null, ZXingNetFixtureGenerator.ToModeName(qr.Mode), qr.MaskPattern), matrix.Modules);
    }
}
