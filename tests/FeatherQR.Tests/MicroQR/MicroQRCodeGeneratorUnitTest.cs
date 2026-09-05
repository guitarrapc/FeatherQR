using FeatherQR.Internals.MicroQR;

namespace FeatherQR.Tests;

public class MicroQRCodeGeneratorUnitTest
{
    // ---------------------------------------------------------------
    // Version auto-selection (mode x ECC x length equivalence classes)
    // ---------------------------------------------------------------

    [Test]
    // Numeric
    [Arguments("12345", MicroQREccLevel.ErrorDetectionOnly, MicroQRVersion.M1)] // M1 numeric capacity boundary (5)
    [Arguments("12345", MicroQREccLevel.L, MicroQRVersion.M2)]                  // EDO-only M1 skipped when L requested
    [Arguments("1234567890", MicroQREccLevel.L, MicroQRVersion.M2)]             // M2-L numeric boundary (10)
    [Arguments("12345678901", MicroQREccLevel.L, MicroQRVersion.M3)]            // one over M2-L
    [Arguments("12345678901234567890123", MicroQREccLevel.L, MicroQRVersion.M3)] // M3-L numeric boundary (23)
    [Arguments("123456789012345678901234", MicroQREccLevel.L, MicroQRVersion.M4)] // one over M3-L
    [Arguments("12345678901234567890123456789012345", MicroQREccLevel.L, MicroQRVersion.M4)] // M4-L numeric boundary (35)
    [Arguments("123456789012345678", MicroQREccLevel.M, MicroQRVersion.M3)]     // M3-M numeric boundary (18)
    [Arguments("123456789012345678901", MicroQREccLevel.Q, MicroQRVersion.M4)]  // M4-Q numeric boundary (21)
    // Alphanumeric
    [Arguments("HELLO*", MicroQREccLevel.L, MicroQRVersion.M2)]                 // M2-L alphanumeric boundary (6)
    [Arguments("HELLO*+", MicroQREccLevel.L, MicroQRVersion.M3)]                // one over M2-L
    [Arguments("HELLO WORLD 14", MicroQREccLevel.L, MicroQRVersion.M3)]         // M3-L alphanumeric boundary (14)
    [Arguments("HELLO WORLD PLUS 21ST", MicroQREccLevel.L, MicroQRVersion.M4)]  // M4-L alphanumeric boundary (21)
    // Byte
    [Arguments("byte data", MicroQREccLevel.L, MicroQRVersion.M3)]              // M3-L byte boundary (9)
    [Arguments("byte data!", MicroQREccLevel.L, MicroQRVersion.M4)]             // one over M3-L
    [Arguments("bytes!!", MicroQREccLevel.M, MicroQRVersion.M3)]                // M3-M byte boundary (7)
    [Arguments("bytes!!!!", MicroQREccLevel.Q, MicroQRVersion.M4)]              // M4-Q byte boundary (9)
    public async Task CreateMicroQRCode_AutoVersion_SelectsSmallestLegalVersion(string text, MicroQREccLevel ecc, MicroQRVersion expectedVersion)
    {
        var data = MicroQRCodeGenerator.CreateMicroQRCode(text, ecc);
        await Assert.That(data.Version).IsEqualTo(expectedVersion);
    }

    // ---------------------------------------------------------------
    // Illegal combinations (must throw, never silently downgrade)
    // ---------------------------------------------------------------

    [Test]
    [Arguments("123456", MicroQREccLevel.ErrorDetectionOnly)] // over M1 capacity; EDO cannot escalate past M1
    [Arguments("HELLO", MicroQREccLevel.ErrorDetectionOnly)]  // alphanumeric not available on M1
    [Arguments("bytes", MicroQREccLevel.ErrorDetectionOnly)]  // byte not available on M1
    [Arguments("123456789012345678901234567890123456", MicroQREccLevel.L)] // over M4-L numeric capacity (36 digits)
    [Arguments("this payload is far too long for M4", MicroQREccLevel.Q)]  // over M4-Q byte capacity
    public async Task CreateMicroQRCode_AutoVersion_ThrowsWhenNothingFits(string text, MicroQREccLevel ecc)
    {
        await Assert.That(() => MicroQRCodeGenerator.CreateMicroQRCode(text, ecc)).Throws<ArgumentException>();
    }

    [Test]
    [Arguments("123", MicroQREccLevel.L, MicroQRVersion.M1)]        // M1 supports error detection only
    [Arguments("123", MicroQREccLevel.Q, MicroQRVersion.M2)]        // Q requires M4
    [Arguments("123", MicroQREccLevel.Q, MicroQRVersion.M3)]        // Q requires M4
    [Arguments("123", MicroQREccLevel.ErrorDetectionOnly, MicroQRVersion.M2)] // EDO is M1-only
    [Arguments("HELLO", MicroQREccLevel.L, MicroQRVersion.M1)]      // requested version cannot hold the mode
    [Arguments("bytes", MicroQREccLevel.L, MicroQRVersion.M2)]      // M2 has no byte mode
    [Arguments("123456789012345678901234", MicroQREccLevel.L, MicroQRVersion.M3)] // over requested version capacity
    public async Task CreateMicroQRCode_RequestedVersion_ThrowsOnIllegalCombination(string text, MicroQREccLevel ecc, MicroQRVersion version)
    {
        await Assert.That(() => MicroQRCodeGenerator.CreateMicroQRCode(text, ecc, new MicroQRCodeGeneratorOptions { Version = version })).Throws<ArgumentException>();
    }

    // ---------------------------------------------------------------
    // Capacity error messages (interactive UIs surface these verbatim,
    // so they must state the actual length, the applicable maximum, and
    // an actionable way out)
    // ---------------------------------------------------------------

    [Test]
    public async Task CreateMicroQRCode_AutoVersion_TooLongByte_MessageStatesLengthMaximumAndRemedy()
    {
        // 37 lowercase bytes; ECC M byte capacity tops out at 13 (M4-M)
        var text = "this text is way too long for microqr";
        var ex = Assert.Throws<ArgumentException>(() => MicroQRCodeGenerator.CreateMicroQRCode(text, MicroQREccLevel.M));

        await Assert.That(ex.Message).Contains("too long for Micro QR");
        await Assert.That(ex.Message).Contains("37 bytes");     // actual encoded length
        await Assert.That(ex.Message).Contains("13 bytes");     // maximum at ECC M (M4)
        await Assert.That(ex.Message).Contains("M4");           // version carrying that maximum
        await Assert.That(ex.Message).Contains("QRCodeGenerator"); // remedy: Standard QR
    }

    [Test]
    public async Task CreateMicroQRCode_AutoVersion_TooLongNumeric_MessageStatesDigitMaximum()
    {
        // 36 digits; ECC L numeric capacity tops out at 35 (M4-L)
        var ex = Assert.Throws<ArgumentException>(() => MicroQRCodeGenerator.CreateMicroQRCode("123456789012345678901234567890123456", MicroQREccLevel.L));

        await Assert.That(ex.Message).Contains("too long for Micro QR");
        await Assert.That(ex.Message).Contains("36 digits");
        await Assert.That(ex.Message).Contains("35 digits");
        await Assert.That(ex.Message).Contains("M4");
    }

    [Test]
    public async Task CreateMicroQRCode_RequestedVersion_TooLong_MessageStatesVersionMaximum()
    {
        // 11 digits on M2-L (numeric capacity 10)
        var ex = Assert.Throws<ArgumentException>(() => MicroQRCodeGenerator.CreateMicroQRCode("12345678901", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M2 }));

        await Assert.That(ex.Message).Contains("too long for Micro QR M2");
        await Assert.That(ex.Message).Contains("11 digits");
        await Assert.That(ex.Message).Contains("10 digits");
        await Assert.That(ex.Message).Contains("QRCodeGenerator");
    }

    [Test]
    public async Task CreateMicroQRCode_AutoVersion_ModeUnsupportedAtEcc_MessageStatesConstraint()
    {
        // Alphanumeric at ErrorDetectionOnly: no version supports the combination at any length
        var ex = Assert.Throws<ArgumentException>(() => MicroQRCodeGenerator.CreateMicroQRCode("HELLO", MicroQREccLevel.ErrorDetectionOnly));

        await Assert.That(ex.Message).Contains("ErrorDetectionOnly");
        await Assert.That(ex.Message).Contains("M1");
        await Assert.That(ex.Message).Contains("QRCodeGenerator");
    }

    [Test]
    public async Task CreateMicroQRCode_RequestedVersion_UsesRequestedVersion()
    {
        var data = MicroQRCodeGenerator.CreateMicroQRCode("123", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M4 });
        await Assert.That(data.Version).IsEqualTo(MicroQRVersion.M4);
        await Assert.That(data.Size).IsEqualTo(17 + 2 * 2); // core 17 + default quiet zone 2 each side
    }

    // ---------------------------------------------------------------
    // Matrix structure invariants
    // ---------------------------------------------------------------

    [Test]
    public async Task CreateMicroQRCode_M2_MatrixStructure()
    {
        var data = MicroQRCodeGenerator.CreateMicroQRCode("01234567", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });

        await Assert.That(data.Version).IsEqualTo(MicroQRVersion.M2);
        await Assert.That(data.Size).IsEqualTo(13);

        // Finder pattern: outer ring dark, inner separator light, 3x3 center dark.
        await Assert.That(data[0, 0]).IsTrue();
        await Assert.That(data[0, 6]).IsTrue();
        await Assert.That(data[6, 0]).IsTrue();
        await Assert.That(data[1, 1]).IsFalse();
        await Assert.That(data[5, 5]).IsFalse();
        await Assert.That(data[3, 3]).IsTrue();

        // Separator: row 7 cols 0-7 and col 7 rows 0-7 are light.
        for (var i = 0; i <= 7; i++)
        {
            await Assert.That(data[7, i]).IsFalse();
            await Assert.That(data[i, 7]).IsFalse();
        }

        // Timing patterns: row 0 and column 0 from index 8, dark at even indices.
        for (var i = 8; i < 13; i++)
        {
            await Assert.That(data[0, i]).IsEqualTo(i % 2 == 0);
            await Assert.That(data[i, 0]).IsEqualTo(i % 2 == 0);
        }
    }

    [Test]
    [Arguments("12345", MicroQREccLevel.ErrorDetectionOnly)]
    [Arguments("01234567", MicroQREccLevel.L)]
    [Arguments("HELLO WORLD 14", MicroQREccLevel.L)]
    [Arguments("byte data", MicroQREccLevel.M)]
    [Arguments("bytes!!!!", MicroQREccLevel.Q)]
    public async Task CreateMicroQRCode_FormatInfo_RoundTripsFromMatrix(string text, MicroQREccLevel ecc)
    {
        var data = MicroQRCodeGenerator.CreateMicroQRCode(text, ecc, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });

        // Read the 15 placed format modules back (ISO order: bit14..bit8 along
        // row 8 cols 1..7, bit7 at (8,8), bits 6..0 down col 8 rows 7..1) and
        // compare against the expected pattern for every mask; exactly one mask
        // must match, proving format info placement is self-consistent.
        ushort placed = 0;
        for (var col = 1; col <= 8; col++)
        {
            placed = (ushort)((placed << 1) | (data[8, col] ? 1 : 0));
        }
        for (var row = 7; row >= 1; row--)
        {
            placed = (ushort)((placed << 1) | (data[row, 8] ? 1 : 0));
        }

        var matches = 0;
        for (var mask = 0; mask < 4; mask++)
        {
            if (MicroQRConstants.GetFormatBits(data.Version, ecc, mask) == placed)
            {
                matches++;
            }
        }
        await Assert.That(matches).IsEqualTo(1);
    }

    [Test]
    public async Task CreateMicroQRCode_QuietZone_IsLightAndSizedCorrectly()
    {
        var data = MicroQRCodeGenerator.CreateMicroQRCode("12345", MicroQREccLevel.ErrorDetectionOnly, new MicroQRCodeGeneratorOptions { QuietZoneSize = 2 });

        await Assert.That(data.Size).IsEqualTo(11 + 4);
        for (var i = 0; i < data.Size; i++)
        {
            await Assert.That(data[0, i]).IsFalse();
            await Assert.That(data[1, i]).IsFalse();
            await Assert.That(data[i, data.Size - 1]).IsFalse();
        }
        // Core top-left finder corner sits just inside the quiet zone.
        await Assert.That(data[2, 2]).IsTrue();
    }

    // ---------------------------------------------------------------
    // Span (zero-allocation) API parity with the class API
    // ---------------------------------------------------------------

    [Test]
    [Arguments("12345", MicroQREccLevel.ErrorDetectionOnly, 0)]
    [Arguments("01234567", MicroQREccLevel.L, 2)]
    [Arguments("byte data", MicroQREccLevel.M, 4)]
    public async Task CreateMicroQRCode_SpanApi_MatchesClassApi(string text, MicroQREccLevel ecc, int quietZone)
    {
        var data = MicroQRCodeGenerator.CreateMicroQRCode(text, ecc, new MicroQRCodeGeneratorOptions { QuietZoneSize = quietZone });

        var calculated = Sizing.Required(text.AsSpan(), ecc, quietZoneSize: quietZone);
        await Assert.That(calculated.QrSize).IsEqualTo(data.Size);

        var buffer = new byte[calculated.BufferSize];
        var written = MicroQRCodeGenerator.CreateMicroQRCode(text.AsSpan(), ecc, buffer, new MicroQRCodeGeneratorOptions { QuietZoneSize = quietZone });
        await Assert.That(written).IsEqualTo(calculated.BufferSize);

        for (var row = 0; row < data.Size; row++)
        {
            for (var col = 0; col < data.Size; col++)
            {
                var expected = data[row, col];
                var actual = buffer[row * data.Size + col] != 0;
                if (expected != actual)
                {
                    Assert.Fail($"Module mismatch at ({row},{col}): class={expected}, span={actual}");
                }
            }
        }
    }

    [Test]
    public async Task CreateMicroQRCode_SpanApi_ThrowsWhenBufferTooSmall()
    {
        var buffer = new byte[8];
        await Assert.That(() => MicroQRCodeGenerator.CreateMicroQRCode("123".AsSpan(), MicroQREccLevel.L, buffer)).Throws<ArgumentException>();
    }

    [Test]
    public async Task CreateMicroQRCode_IsDeterministic()
    {
        var first = MicroQRCodeGenerator.CreateMicroQRCode("HELLO WORLD 14", MicroQREccLevel.L);
        var second = MicroQRCodeGenerator.CreateMicroQRCode("HELLO WORLD 14", MicroQREccLevel.L);
        await Assert.That(first.GetRawData()).IsEquivalentTo(second.GetRawData());
    }
}
