using System.Text;
using FeatherQR;
using FeatherQR.Tests;

/// <summary>A payload with the symbol it is measured in: the symbology, and the error correction level and version every encoder is pinned to.</summary>
internal sealed record Case(string Id, string Symbology, string Payload, string Ecc, string Version);

/// <summary>
/// Writes the corpus every CLI reads: per case, the payload as UTF-8, the bare symbol as a one-pixel-per-module PGM, and the symbol drawn as an 8-bit grey PGM.
/// </summary>
internal static class Corpus
{
    /// <summary>The payloads and symbols of the benchmark project's <c>Simple*</c> classes, so a row here can be held against a row there.</summary>
    public static readonly Case[] Cases =
    [
        new("qr-numeric", "qr", "0123456789012345678901234567890123456789", "L", "1"),
        new("qr-alphanumeric", "qr", "0123456789ABCDEFG0123456789HIJKLMN", "L", "2"),
        new("qr-url", "qr", "https://example.com/user/repo?foo=value&bar=piyo", "L", "3"),
        new("qr-unicode", "qr", "FooBar你好世界こんにちはПривет мир🎉🎊🎈Zürich", "L", "4"),
        new("qr-wifi", "qr", "WIFI:S:foobar-wifi;T:WPA;P:test123;H:false;;", "L", "3"),
        new("microqr-numeric", "microqr", "0123456789", "L", "M2"),
        new("microqr-alphanumeric", "microqr", "HELLO WORLD 14", "L", "M3"),
        new("microqr-byte", "microqr", "bytes m4 mode", "M", "M4"),
        new("rmqr-numeric", "rmqr", "012345678901", "M", "R7x43"),
        new("rmqr-alphanumeric", "rmqr", "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 $%*+-.", "M", "R11x59"),
        new("rmqr-byte", "rmqr", string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog?! ", 4))[..150], "M", "R17x139"),
    ];

    public const int PixelsPerModule = 8;

    public static void Write(string dir)
    {
        Directory.CreateDirectory(dir);
        var manifest = new StringBuilder(ManifestEntry.Header).Append('\n');
        foreach (var c in Cases)
        {
            var payload = Encoding.UTF8.GetBytes(c.Payload);
            var expected = Convert.ToHexString(payload);
            File.WriteAllBytes(Path.Combine(dir, $"{c.Id}.txt"), payload);

            var (isDark, width, height, quietZone) = Encode(c);
            var matrix = new byte[width * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                    matrix[y * width + x] = isDark(y, x) ? (byte)0 : (byte)255;
            }
            Pgm.Write(Path.Combine(dir, $"{c.Id}.matrix.pgm"), matrix, width, height);

            var image = NearestNeighbourRenderer.Render(
                (row, col) => row >= quietZone && col >= quietZone && row < quietZone + height && col < quietZone + width && isDark(row - quietZone, col - quietZone),
                width + 2 * quietZone, height + 2 * quietZone, PixelsPerModule, 0, 0);
            Pgm.Write(Path.Combine(dir, $"{c.Id}.image.pgm"), image.Luminance, image.Width, image.Height);

            manifest.Append(new ManifestEntry($"encode/{c.Id}", c.Id, "encode", c.Symbology, $"{c.Id}.txt", c.Ecc, c.Version, expected).ToLine()).Append('\n');
            manifest.Append(new ManifestEntry($"decode-matrix/{c.Id}", c.Id, "decode-matrix", c.Symbology, $"{c.Id}.matrix.pgm", null, null, expected).ToLine()).Append('\n');
            manifest.Append(new ManifestEntry($"decode-image/{c.Id}", c.Id, "decode-image", c.Symbology, $"{c.Id}.image.pgm", null, null, expected).ToLine()).Append('\n');
        }
        File.WriteAllText(Path.Combine(dir, ManifestEntry.FileName), manifest.ToString());

        // A corpus this library cannot read back is broken, not a measurement.
        foreach (var entry in ManifestEntry.Read(dir).Where(e => e.Op != "encode"))
        {
            var description = Operations.Load(entry.Op, entry.Symbology, Path.Combine(dir, entry.Input), null, null).Describe();
            if (!description.Contains($"\"text\":\"{entry.ExpectedHex}\"", StringComparison.Ordinal))
                throw new InvalidOperationException($"FeatherQR does not read {entry.Key} back: {description}");
        }
    }

    /// <summary>The bare symbol FeatherQR encodes at the case's pinned level and version, and the quiet zone its symbology specifies.</summary>
    private static (Func<int, int, bool> IsDark, int Width, int Height, int QuietZone) Encode(Case c)
    {
        switch (c.Symbology)
        {
            case "qr":
                {
                    var data = QRCodeGenerator.Create(c.Payload.AsSpan(), Enum.Parse<QREccLevel>(c.Ecc), new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(int.Parse(c.Version)), QuietZoneSize = 0 });
                    return ((row, col) => data[row, col], data.Size, data.Size, 4);
                }
            case "microqr":
                {
                    var data = MicroQRCodeGenerator.Create(c.Payload.AsSpan(), Enum.Parse<MicroQREccLevel>(c.Ecc), new MicroQRCodeGeneratorOptions { Version = Enum.Parse<MicroQRVersion>(c.Version), QuietZoneSize = 0 });
                    return ((row, col) => data[row, col], data.Size, data.Size, 2);
                }
            case "rmqr":
                {
                    var data = RmQRCodeGenerator.Create(c.Payload.AsSpan(), Enum.Parse<RmQREccLevel>(c.Ecc), new RmQRCodeGeneratorOptions { Version = Enum.Parse<RmQRVersion>(c.Version), QuietZoneSize = 0 });
                    return ((row, col) => data[row, col], data.Width, data.Height, 2);
                }
            default:
                throw new ArgumentException($"Unknown symbology {c.Symbology}.");
        }
    }
}
