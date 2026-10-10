using System.Text;
using ZXingCpp;

namespace QRInteropFixtures;

/// <summary>
/// rMQR fixture generator backed by libzint (zxing-cpp's writer, compiled into the
/// pinned ZXingCpp native binary), a zint-lineage ENCODER independent of both
/// FeatherQR and the zxing-cpp reader used as the sanity gate. libzint's
/// <c>version=N</c> is the ISO/IEC 23941 version index + 1 (verified by
/// <c>probe-rmqr</c>: 1 = R7x43 … 32 = R17x139).
/// </summary>
public sealed class ZintRmQRFixtureGenerator : IRmQRFixtureGenerator
{
    public string Name => "zint-libzint";

    public bool IsAvailable
    {
        get
        {
            try
            {
                using var barcode = ZXingCppCalls.Create(new BarcodeCreator(BarcodeFormat.RMQRCode), "0");
                return barcode.IsValid;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// libzint (through the ZXingCpp wrapper) rejects non-ASCII input, and it never
    /// selects Kanji mode: given Shift_JIS bytes it emits Byte mode with ECI 20.
    /// </summary>
    public bool SupportsCase(RmQRFixtureCaseDefinition caseDefinition) => !caseDefinition.Utf8 && caseDefinition.Mode != KanjiPayload.ModeName;

    public GeneratedFixture Generate(RmQRFixtureCaseDefinition caseDefinition)
    {
        var entry = RmQRVersionTable.Find(caseDefinition.Height, caseDefinition.Width);
        using var barcode = ZXingCppCalls.Create(new BarcodeCreator(BarcodeFormat.RMQRCode) { Options = $"version={entry.Number},ecLevel={caseDefinition.ErrorCorrectionLevel}" }, caseDefinition.PayloadText);

        // Scale 1 without quiet zones yields the module-exact symbol.
        using var image = ZXingCppCalls.ToImage(barcode, new WriterOptions { Scale = 1, AddQuietZones = false });
        if (image.Width != caseDefinition.Width || image.Height != caseDefinition.Height)
            throw new InvalidOperationException($"libzint produced a {image.Height}x{image.Width} symbol for case {caseDefinition.Id}, expected {caseDefinition.VersionName}.");

        var pixels = image.ToArray();
        var modules = new byte[caseDefinition.Width * caseDefinition.Height];
        for (var i = 0; i < modules.Length; i++)
        {
            modules[i] = pixels[i] < 128 ? (byte)1 : (byte)0; // luminance: dark < 128 -> module = 1
        }

        var manifest = new FixtureManifest
        {
            Id = caseDefinition.Id,
            Generator = Name,
            GeneratorVersion = $"ZXingCpp {typeof(BarcodeCreator).Assembly.GetName().Version?.ToString(3) ?? "unknown"} bundled libzint",
            SymbolType = "rMQR",
            Version = entry.Number,
            VersionName = entry.Name,
            Width = caseDefinition.Width,
            Height = caseDefinition.Height,
            ErrorCorrectionLevel = caseDefinition.ErrorCorrectionLevel,
            Mode = caseDefinition.Mode,
            MaskPattern = -1, // filled in by the sanity gate from the zxing-cpp reader
            PayloadText = caseDefinition.PayloadText,
            PayloadUtf8Hex = Convert.ToHexString(Encoding.UTF8.GetBytes(caseDefinition.PayloadText)),
            EciCharset = null, // no ECI header in this corpus
            QuietZoneModules = FixtureWriter.RmQRQuietZoneModules,
            PixelsPerModule = FixtureWriter.PixelsPerModule,
        };

        return new GeneratedFixture(manifest, modules);
    }
}
