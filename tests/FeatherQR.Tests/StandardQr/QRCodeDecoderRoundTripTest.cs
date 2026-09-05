namespace FeatherQR.Tests;

/// <summary>
/// Encode 驕ｶ鄙ｫ繝ｻdecode round-trip tests: every QR produced by <see cref="QRCodeGenerator"/>
/// must decode back to the original text with the library's own decoder.
/// </summary>
public class QRCodeDecoderRoundTripTest
{
    [Test]
    [Arguments("0123456789", ECCLevel.L)]
    [Arguments("0123456789012345678901234567890123456789", ECCLevel.H)]
    [Arguments("1", ECCLevel.M)]
    [Arguments("12", ECCLevel.Q)]
    public async Task RoundTrip_Numeric(string content, ECCLevel eccLevel)
        => await AssertRoundTrip(content, eccLevel);

    [Test]
    [Arguments("HELLO WORLD", ECCLevel.L)]
    [Arguments("ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:", ECCLevel.M)]
    [Arguments("A", ECCLevel.Q)]
    [Arguments("AC-42", ECCLevel.H)]
    public async Task RoundTrip_Alphanumeric(string content, ECCLevel eccLevel)
        => await AssertRoundTrip(content, eccLevel);

    [Test]
    [Arguments("Hello, World!", ECCLevel.L)]
    [Arguments("hello lowercase", ECCLevel.M)]
    [Arguments("https://example.com/path?query=value&x=1", ECCLevel.Q)]
    [Arguments("Café Zürich Résumé", ECCLevel.H)]
    public async Task RoundTrip_Byte(string content, ECCLevel eccLevel)
        => await AssertRoundTrip(content, eccLevel);

    [Test]
    [Arguments("こんにちは世界", ECCLevel.L)]
    [Arguments("你好世界", ECCLevel.M)]
    [Arguments("Привет мир", ECCLevel.Q)]
    [Arguments("🎉🎊🎈 emoji", ECCLevel.H)]
    public async Task RoundTrip_Utf8(string content, ECCLevel eccLevel)
        => await AssertRoundTrip(content, eccLevel, EciMode.Utf8);

    [Test]
    [Arguments("Café", ECCLevel.L)]
    [Arguments("Zürich", ECCLevel.M)]
    [Arguments("Naïve Résumé", ECCLevel.H)]
    public async Task RoundTrip_Iso8859_1_Eci(string content, ECCLevel eccLevel)
        => await AssertRoundTrip(content, eccLevel, EciMode.Iso8859_1);

    [Test]
    public async Task RoundTrip_Utf8WithBom()
    {
        var content = "BOM roundtrip 日本語";
        var qr = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, new QRCodeGeneratorOptions { Utf8BOM = true, EciMode = EciMode.Utf8 });

        await Assert.That(QRCodeDecoder.TryDecode(qr, out var decoded, out var info)).IsTrue();
        await Assert.That(decoded).IsEquivalentTo(content);
        await Assert.That(info.Status).IsEquivalentTo(QRCodeDecodeStatus.Success);
    }

    [Test]
    public async Task RoundTrip_AllVersions()
    {
        // Force every version; content sized to fit version 1 at ECC H
        var content = "V17!";
        for (var version = 1; version <= 40; version++)
        {
            foreach (var eccLevel in new[] { ECCLevel.L, ECCLevel.M, ECCLevel.Q, ECCLevel.H })
            {
                var qr = QRCodeGenerator.CreateQrCode(content, eccLevel, new QRCodeGeneratorOptions { Version = version });

                await Assert.That(QRCodeDecoder.TryDecode(qr, out var decoded, out var info)).IsTrue().Because($"version={version}, ecc={eccLevel}, status={info.Status}");
                await Assert.That(decoded).IsEqualTo(content);
                await Assert.That(info.Version).IsEqualTo(version);
                await Assert.That(info.EccLevel).IsEqualTo(eccLevel);
                await Assert.That(info.MaskPattern).IsBetween(0, 7);
                await Assert.That(info.ErrorsCorrected).IsEqualTo(0);
            }
        }
    }

    [Test]
    public async Task RoundTrip_VariousContents_CoversMultipleMaskPatterns()
    {
        // Mask selection depends on content; a spread of contents exercises
        // several of the 8 patterns through the decoder's unmasking path.
        var observedMasks = new HashSet<int>();
        for (var i = 0; i < 64; i++)
        {
            var content = $"mask-coverage-{i}-{new string((char)('a' + i % 26), i % 7 + 1)}";
            var qr = QRCodeGenerator.CreateQrCode(content, ECCLevel.M);

            await Assert.That(QRCodeDecoder.TryDecode(qr, out var decoded, out var info)).IsTrue();
            await Assert.That(decoded).IsEqualTo(content);
            observedMasks.Add(info.MaskPattern);
        }

        // Not all 8 are guaranteed, but a healthy spread proves per-pattern unmasking
        await Assert.That(observedMasks.Count >= 4).IsTrue().Because($"expected >= 4 distinct masks, observed: {string.Join(", ", observedMasks)}");
    }

    [Test]
    public async Task RoundTrip_SpanMatrix_WithQuietZone()
    {
        var content = "span with quiet zone";
        var calculated = Sizing.Required(content, ECCLevel.M);
        var buffer = new byte[calculated.BufferSize];
        var written = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, buffer);

        await Assert.That(QRCodeDecoder.TryDecode(buffer.AsSpan(0, written), calculated.QrSize, out var decoded, out var info)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
        await Assert.That(info.Status).IsEqualTo(QRCodeDecodeStatus.Success);
    }

    [Test]
    public async Task RoundTrip_SpanMatrix_WithoutQuietZone()
    {
        var content = "span without quiet zone";
        var calculated = Sizing.Required(content, ECCLevel.M, quietZoneSize: 0);
        var buffer = new byte[calculated.BufferSize];
        var written = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, buffer, new QRCodeGeneratorOptions { QuietZoneSize = 0 });

        await Assert.That(QRCodeDecoder.TryDecode(buffer.AsSpan(0, written), calculated.QrSize, out var decoded, out _)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    public async Task RoundTrip_CharSpanDestination_NoStringAllocation()
    {
        var content = "char span destination";
        var calculated = Sizing.Required(content, ECCLevel.M, quietZoneSize: 0);
        var buffer = new byte[calculated.BufferSize];
        QRCodeGenerator.CreateQrCode(content, ECCLevel.M, buffer, new QRCodeGeneratorOptions { QuietZoneSize = 0 });

        Span<char> destination = stackalloc char[QRCodeDecoder.GetMaxDecodedLength(calculated.Version)];
        var ok = QRCodeDecoder.TryDecode(buffer.AsSpan(0, calculated.BufferSize), calculated.QrSize, destination, out var charsWritten, out _);
        var decodedString = destination.Slice(0, charsWritten).ToString();
        await Assert.That(ok).IsTrue();
        await Assert.That(decodedString).IsEqualTo(content);
    }

#if !DEBUG
    [Test]
    public async Task Decode_CharSpanDestination_IsAllocationFree()
    {
        // Steady-state decode (span in, span out, no quiet zone) must not allocate.
        // Debug builds heap-allocate stackalloc initializers (see repo notes), so
        // this assertion is Release-only.
        var content = "0123456789";
        var calculated = Sizing.Required(content, ECCLevel.M, quietZoneSize: 0);
        var buffer = new byte[calculated.BufferSize];
        QRCodeGenerator.CreateQrCode(content, ECCLevel.M, buffer, quietZoneSize: 0);
        var destination = new char[QRCodeDecoder.GetMaxDecodedLength(calculated.Version)];

        // Warm-up: blocked-mask cache, ArrayPool buckets, JIT
        for (var i = 0; i < 3; i++)
        {
            QRCodeDecoder.TryDecode(buffer.AsSpan(0, calculated.BufferSize), calculated.QrSize, destination, out _, out _);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 16; i++)
        {
            QRCodeDecoder.TryDecode(buffer.AsSpan(0, calculated.BufferSize), calculated.QrSize, destination, out _, out _);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(allocated).IsEqualTo(0);
    }
#endif

    [Test]
    public async Task Decode_AllLightMatrix_ReturnsInvalidMatrix()
    {
        var modules = new byte[25 * 25];

        await Assert.That(QRCodeDecoder.TryDecode(modules, 25, out var text, out var info)).IsFalse();
        await Assert.That(text).IsEqualTo(string.Empty);
        await Assert.That(info.Status).IsEqualTo(QRCodeDecodeStatus.InvalidMatrix);
    }

    [Test]
    public async Task Decode_InvalidSize_ReturnsInvalidMatrix()
    {
        // 20x20 all-dark: bounding box is 20, not a valid QR dimension
        var modules = new byte[20 * 20];
        modules.AsSpan().Fill(1);

        await Assert.That(QRCodeDecoder.TryDecode(modules, 20, out _, out var info)).IsFalse();
        await Assert.That(info.Status).IsEqualTo(QRCodeDecodeStatus.InvalidMatrix);
    }

    [Test]
    public async Task Decode_CorruptedFormatInformation_ReturnsFormatInvalid()
    {
        var qr = QRCodeGenerator.CreateQrCode("format corruption", ECCLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 0 });
        var size = qr.Size;
        var modules = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                modules[y * size + x] = qr[y, x] ? (byte)1 : (byte)0;
            }
        }

        // Find a 15-bit pattern with Hamming distance > 3 from every valid masked
        // format pattern, then write it into both format copies. BCH(15,5) corrects
        // at most 3 bit errors, so this must be rejected.
        var farPattern = FindPatternFarFromAllFormats();
        WriteFormatPattern(modules, size, farPattern);

        await Assert.That(QRCodeDecoder.TryDecode(modules, size, out _, out var info)).IsFalse();
        await Assert.That(info.Status).IsEqualTo(QRCodeDecodeStatus.FormatInformationInvalid);
    }

    private static ushort FindPatternFarFromAllFormats()
    {
        for (var pattern = 0; pattern < 0x8000; pattern++)
        {
            var minDistance = int.MaxValue;
            for (var level = 0; level < 4; level++)
            {
                for (var mask = 0; mask < 8; mask++)
                {
                    var candidate = Internals.StandardQr.QRCodeConstants.GetFormatBits((ECCLevel)level, mask);
                    var distance = CountBits((ushort)(pattern ^ candidate));
                    if (distance < minDistance)
                        minDistance = distance;
                }
            }
            if (minDistance > 3)
                return (ushort)pattern;
        }
        throw new InvalidOperationException("no pattern found beyond correction distance");

        static int CountBits(ushort v)
        {
            var count = 0;
            while (v != 0)
            {
                count += v & 1;
                v >>= 1;
            }
            return count;
        }
    }

    private static async Task WriteFormatPattern(byte[] modules, int size, ushort pattern)
    {
        // Same positions as ModulePlacer.PlaceFormat (bit i, LSB first)
        var positions = new (int x1, int y1, int x2, int y2)[]
        {
            ( 8, 0, size - 1, 8 ),
            ( 8, 1, size - 2, 8 ),
            ( 8, 2, size - 3, 8 ),
            ( 8, 3, size - 4, 8 ),
            ( 8, 4, size - 5, 8 ),
            ( 8, 5, size - 6, 8 ),
            ( 8, 7, size - 7, 8 ),
            ( 8, 8, size - 8, 8 ),
            ( 7, 8, 8, size - 7 ),
            ( 5, 8, 8, size - 6 ),
            ( 4, 8, 8, size - 5 ),
            ( 3, 8, 8, size - 4 ),
            ( 2, 8, 8, size - 3 ),
            ( 1, 8, 8, size - 2 ),
            ( 0, 8, 8, size - 1 ),
        };

        for (var i = 0; i < 15; i++)
        {
            var bit = (byte)((pattern >> i) & 1);
            var (x1, y1, x2, y2) = positions[i];
            modules[y1 * size + x1] = bit;
            modules[y2 * size + x2] = bit;
        }
    }

    [Test]
    public async Task Decode_FewFlippedModules_IsCorrectedByEcc()
    {
        // Version 1-M has a single block with 10 ECC codewords 驕ｶ鄙ｫ繝ｻcorrects 5 codewords.
        var content = "ECCFIX";
        var qr = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, new QRCodeGeneratorOptions { Version = 1, QuietZoneSize = 0 });
        var size = qr.Size;
        var modules = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                modules[y * size + x] = qr[y, x] ? (byte)1 : (byte)0;
            }
        }

        // Flip 3 modules inside the data region (away from function patterns:
        // rows 9-12, columns 9-12 are data area in version 1).
        modules[9 * size + 9] ^= 1;
        modules[10 * size + 11] ^= 1;
        modules[12 * size + 10] ^= 1;

        await Assert.That(QRCodeDecoder.TryDecode(modules, size, out var decoded, out var info)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
        await Assert.That(info.ErrorsCorrected > 0).IsTrue().Because("expected ECC to correct at least one codeword");
    }

    [Test]
    public async Task Decode_HeavilyCorruptedData_ReturnsDataUncorrectable()
    {
        // Version 1-L corrects only 3 codewords; flipping a large scattered set
        // of data modules must exceed capacity and fail (not misdecode).
        var qr = QRCodeGenerator.CreateQrCode("FAIL", ECCLevel.L, new QRCodeGeneratorOptions { Version = 1, QuietZoneSize = 0 });
        var size = qr.Size;
        var modules = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                modules[y * size + x] = qr[y, x] ? (byte)1 : (byte)0;
            }
        }

        for (var y = 9; y <= 12; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (x == 6)
                    continue; // timing column
                modules[y * size + x] ^= 1;
            }
        }

        await Assert.That(QRCodeDecoder.TryDecode(modules, size, out _, out var info)).IsFalse();
        await Assert.That(info.Status).IsEqualTo(QRCodeDecodeStatus.DataUncorrectable);
    }

    [Test]
    public async Task TryDecode_NullData_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => QRCodeDecoder.TryDecode((QRCodeData)null!, out _));
    }

    [Test]
    public async Task GetMaxDecodedLength_InvalidVersion_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QRCodeDecoder.GetMaxDecodedLength(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => QRCodeDecoder.GetMaxDecodedLength(41));
    }

    [Test]
    [Arguments(0)]
    [Arguments(2)]
    [Arguments(4)]
    [Arguments(10)]
    public async Task RoundTrip_QuietZoneSizes(int quietZoneSize)
    {
        var content = "quiet zone variations";
        var qr = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = quietZoneSize });

        await Assert.That(QRCodeDecoder.TryDecode(qr, out var decoded, out _)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    private static async Task AssertRoundTrip(string content, ECCLevel eccLevel, EciMode eciMode = EciMode.Default)
    {
        var qr = QRCodeGenerator.CreateQrCode(content, eccLevel, new QRCodeGeneratorOptions { EciMode = eciMode });

        await Assert.That(QRCodeDecoder.TryDecode(qr, out var decoded, out var info)).IsTrue().Because($"decode failed: status={info.Status}, version={info.Version}");
        await Assert.That(decoded).IsEqualTo(content);
        await Assert.That(info.Status).IsEqualTo(QRCodeDecodeStatus.Success);
        await Assert.That(info.EccLevel).IsEqualTo(eccLevel);
    }
}
