using ZXingCpp;

namespace QRInteropFixtures;

/// <summary>
/// FNC1 fixture generator backed by libzint (zxing-cpp's writer, compiled into the pinned ZXingCpp
/// native binary), a zint-lineage encoder independent of both this library and the zxing-cpp
/// reader used as the sanity gate. Its <c>gs1</c> option takes bracketed or parenthesized AI
/// syntax, validates it, and writes FNC1 in first position for Standard QR and rMQR (<c>version=N</c>
/// is the ISO/IEC 23941 version index + 1). It writes FNC1 before an ECI header (<c>eci=26</c>).
/// </summary>
public sealed class ZintFnc1FixtureGenerator : IFnc1FixtureGenerator
{
    public string Name => "zint-libzint";

    public bool IsAvailable
    {
        get
        {
            try
            {
                using var barcode = new BarcodeCreator(BarcodeFormat.QRCode).From("0");
                return barcode.IsValid;
            }
            catch
            {
                return false;
            }
        }
    }

    public GeneratedFixture Generate(Fnc1CaseDefinition caseDefinition)
    {
        if (caseDefinition.Position != Fnc1Position.First)
            throw new NotSupportedException($"libzint writes FNC1 in first position only (case {caseDefinition.Id}).");

        var options = $"gs1,ecLevel={caseDefinition.ErrorCorrectionLevel}";
        if (caseDefinition.IsRmQR)
            options += $",version={caseDefinition.RmQRVersion}";
        if (caseDefinition.Utf8)
            options += ",eci=26";

        var creator = new BarcodeCreator(caseDefinition.IsRmQR ? BarcodeFormat.RMQRCode : BarcodeFormat.QRCode) { Options = options };
        using var barcode = creator.From(caseDefinition.Input);
        var matrix = Fnc1Writers.FromZint(barcode);

        int version;
        string? versionName = null;
        if (caseDefinition.IsRmQR)
        {
            var entry = RmQRVersionTable.Entries[caseDefinition.RmQRVersion - 1];
            if (matrix.Width != entry.Width || matrix.Height != entry.Height)
                throw new InvalidOperationException($"libzint produced a {matrix.Height}x{matrix.Width} symbol for case {caseDefinition.Id}, expected {entry.Name}.");
            version = entry.Number;
            versionName = entry.Name;
        }
        else
        {
            if (matrix.Width != matrix.Height || matrix.Width < 21 || (matrix.Width - 17) % 4 != 0)
                throw new InvalidOperationException($"libzint produced a {matrix.Height}x{matrix.Width} symbol for case {caseDefinition.Id}, not a Standard QR size.");
            version = (matrix.Width - 17) / 4;
        }

        // libzint reports neither its segmentation nor its mask, so the gate takes the mask from the reader.
        return new GeneratedFixture(Fnc1Manifests.Build(Name, Fnc1Writers.ZintVersion, caseDefinition, matrix, version, versionName, mode: "Unknown", maskPattern: -1), matrix.Modules);
    }
}
