using FeatherQR;
using ZXingCpp;

namespace QRInteropFixtures;

/// <summary>
/// Correction-capacity oracle probe: answers "does rMQR reserve misdecode-protection
/// codewords p, the way ISO/IEC 18004 Table 9 does for Micro QR?" by measurement
/// rather than by reading ISO/IEC 23941 Table 8 (paywalled; the public preview stops
/// short of it).
/// </summary>
/// <remarks>
/// For every version × ECC the probe damages a symbol one module at a time, keeping
/// only flips that our own decoder reports as exactly one more corrected codeword, so
/// the damage saturates at our capacity in every Reed-Solomon block without needing
/// any block-structure table here. It then asks zxing-cpp — the only maintained OSS
/// rMQR decode lineage — the same symbol, in both directions:
/// <list type="bullet">
/// <item>at saturation, zxing-cpp must still decode. A failure would mean zxing-cpp
/// stops below our capacity, i.e. a reserved p we are ignoring (we over-correct).</item>
/// <item>one error past saturation, zxing-cpp must not decode to the original text
/// either. A success would mean zxing-cpp reaches further than we do, i.e. our
/// capacity is too low (we under-correct).</item>
/// </list>
/// Both green is evidence for p = 0 in the reference implementation; it is not a
/// reading of the standard, and the Correction cap decision in
/// specs/rmqr-decoder.md stays open until someone reads Table 8.
/// <para>
/// Usage: <c>dotnet run --project tools/QRInteropFixtures -- probe-rmqr-capacity</c>
/// </para>
/// </remarks>
public static class RmQRCapacityProbe
{
    private const int QuietZoneModules = 2; // ISO/IEC 23941 quiet zone
    private const int PixelsPerModule = 6;
    private const int Seed = 23941;         // deterministic damage, so a failure reproduces

    /// <summary>Consecutive rejected flips before a symbol counts as saturated.</summary>
    private const int SaturationAttempts = 4000;

    public static int Run()
    {
        var reader = new BarcodeReader { Formats = BarcodeFormat.RMQRCode, TryHarder = true };
        var failures = 0;
        var total = 0;

        foreach (var version in Enum.GetValues<RmQRVersion>())
        {
            foreach (var ecc in new[] { RmQREccLevel.M, RmQREccLevel.H })
            {
                total++;
                var text = "R" + (int)version; // 2-3 alphanumeric chars: fits every version × ECC
                var size = Sizing.Required(text.AsSpan(), ecc, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = QuietZoneModules });
                var pristine = new byte[size.BufferSize];
                RmQRCodeGenerator.Create(text.AsSpan(), ecc, pristine, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = QuietZoneModules });

                var damaged = (byte[])pristine.Clone();
                var saturation = Saturate(damaged, size.Width, size.Height, text);
                if (saturation < 0)
                {
                    failures++;
                    Console.Error.WriteLine($"FAIL: {version}-{ecc} the pristine symbol does not decode with our own decoder");
                    continue;
                }

                // Direction 1: zxing-cpp must reach our capacity.
                if (!DecodesAs(reader, damaged, size.Width, size.Height, text))
                {
                    failures++;
                    Console.Error.WriteLine($"FAIL: {version}-{ecc} zxing-cpp rejects {saturation} corrected codewords that we correct (a reserved p we ignore?)");
                    continue;
                }

                // Direction 2: zxing-cpp must not reach past it.
                var beyond = (byte[])damaged.Clone();
                if (!FlipOneMoreCodeword(beyond, size.Width, size.Height, text))
                {
                    failures++;
                    Console.Error.WriteLine($"FAIL: {version}-{ecc} could not place an error past saturation");
                    continue;
                }

                if (DecodesAs(reader, beyond, size.Width, size.Height, text))
                {
                    failures++;
                    Console.Error.WriteLine($"FAIL: {version}-{ecc} zxing-cpp corrects {saturation + 1} codewords where we stop at {saturation} (our capacity is too low)");
                    continue;
                }

                Console.WriteLine($"ok: {version}-{ecc} saturates at {saturation} corrected codewords, zxing-cpp agrees in both directions");
            }
        }

        if (total != 64)
        {
            Console.Error.WriteLine($"FAIL: expected 64 version × ECC combinations, probed {total}");
            return 1;
        }

        Console.WriteLine(failures == 0
            ? $"probe-rmqr-capacity: all {total} version × ECC combinations agree with zxing-cpp at full Reed-Solomon strength (no misdecode-protection codewords p)"
            : $"probe-rmqr-capacity: {failures}/{total} FAILED");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Flips modules until no further flip adds a corrected codeword, i.e. every
    /// Reed-Solomon block sits at our correction capacity.
    /// </summary>
    /// <returns>The number of corrected codewords at saturation, or -1 when the pristine symbol does not decode.</returns>
    private static int Saturate(byte[] modules, int width, int height, string text)
    {
        if (!OurDecode(modules, width, height, text, out var corrected) || corrected != 0)
            return -1;

        var random = new Random(Seed);
        var misses = 0;
        while (misses < SaturationAttempts)
        {
            if (TryAddOneCodewordError(modules, width, height, text, random, ref corrected))
            {
                misses = 0;
                continue;
            }
            misses++;
        }
        return corrected;
    }

    /// <summary>
    /// Flips one interior module and keeps it only when our decoder still succeeds with
    /// exactly one more corrected codeword. That rejects function-pattern modules (the
    /// decode breaks or is unaffected) and modules of an already-damaged codeword (the
    /// count does not move), so every accepted flip is one fresh codeword error.
    /// </summary>
    private static bool TryAddOneCodewordError(byte[] modules, int width, int height, string text, Random random, ref int corrected)
    {
        var row = random.Next(QuietZoneModules, height - QuietZoneModules);
        var col = random.Next(QuietZoneModules, width - QuietZoneModules);
        var index = row * width + col;

        modules[index] ^= 1;
        if (OurDecode(modules, width, height, text, out var after) && after == corrected + 1)
        {
            corrected = after;
            return true;
        }

        modules[index] ^= 1;
        return false;
    }

    /// <summary>Places one error past saturation: any flip our decoder can no longer absorb.</summary>
    private static bool FlipOneMoreCodeword(byte[] modules, int width, int height, string text)
    {
        var random = new Random(Seed + 1);
        for (var attempt = 0; attempt < SaturationAttempts; attempt++)
        {
            var row = random.Next(QuietZoneModules, height - QuietZoneModules);
            var col = random.Next(QuietZoneModules, width - QuietZoneModules);
            var index = row * width + col;

            modules[index] ^= 1;
            if (!OurDecode(modules, width, height, text, out _))
                return true;

            modules[index] ^= 1;
        }
        return false;
    }

    private static bool OurDecode(byte[] modules, int width, int height, string expected, out int corrected)
    {
        if (RmQRCodeDecoder.TryDecode(modules, width, height, out var decoded, out var info) && decoded == expected)
        {
            corrected = info.ErrorsCorrected;
            return true;
        }

        corrected = 0;
        return false;
    }

    private static bool DecodesAs(BarcodeReader reader, byte[] modules, int width, int height, string expected)
    {
        var luminance = RenderLuminance(modules, width, height, PixelsPerModule);
        var image = new ImageView(luminance, width * PixelsPerModule, height * PixelsPerModule, ImageFormat.Lum);
        var results = reader.From(image);
        return results.Length == 1 && results[0].Text == expected;
    }

    private static byte[] RenderLuminance(byte[] modules, int width, int height, int pixelsPerModule)
    {
        var widthPixels = width * pixelsPerModule;
        var luminance = new byte[widthPixels * height * pixelsPerModule];
        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                var value = modules[row * width + col] != 0 ? (byte)0 : (byte)255;
                for (var y = 0; y < pixelsPerModule; y++)
                {
                    luminance.AsSpan((row * pixelsPerModule + y) * widthPixels + col * pixelsPerModule, pixelsPerModule).Fill(value);
                }
            }
        }
        return luminance;
    }
}
