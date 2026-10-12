using ZXingCpp;

namespace QRInteropFixtures;

/// <summary>
/// Fixture sanity gate: every generated Micro QR fixture must decode with the
/// zxing-cpp reader (the only maintained OSS Micro QR decode lineage) before it
/// is written, so a broken generator cannot poison the committed corpus. The
/// reader's metadata also supplies the mask pattern for the manifest, an
/// externally-sourced value, not this library's own reading.
/// </summary>
public static class MicroQRSanityGate
{
    /// <summary>
    /// Renders the fixture to a luminance image, decodes it with zxing-cpp, and
    /// verifies payload, version and ECC level against the manifest.
    /// </summary>
    /// <returns>The mask pattern (0-3) reported by the reader, or -1 when unavailable.</returns>
    /// <exception cref="InvalidOperationException">The fixture does not decode as its manifest claims.</exception>
    public static int VerifyAndGetMask(GeneratedFixture fixture)
    {
        var manifest = fixture.Manifest;
        var pixelsPerModule = manifest.PixelsPerModule;
        var quietZone = manifest.QuietZoneModules;
        var sizeWithQuietZone = manifest.Width + quietZone * 2;
        var widthPixels = sizeWithQuietZone * pixelsPerModule;

        // Luminance image: 0 = dark, 255 = light, quiet zone light.
        var luminance = new byte[widthPixels * widthPixels];
        luminance.AsSpan().Fill(255);
        for (var row = 0; row < manifest.Width; row++)
        {
            for (var col = 0; col < manifest.Width; col++)
            {
                if (fixture.Modules[row * manifest.Width + col] == 0)
                    continue;

                var pixelRow = (quietZone + row) * pixelsPerModule;
                var pixelCol = (quietZone + col) * pixelsPerModule;
                for (var y = 0; y < pixelsPerModule; y++)
                {
                    luminance.AsSpan((pixelRow + y) * widthPixels + pixelCol, pixelsPerModule).Clear();
                }
            }
        }

        var results = ZXingCppCalls.Read(new BarcodeReader { Formats = BarcodeFormat.MicroQRCode, TryHarder = true }, luminance, widthPixels, widthPixels);
        if (results.Length != 1)
            throw new InvalidOperationException($"sanity gate: zxing-cpp found {results.Length} symbols in fixture {manifest.Generator}/{manifest.Id}.");

        var result = results[0];
        if (result.Text != manifest.PayloadText)
            throw new InvalidOperationException($"sanity gate: fixture {manifest.Generator}/{manifest.Id} decodes as \"{result.Text}\", manifest says \"{manifest.PayloadText}\".");

        var version = result.Extra("Version");
        if (version != $"M{manifest.Version}")
            throw new InvalidOperationException($"sanity gate: fixture {manifest.Generator}/{manifest.Id} reads as version {version}, manifest says M{manifest.Version}.");

        // zxing-cpp reports M1's implicit detection-only level as "L".
        var expectedEcc = manifest.ErrorCorrectionLevel == "ErrorDetectionOnly" ? "L" : manifest.ErrorCorrectionLevel;
        var eccLevel = result.Extra("EcLevel");
        if (eccLevel != expectedEcc)
            throw new InvalidOperationException($"sanity gate: fixture {manifest.Generator}/{manifest.Id} reads as ECC {eccLevel}, manifest says {manifest.ErrorCorrectionLevel}.");

        return int.TryParse(result.Extra("DataMask"), out var mask) ? mask : -1;
    }
}
