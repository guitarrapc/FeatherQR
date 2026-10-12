using System.Text;
using ZXingCpp;

namespace QRInteropFixtures;

/// <summary>
/// Sanity gate for an FNC1 fixture: its render is read with the pinned zxing-cpp reader in
/// <c>TextMode.Plain</c>, and the fixture is written only if the reader reports the mode the case
/// claims (the symbology identifier <c>]Q3</c> or <c>]Q5</c>, GS1 content in first position, the
/// application indicator before the text in second position, an ECI header exactly where the case
/// asked for one), the version, ECC and mask of the manifest, and the element string the case
/// expects. The reader supplies the identifier and, for libzint, the mask. Where the writer's input
/// is not the text the symbol means, the manifest records the input beside the reader's text.
/// </summary>
public static class Fnc1SanityGate
{
    public static GeneratedFixture Verify(GeneratedFixture fixture, Fnc1CaseDefinition caseDefinition)
    {
        var manifest = fixture.Manifest;
        var name = $"{manifest.Generator}/{manifest.Id}";
        var matrix = new ModuleMatrix(fixture.Modules, manifest.Width, manifest.Height);
        var format = caseDefinition.IsRmQR ? BarcodeFormat.RMQRCode : BarcodeFormat.QRCode;

        var results = Fnc1Writers.ReadCpp(matrix, format, manifest.QuietZoneModules, manifest.PixelsPerModule);
        if (results.Length != 1)
            throw new InvalidOperationException($"sanity gate: zxing-cpp found {results.Length} symbols in fixture {name}.");
        var result = results[0];
        if (!result.IsValid)
            throw new InvalidOperationException($"sanity gate: zxing-cpp rejects fixture {name}: {result.ErrorMsg}.");

        var expectedIdentifier = caseDefinition.Position == Fnc1Position.First ? "]Q3" : "]Q5";
        if (result.SymbologyIdentifier != expectedIdentifier)
            throw new InvalidOperationException($"sanity gate: fixture {name} reads with symbology identifier '{result.SymbologyIdentifier}', the case claims {caseDefinition.Position} position ({expectedIdentifier}).");
        if (caseDefinition.Position == Fnc1Position.First && result.ContentType != ContentType.GS1)
            throw new InvalidOperationException($"sanity gate: fixture {name} reads as {result.ContentType} content, not GS1.");
        if (result.HasECI != caseDefinition.Utf8)
            throw new InvalidOperationException($"sanity gate: fixture {name} {(result.HasECI ? "carries" : "carries no")} ECI header, the case {(caseDefinition.Utf8 ? "asked for one" : "asked for none")}.");
        if (caseDefinition.Utf8)
        {
            // The reader's ECI-protocol bytes name each assignment as \000026. HasECI alone would pass a writer that
            // honoured a UTF-8 request with another assignment.
            var eciProtocol = Encoding.Latin1.GetString(result.BytesECI);
            if (!eciProtocol.Contains("\\000026", StringComparison.Ordinal))
                throw new InvalidOperationException($"sanity gate: fixture {name} carries an ECI header that is not assignment 26 (ECI protocol bytes: {eciProtocol}).");
        }

        // Plain text: the element string with GS as 0x1D. In second position the reader puts the
        // application indicator first, as the standard transmits it, and the fixture's text is the rest.
        var text = result.Text;
        if (caseDefinition.Position == Fnc1Position.Second)
        {
            var indicator = Fnc1Writers.FormatIndicator(caseDefinition.ApplicationIndicator);
            if (!text.StartsWith(indicator, StringComparison.Ordinal))
                throw new InvalidOperationException($"sanity gate: fixture {name} reads as \"{Fnc1Writers.Escape(text)}\", which does not start with the application indicator {indicator} ({caseDefinition.ApplicationIndicator}).");
            text = text.Substring(indicator.Length);
        }
        if (text != caseDefinition.ExpectedText)
            throw new InvalidOperationException($"sanity gate: fixture {name} reads as \"{Fnc1Writers.Escape(text)}\", the case expects \"{Fnc1Writers.Escape(caseDefinition.ExpectedText)}\" (writer input \"{Fnc1Writers.Escape(caseDefinition.Input)}\").");

        var version = result.Extra("Version");
        if (caseDefinition.IsRmQR ? version != manifest.VersionName : StructuredAppendSanityGate.ParseVersion(version) != manifest.Version)
            throw new InvalidOperationException($"sanity gate: fixture {name} reads as version {version}, manifest says {manifest.VersionName ?? manifest.Version.ToString()}.");

        var eccLevel = result.Extra("EcLevel");
        if (eccLevel != manifest.ErrorCorrectionLevel)
            throw new InvalidOperationException($"sanity gate: fixture {name} reads as ECC {eccLevel}, manifest says {manifest.ErrorCorrectionLevel}.");

        var mask = int.TryParse(result.Extra("DataMask"), out var readMask) ? readMask : -1;
        if (manifest.MaskPattern >= 0 && mask >= 0 && mask != manifest.MaskPattern)
            throw new InvalidOperationException($"sanity gate: fixture {name} reads as mask {mask}, manifest says {manifest.MaskPattern}.");

        return fixture with
        {
            Manifest = manifest with
            {
                PayloadText = text,
                PayloadUtf8Hex = Convert.ToHexString(Encoding.UTF8.GetBytes(text)),
                MaskPattern = manifest.MaskPattern >= 0 ? manifest.MaskPattern : mask,
                Fnc1 = manifest.Fnc1! with
                {
                    SymbologyIdentifier = result.SymbologyIdentifier,
                    WriterInput = caseDefinition.Input == text ? null : caseDefinition.Input,
                },
            },
        };
    }
}
