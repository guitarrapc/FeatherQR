using System.Reflection;
using System.Text;
using ZXing;
using ZXing.QrCode.Internal;
using ZXingEncoder = ZXing.QrCode.Internal.Encoder;

namespace QRInteropFixtures;

/// <summary>
/// Fixture generator backed by ZXing.Net's Standard QR encoder
/// (<see cref="ZXing.QrCode.Internal.Encoder"/>), an independent implementation
/// lineage from FeatherQR. Runs in-process from the pinned NuGet package, so it
/// needs no external toolchain and is always available.
/// </summary>
public sealed class ZXingNetFixtureGenerator : IFixtureGenerator
{
    public string Name => "zxing-net";

    public bool IsAvailable => true;

    public GeneratedFixture Generate(FixtureCaseDefinition caseDefinition)
    {
        // Kanji mode: ZXing's chooseMode returns KANJI only when the requested charset
        // is Shift_JIS and every character is double-byte, and .NET needs the CodePages
        // provider before it can resolve that charset at all.
        var kanji = caseDefinition.Mode == KanjiPayload.ModeName;
        if (kanji)
            _ = KanjiPayload.ShiftJis;

        var hints = (kanji, caseDefinition.Utf8) switch
        {
            (true, _) => new Dictionary<EncodeHintType, object> { [EncodeHintType.CHARACTER_SET] = "Shift_JIS" },
            (_, true) => new Dictionary<EncodeHintType, object> { [EncodeHintType.CHARACTER_SET] = "UTF-8" },
            _ => null,
        };

        var qr = ZXingEncoder.encode(caseDefinition.PayloadText, ToZXingEccLevel(caseDefinition.ErrorCorrectionLevel), hints);
        if (kanji && qr.Mode != Mode.KANJI)
            throw new InvalidOperationException($"ZXing encoded case {caseDefinition.Id} in {qr.Mode} mode, not Kanji; the payload must be entirely JIS X 0208 double-byte characters.");
        var matrix = qr.Matrix;
        var size = matrix.Width;
        if (matrix.Height != size)
            throw new InvalidOperationException($"ZXing produced a non-square matrix ({matrix.Width}x{matrix.Height}) for case {caseDefinition.Id}.");

        var modules = new byte[size * size];
        var rows = matrix.Array;
        for (var row = 0; row < size; row++)
        {
            for (var col = 0; col < size; col++)
            {
                modules[row * size + col] = rows[row][col] != 0 ? (byte)1 : (byte)0;
            }
        }

        var manifest = new FixtureManifest
        {
            Id = caseDefinition.Id,
            Generator = Name,
            GeneratorVersion = GetZXingVersion(),
            SymbolType = "StandardQR",
            Version = qr.Version.VersionNumber,
            Width = size,
            Height = size,
            ErrorCorrectionLevel = caseDefinition.ErrorCorrectionLevel,
            Mode = ToModeName(qr.Mode),
            MaskPattern = qr.MaskPattern,
            PayloadText = caseDefinition.PayloadText,
            PayloadUtf8Hex = Convert.ToHexString(Encoding.UTF8.GetBytes(caseDefinition.PayloadText)),
            EciCharset = caseDefinition.Utf8 ? "UTF-8" : null, // Kanji mode carries no ECI: the mode itself declares JIS X 0208
            QuietZoneModules = FixtureWriter.QuietZoneModules,
            PixelsPerModule = FixtureWriter.PixelsPerModule,
        };

        return new GeneratedFixture(manifest, modules);
    }

    internal static ErrorCorrectionLevel ToZXingEccLevel(string ecc) => ecc switch
    {
        "L" => ErrorCorrectionLevel.L,
        "M" => ErrorCorrectionLevel.M,
        "Q" => ErrorCorrectionLevel.Q,
        "H" => ErrorCorrectionLevel.H,
        _ => throw new ArgumentOutOfRangeException(nameof(ecc), $"Unknown ECC level '{ecc}'."),
    };

    internal static string ToModeName(Mode mode)
    {
        if (mode == Mode.NUMERIC) return "Numeric";
        if (mode == Mode.ALPHANUMERIC) return "Alphanumeric";
        if (mode == Mode.BYTE) return "Byte";
        if (mode == Mode.KANJI) return "Kanji";
        throw new NotSupportedException($"Unexpected ZXing mode '{mode}' in the Standard QR corpus.");
    }

    private static string GetZXingVersion()
    {
        var assembly = typeof(ZXingEncoder).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }
}
