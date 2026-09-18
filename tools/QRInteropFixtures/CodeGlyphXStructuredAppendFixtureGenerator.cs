using System.Reflection;
using System.Text;
using CodeGlyphX;

namespace QRInteropFixtures;

/// <summary>
/// Structured Append lineage backed by the pinned CodeGlyphX package: an explicit-parts
/// encoder, handed the case's text split into <c>Parts</c> equal character runs, each
/// symbol versioned on its own within the case's bounds. Per-symbol text is therefore
/// known here; the parity byte is still reader-sourced by the gate. Runs in-process;
/// always available.
/// </summary>
public sealed class CodeGlyphXStructuredAppendFixtureGenerator : IStructuredAppendFixtureGenerator
{
    public string Name => "codeglyphx";

    public bool IsAvailable => true;

    public GeneratedFixture[] Generate(StructuredAppendCaseDefinition caseDefinition)
    {
        var parts = SplitEvenly(caseDefinition.PayloadText, caseDefinition.Parts);
        var options = new QrEncodingOptions
        {
            ErrorCorrectionLevel = ToEcc(caseDefinition.ErrorCorrectionLevel),
            MinVersion = caseDefinition.MinVersion,
            MaxVersion = caseDefinition.MaxVersion,
        };
        var codes = QrCodeEncoder.EncodeStructuredAppend(parts, options);

        var fixtures = new GeneratedFixture[codes.Length];
        for (var i = 0; i < codes.Length; i++)
        {
            var code = codes[i];
            var size = code.Size;
            var modules = new byte[size * size];
            for (var row = 0; row < size; row++)
            {
                for (var col = 0; col < size; col++)
                    modules[row * size + col] = code.Modules[col, row] ? (byte)1 : (byte)0;
            }

            var manifest = new FixtureManifest
            {
                Id = $"{caseDefinition.Id}-{i}of{codes.Length}",
                Generator = Name,
                GeneratorVersion = GetVersion(),
                SymbolType = "StandardQR",
                Version = code.Version,
                Width = size,
                Height = size,
                ErrorCorrectionLevel = caseDefinition.ErrorCorrectionLevel,
                Mode = "Unknown",
                MaskPattern = code.Mask,
                PayloadText = parts[i],
                PayloadUtf8Hex = Convert.ToHexString(Encoding.UTF8.GetBytes(parts[i])),
                EciCharset = null, // reader-sourced by the gate
                QuietZoneModules = FixtureWriter.QuietZoneModules,
                PixelsPerModule = FixtureWriter.PixelsPerModule,
                StructuredAppend = new StructuredAppendManifest { SetId = caseDefinition.Id, Index = i, Count = codes.Length, Parity = -1 },
            };
            fixtures[i] = new GeneratedFixture(manifest, modules);
        }

        return fixtures;
    }

    /// <summary>Equal character runs, never splitting a surrogate pair.</summary>
    private static string[] SplitEvenly(string text, int parts)
    {
        var result = new string[parts];
        var start = 0;
        for (var i = 0; i < parts; i++)
        {
            var end = i == parts - 1 ? text.Length : start + (text.Length - start) / (parts - i);
            if (end < text.Length && char.IsLowSurrogate(text[end]))
                end--;
            result[i] = text.Substring(start, end - start);
            start = end;
        }
        return result;
    }

    private static QrErrorCorrectionLevel ToEcc(string ecc) => ecc switch
    {
        "L" => QrErrorCorrectionLevel.L,
        "M" => QrErrorCorrectionLevel.M,
        "Q" => QrErrorCorrectionLevel.Q,
        "H" => QrErrorCorrectionLevel.H,
        _ => throw new ArgumentOutOfRangeException(nameof(ecc), $"Unknown ECC level '{ecc}'."),
    };

    private static string GetVersion()
    {
        var assembly = typeof(QrCodeEncoder).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }
}
