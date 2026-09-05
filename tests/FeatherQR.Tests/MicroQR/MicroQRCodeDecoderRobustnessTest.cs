using FeatherQR.Internals.MicroQR;

namespace FeatherQR.Tests;

/// <summary>
/// Decoder behavior under damage, by equivalence class of the error-correction
/// capacity check (ISO/IEC 18004 Table 9, capacity t):
/// <list type="bullet">
/// <item>errors ≤ t → corrected, reported in <c>ErrorsCorrected</c></item>
/// <item>t &lt; errors ≤ ⌊ecc/2⌋ → Reed-Solomon could correct, but the capacity
/// cap must reject (misdecode-protection codewords p; the false-positive class)</item>
/// <item>errors &gt; ⌊ecc/2⌋ → Reed-Solomon itself fails</item>
/// <item>M1 (t = 0): any data error must be rejected, error detection only</item>
/// </list>
/// Plus format-information damage and cross-symbology rejection (a Micro QR
/// matrix presented to the Standard QR decoder and vice versa).
/// </summary>
public class MicroQRCodeDecoderRobustnessTest
{
    /// <summary>
    /// Flips one module inside each of <paramref name="codewordErrors"/> distinct
    /// codewords, walking the placement zigzag to find the module that carries the
    /// first bit of each codeword. The walk mirrors MicroQRModulePlacer.PlaceDataCodewords.
    /// </summary>
    private static void FlipCodewords(byte[] modules, int size, int codewordErrors)
    {
        var targetBits = new int[codewordErrors];
        for (var i = 0; i < codewordErrors; i++)
        {
            targetBits[i] = i * 8; // first bit of codeword i (data section first)
        }

        var bitIndex = 0;
        var next = 0;
        var upward = true;
        for (var right = size - 1; right >= 2 && next < targetBits.Length; right -= 2)
        {
            for (var step = 0; step < size && next < targetBits.Length; step++)
            {
                var row = upward ? size - 1 - step : step;
                for (var side = 0; side < 2 && next < targetBits.Length; side++)
                {
                    var col = right - side;
                    if (row == 0 || col == 0 || (row <= 8 && col <= 8))
                        continue;
                    if (bitIndex == targetBits[next])
                    {
                        modules[row * size + col] ^= 1;
                        next++;
                    }
                    bitIndex++;
                }
            }
            upward = !upward;
        }

        if (next != targetBits.Length)
            throw new InvalidOperationException($"only {next}/{targetBits.Length} target modules found");
    }

    private static byte[] CreateMatrix(string text, MicroQREccLevel ecc, out int size)
    {
        var calculated = Sizing.Required(text.AsSpan(), ecc, quietZoneSize: 0);
        var modules = new byte[calculated.BufferSize];
        MicroQRCodeGenerator.CreateMicroQRCode(text.AsSpan(), ecc, modules, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });
        size = calculated.QrSize;
        return modules;
    }

    // errors ≤ t: every version/ECC combination with correction capacity,
    // damaged at exactly its capacity t.
    [Test]
    [Arguments("0123456789", MicroQREccLevel.L, 1)]  // M2-L, t=1
    [Arguments("12345678", MicroQREccLevel.M, 2)]    // M2-M, t=2
    [Arguments("HELLO WORLD 14", MicroQREccLevel.L, 2)] // M3-L, t=2
    [Arguments("byte hi", MicroQREccLevel.M, 4)]     // M3-M, t=4
    [Arguments("HELLO WORLD PLUS 21ST", MicroQREccLevel.L, 3)] // M4-L, t=3
    [Arguments("bytes m4 mode", MicroQREccLevel.M, 5)] // M4-M, t=5
    [Arguments("bytes!!!!", MicroQREccLevel.Q, 7)]   // M4-Q, t=7
    public async Task TryDecode_ErrorsWithinCapacity_CorrectedAndReported(string text, MicroQREccLevel ecc, int capacity)
    {
        var modules = CreateMatrix(text, ecc, out var size);
        FlipCodewords(modules, size, capacity);

        var success = MicroQRCodeDecoder.TryDecode(modules, size, out var decoded, out var info);

        await Assert.That(success).IsTrue();
        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.ErrorsCorrected).IsEqualTo(capacity);
    }

    // t < errors ≤ ⌊ecc/2⌋: Reed-Solomon alone would correct these, but the
    // capacity cap must reject them (this is the misdecode-protection class that
    // a naive full-strength RS decoder gets wrong).
    [Test]
    [Arguments("0123456789", MicroQREccLevel.L, 2)]  // M2-L: t=1, RS could do 2
    [Arguments("12345678", MicroQREccLevel.M, 3)]    // M2-M: t=2, RS could do 3
    [Arguments("HELLO WORLD 14", MicroQREccLevel.L, 3)] // M3-L: t=2, RS could do 3
    [Arguments("HELLO WORLD PLUS 21ST", MicroQREccLevel.L, 4)] // M4-L: t=3, RS could do 4
    public async Task TryDecode_ErrorsBeyondCapacityButWithinRsRange_Rejected(string text, MicroQREccLevel ecc, int errors)
    {
        var modules = CreateMatrix(text, ecc, out var size);
        FlipCodewords(modules, size, errors);

        var success = MicroQRCodeDecoder.TryDecode(modules, size, out _, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(info.Status).IsEqualTo(QRCodeDecodeStatus.DataUncorrectable);
    }

    // errors > ⌊ecc/2⌋: Reed-Solomon decoding itself fails.
    [Test]
    [Arguments("byte hi", MicroQREccLevel.M, 5)]     // M3-M: t=4=⌊8/2⌋
    [Arguments("bytes!!!!", MicroQREccLevel.Q, 8)]   // M4-Q: t=7=⌊14/2⌋
    public async Task TryDecode_ErrorsBeyondRsRange_Rejected(string text, MicroQREccLevel ecc, int errors)
    {
        var modules = CreateMatrix(text, ecc, out var size);
        FlipCodewords(modules, size, errors);

        var success = MicroQRCodeDecoder.TryDecode(modules, size, out _, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(info.Status).IsEqualTo(QRCodeDecodeStatus.DataUncorrectable);
    }

    [Test]
    public async Task TryDecode_M1_SingleDataError_Rejected()
    {
        // M1 is error detection only (t = 0): a single module flip in the data
        // region must fail the decode even though the RS(5,3) code could correct it.
        var modules = CreateMatrix("12345", MicroQREccLevel.ErrorDetectionOnly, out var size);
        FlipCodewords(modules, size, 1);

        var success = MicroQRCodeDecoder.TryDecode(modules, size, out _, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(info.Status).IsEqualTo(QRCodeDecodeStatus.DataUncorrectable);
    }

    [Test]
    [Arguments(new[] { 14 })]
    [Arguments(new[] { 14, 0 })]
    [Arguments(new[] { 13, 7, 1 })]
    public async Task TryDecode_FormatInformationDamageWithinBch_StillDecodes(int[] flippedFormatBits)
    {
        var modules = CreateMatrix("0123456789", MicroQREccLevel.L, out var size);
        foreach (var bit in flippedFormatBits)
        {
            FlipFormatModule(modules, size, bit);
        }

        var success = MicroQRCodeDecoder.TryDecode(modules, size, out var decoded, out var info);

        await Assert.That(success).IsTrue();
        await Assert.That(decoded).IsEqualTo("0123456789");
        await Assert.That(info.Version).IsEqualTo(MicroQRVersion.M2);
        await Assert.That(info.EccLevel).IsEqualTo(MicroQREccLevel.L);
    }

    [Test]
    public async Task TryDecode_FormatInformationDestroyed_ReportsFormatInformationInvalid()
    {
        var modules = CreateMatrix("0123456789", MicroQREccLevel.L, out var size);

        // Overwrite the 15 format modules with a pattern at Hamming distance ≥ 4
        // from every valid candidate (found deterministically by search).
        var damaged = FindRawBeyondCorrectionDistance();
        WriteFormatModules(modules, size, damaged);

        var success = MicroQRCodeDecoder.TryDecode(modules, size, out _, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(info.Status).IsEqualTo(QRCodeDecodeStatus.FormatInformationInvalid);
    }

    [Test]
    public async Task TryDecode_FormatNamesDifferentVersionThanMatrixSize_Rejected()
    {
        // A 13×13 (M2) matrix whose format information names M4-L: individually
        // valid pieces that contradict each other must not decode.
        var modules = CreateMatrix("0123456789", MicroQREccLevel.L, out var size);
        WriteFormatModules(modules, size, MicroQRConstants.GetFormatBits(MicroQRVersion.M4, MicroQREccLevel.L, 0));

        var success = MicroQRCodeDecoder.TryDecode(modules, size, out _, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(info.Status).IsEqualTo(QRCodeDecodeStatus.FormatInformationInvalid);
    }

    [Test]
    public async Task StandardDecoder_RejectsMicroQRMatrix()
    {
        // All Micro QR sizes, bare core and with the spec quiet zone.
        foreach (var (text, ecc) in ((string, MicroQREccLevel)[])
        [
            ("12345", MicroQREccLevel.ErrorDetectionOnly),
            ("0123456789", MicroQREccLevel.L),
            ("HELLO WORLD 14", MicroQREccLevel.L),
            ("bytes!!!!", MicroQREccLevel.Q),
        ])
        {
            foreach (var quietZone in (int[])[0, 2])
            {
                var calculated = Sizing.Required(text.AsSpan(), ecc, quietZoneSize: quietZone);
                var modules = new byte[calculated.BufferSize];
                MicroQRCodeGenerator.CreateMicroQRCode(text.AsSpan(), ecc, modules, new MicroQRCodeGeneratorOptions { QuietZoneSize = quietZone });

                var success = QRCodeDecoder.TryDecode(modules, calculated.QrSize, out _, out var info);

                await Assert.That(success).IsFalse();
                await Assert.That(info.Status).IsEqualTo(QRCodeDecodeStatus.InvalidMatrix);
            }
        }
    }

    [Test]
    public async Task MicroDecoder_RejectsStandardQrMatrix()
    {
        // Standard QR v1 (21×21) bare and with quiet zone; 21 is not a Micro QR size.
        foreach (var quietZone in (int[])[0, 4])
        {
            var qr = QRCodeGenerator.CreateQrCode("HELLO WORLD", ECCLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = quietZone });
            var size = qr.GetCoreSize() + quietZone * 2;
            var modules = new byte[size * size];
            for (var row = 0; row < size; row++)
            {
                for (var col = 0; col < size; col++)
                {
                    modules[row * size + col] = qr[row, col] ? (byte)1 : (byte)0;
                }
            }

            var success = MicroQRCodeDecoder.TryDecode(modules, size, out _, out var info);

            await Assert.That(success).IsFalse();
            await Assert.That(info.Status).IsEqualTo(QRCodeDecodeStatus.InvalidMatrix);
        }
    }

    /// <summary>Flips the format module carrying format bit <paramref name="bit"/> (14…0).</summary>
    private static void FlipFormatModule(byte[] modules, int size, int bit)
    {
        var (row, col) = FormatModulePosition(bit);
        modules[row * size + col] ^= 1;
    }

    /// <summary>Overwrites all 15 format modules with <paramref name="formatBits"/>.</summary>
    private static void WriteFormatModules(byte[] modules, int size, ushort formatBits)
    {
        for (var bit = 14; bit >= 0; bit--)
        {
            var (row, col) = FormatModulePosition(bit);
            modules[row * size + col] = (byte)((formatBits >> bit) & 1);
        }
    }

    /// <summary>
    /// Format bit positions per MicroQRModulePlacer.PlaceFormat: bits 14…7 along
    /// row 8 columns 1-8, bits 6…0 down column 8 rows 7-1.
    /// </summary>
    private static (int row, int col) FormatModulePosition(int bit)
        => bit >= 7 ? (8, 15 - bit) : (bit + 1, 8);

    private static ushort FindRawBeyondCorrectionDistance()
    {
        var candidates = new ushort[32];
        for (var symbolNumber = 0; symbolNumber < 8; symbolNumber++)
        {
            MicroQRConstants.GetVersionAndEccFromSymbolNumber(symbolNumber, out var version, out var ecc);
            for (var mask = 0; mask < 4; mask++)
            {
                candidates[symbolNumber * 4 + mask] = MicroQRConstants.GetFormatBits(version, ecc, mask);
            }
        }

        for (var raw = 0; raw < (1 << 15); raw++)
        {
            var minDistance = int.MaxValue;
            foreach (var candidate in candidates)
            {
                minDistance = Math.Min(minDistance, System.Numerics.BitOperations.PopCount((uint)(raw ^ candidate)));
            }
            if (minDistance > 3)
                return (ushort)raw;
        }

        throw new InvalidOperationException("no raw value beyond correction distance found");
    }
}
