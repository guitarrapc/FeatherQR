using System.Reflection;
using Net.Codecrete.QrCodeGenerator;

namespace QRInteropFixtures;

/// <summary>
/// Structured Append lineage backed by the pinned Net.Codecrete.QrCodeGenerator package:
/// a balancing encoder that picks the symbol count and a shared version itself. It does
/// not expose per-symbol text or the parity byte, so the sanity gate sources both from
/// the reader. Runs in-process; always available.
/// </summary>
public sealed class QrCodeGeneratorStructuredAppendFixtureGenerator : IStructuredAppendFixtureGenerator
{
    public string Name => "qrcodegenerator";

    public bool IsAvailable => true;

    public GeneratedFixture[] Generate(StructuredAppendCaseDefinition caseDefinition)
    {
        var codes = QrCode.EncodeTextInMultipleBalancedCodes(caseDefinition.PayloadText, ToEcc(caseDefinition.ErrorCorrectionLevel), caseDefinition.MinVersion, caseDefinition.MaxVersion);
        if (codes.Count < 2)
            throw new InvalidOperationException($"{Name}: case {caseDefinition.Id} fit in one symbol; a Structured Append case needs at least two.");

        var fixtures = new GeneratedFixture[codes.Count];
        for (var i = 0; i < codes.Count; i++)
        {
            var code = codes[i];
            var size = code.Size;
            var modules = new byte[size * size];
            for (var row = 0; row < size; row++)
            {
                for (var col = 0; col < size; col++)
                    modules[row * size + col] = code.GetModule(col, row) ? (byte)1 : (byte)0;
            }

            var manifest = new FixtureManifest
            {
                Id = $"{caseDefinition.Id}-{i}of{codes.Count}",
                Generator = Name,
                GeneratorVersion = GetVersion(),
                SymbolType = "StandardQR",
                Version = code.Version,
                Width = size,
                Height = size,
                // Boosted per symbol by this encoder; record what each symbol actually carries.
                ErrorCorrectionLevel = FromEcc(code.ErrorCorrectionLevel),
                Mode = "Unknown", // filled by the gate from the reader when it can tell, else left
                MaskPattern = code.Mask,
                PayloadText = "", // per-symbol text is reader-sourced
                PayloadUtf8Hex = "",
                EciCharset = null,
                QuietZoneModules = FixtureWriter.QuietZoneModules,
                PixelsPerModule = FixtureWriter.PixelsPerModule,
                StructuredAppend = new StructuredAppendManifest { SetId = caseDefinition.Id, Index = i, Count = codes.Count, Parity = -1 },
            };
            fixtures[i] = new GeneratedFixture(manifest, modules);
        }

        return fixtures;
    }

    private static QrCode.Ecc ToEcc(string ecc) => ecc switch
    {
        "L" => QrCode.Ecc.Low,
        "M" => QrCode.Ecc.Medium,
        "Q" => QrCode.Ecc.Quartile,
        "H" => QrCode.Ecc.High,
        _ => throw new ArgumentOutOfRangeException(nameof(ecc), $"Unknown ECC level '{ecc}'."),
    };

    private static string FromEcc(QrCode.Ecc ecc)
    {
        if (ecc == QrCode.Ecc.Low) return "L";
        if (ecc == QrCode.Ecc.Medium) return "M";
        if (ecc == QrCode.Ecc.Quartile) return "Q";
        if (ecc == QrCode.Ecc.High) return "H";
        throw new ArgumentOutOfRangeException(nameof(ecc));
    }

    private static string GetVersion()
    {
        var assembly = typeof(QrCode).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }
}
