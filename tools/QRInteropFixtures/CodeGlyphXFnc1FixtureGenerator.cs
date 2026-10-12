using CodeGlyphX;

namespace QRInteropFixtures;

/// <summary>
/// FNC1 fixture generator backed by the pinned CodeGlyphX package, run in-process. Standard QR: first
/// position through <c>EncodeGs1</c> (the element string with U+001D separators), second position through
/// <c>QrEncodingOptions</c> with the 8-bit application indicator. rMQR: first position through
/// <c>RmQrCodeEncoder.EncodeGs1</c>, whose GS1 validator takes bracketed syntax and refuses a raw
/// separator after a fixed-length AI. It writes FNC1 before an ECI header. Its <c>%</c> handling is
/// described on <see cref="Fnc1Corpus.CodeGlyphXCases"/>.
/// </summary>
public sealed class CodeGlyphXFnc1FixtureGenerator : IFnc1FixtureGenerator
{
    public string Name => "codeglyphx";

    public bool IsAvailable => true;

    public GeneratedFixture Generate(Fnc1CaseDefinition caseDefinition)
    {
        var ecc = ToEcc(caseDefinition.ErrorCorrectionLevel);
        if (caseDefinition.IsRmQR)
        {
            if (caseDefinition.Position != Fnc1Position.First)
                throw new NotSupportedException($"CodeGlyphX writes rMQR FNC1 in first position only (case {caseDefinition.Id}).");
            if (caseDefinition.Utf8)
                throw new NotSupportedException($"CodeGlyphX's rMQR GS1 encoder takes no ECI (case {caseDefinition.Id}).");

            var code = RmQrCodeEncoder.EncodeGs1(caseDefinition.Input, ecc, minimumVersion: caseDefinition.RmQRVersion, maximumVersion: caseDefinition.RmQRVersion);
            if (code.Version != caseDefinition.RmQRVersion)
                throw new InvalidOperationException($"CodeGlyphX produced {code.VersionName} for case {caseDefinition.Id}, expected version {caseDefinition.RmQRVersion}.");
            var rmqr = Fnc1Writers.FromCodeGlyphX(code);
            // rMQR has one mask, which the gate reads back as Standard QR pattern 4.
            return new GeneratedFixture(Fnc1Manifests.Build(Name, Fnc1Writers.CodeGlyphXVersion, caseDefinition, rmqr, code.Version, code.VersionName, mode: "Unknown", maskPattern: -1), rmqr.Modules);
        }

        var options = new QrEncodingOptions
        {
            ErrorCorrectionLevel = ecc,
            TextEncoding = caseDefinition.Utf8 ? QrTextEncoding.Utf8 : QrTextEncoding.Latin1,
            EciMode = caseDefinition.Utf8 ? QrEciMode.Always : QrEciMode.Auto,
            Fnc1Mode = caseDefinition.Position == Fnc1Position.Second ? QrFnc1Mode.SecondPosition : QrFnc1Mode.None,
            Fnc1ApplicationIndicator = caseDefinition.Position == Fnc1Position.Second ? caseDefinition.ApplicationIndicator : null,
        };
        var qr = caseDefinition.Position == Fnc1Position.First
            ? QrCodeEncoder.EncodeGs1(caseDefinition.Input, options)
            : QrCodeEncoder.EncodeText(caseDefinition.Input, options);
        var matrix = Fnc1Writers.FromCodeGlyphX(qr);
        return new GeneratedFixture(Fnc1Manifests.Build(Name, Fnc1Writers.CodeGlyphXVersion, caseDefinition, matrix, qr.Version, null, mode: "Unknown", maskPattern: qr.Mask), matrix.Modules);
    }

    private static QrErrorCorrectionLevel ToEcc(string ecc) => ecc switch
    {
        "L" => QrErrorCorrectionLevel.L,
        "M" => QrErrorCorrectionLevel.M,
        "Q" => QrErrorCorrectionLevel.Q,
        "H" => QrErrorCorrectionLevel.H,
        _ => throw new ArgumentOutOfRangeException(nameof(ecc), $"Unknown ECC level '{ecc}'."),
    };
}
