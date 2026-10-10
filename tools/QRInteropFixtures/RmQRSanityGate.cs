using ZXingCpp;

namespace QRInteropFixtures;

/// <summary>
/// Fixture sanity gate: every generated rMQR fixture must decode with the
/// zxing-cpp reader (the only maintained OSS rMQR decode lineage) before it is
/// written, so a broken generator cannot poison the committed corpus. Payload is
/// compared on raw bytes: byte-mode UTF-8 without ECI is exposed by the reader
/// with a legacy-charset guess in <c>Text</c> while <c>Bytes</c> is exact
/// (recorded in the fixture record). The reader's version / ECC metadata must
/// match the manifest, and its mask value (rMQR's single mask, reported as
/// Standard QR pattern 4) is recorded as externally sourced.
/// </summary>
public static class RmQRSanityGate
{
    /// <returns>The mask pattern reported by the reader (expected 4), or -1 when unavailable.</returns>
    /// <exception cref="InvalidOperationException">The fixture does not decode as its manifest claims.</exception>
    public static int VerifyAndGetMask(GeneratedFixture fixture)
    {
        var manifest = fixture.Manifest;
        var pixelsPerModule = manifest.PixelsPerModule;
        var quietZone = manifest.QuietZoneModules;
        var widthPixels = (manifest.Width + quietZone * 2) * pixelsPerModule;
        var heightPixels = (manifest.Height + quietZone * 2) * pixelsPerModule;

        // Luminance image: 0 = dark, 255 = light, quiet zone light.
        var luminance = new byte[widthPixels * heightPixels];
        luminance.AsSpan().Fill(255);
        for (var row = 0; row < manifest.Height; row++)
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

        var results = ZXingCppCalls.Read(new BarcodeReader { Formats = BarcodeFormat.RMQRCode, TryHarder = true }, luminance, widthPixels, heightPixels);
        if (results.Length != 1)
            throw new InvalidOperationException($"sanity gate: zxing-cpp found {results.Length} symbols in fixture {manifest.Generator}/{manifest.Id}.");

        var result = results[0];

        if (manifest.Mode == KanjiPayload.ModeName)
        {
            // A Kanji segment puts the raw Shift_JIS bytes in Bytes, so the byte
            // comparison used for the other modes does not apply. Text is the check
            // that matters here anyway: it is the reader's JIS X 0208 reading, which
            // is exactly what this library's decoder has to reproduce.
            if (result.Text != manifest.PayloadText)
                throw new InvalidOperationException($"sanity gate: Kanji fixture {manifest.Generator}/{manifest.Id} decodes as \"{result.Text}\" (bytes {Convert.ToHexString(result.Bytes)}), manifest says \"{manifest.PayloadText}\".");
        }
        else
        {
            var expectedBytes = Convert.FromHexString(manifest.PayloadUtf8Hex);
            if (!result.Bytes.AsSpan().SequenceEqual(expectedBytes))
                throw new InvalidOperationException($"sanity gate: fixture {manifest.Generator}/{manifest.Id} decodes to bytes {Convert.ToHexString(result.Bytes)}, manifest says {manifest.PayloadUtf8Hex} (\"{manifest.PayloadText}\").");
        }

        var version = result.Extra("Version");
        if (version != manifest.VersionName)
            throw new InvalidOperationException($"sanity gate: fixture {manifest.Generator}/{manifest.Id} reads as version {version}, manifest says {manifest.VersionName}.");

        var eccLevel = result.Extra("EcLevel");
        if (eccLevel != manifest.ErrorCorrectionLevel)
            throw new InvalidOperationException($"sanity gate: fixture {manifest.Generator}/{manifest.Id} reads as ECC {eccLevel}, manifest says {manifest.ErrorCorrectionLevel}.");

        return int.TryParse(result.Extra("DataMask"), out var mask) ? mask : -1;
    }
}
