using System.Buffers;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="RmQRCodeData"/>: rectangular bit-packed core with a virtual quiet
/// zone, and the "QRX" symbol-type-2 serialization container. No encoder exists
/// yet, so matrices come from the committed external-encoder corpus (Fixtures/RmQr)
/// and from synthetic patterns.
/// </summary>
public class RmQRCodeDataUnitTest
{
    public static IEnumerable<RmQRVersion> AllVersions() => Enum.GetValues<RmQRVersion>();

    private static (byte[] Modules, int Width, int Height, RmQRVersion Version) LoadFixture(string id)
    {
        var fixture = FixtureLoader.Load("RmQr", id);
        var (modules, width, height) = FixtureLoader.ReadRectangularMatrix(fixture.MatrixPath);
        RmQRConstants.TryGetVersion(height, width, out var version);
        return (modules, width, height, version);
    }

    /// <summary>Deterministic pseudo-random core so every version is exercised without an encoder.</summary>
    private static byte[] SyntheticCore(RmQRVersion version, int seed)
    {
        var w = RmQRConstants.GetWidth(version);
        var h = RmQRConstants.GetHeight(version);
        var modules = new byte[w * h];
        var state = (uint)(seed * 2654435761u + 12345u);
        for (var i = 0; i < modules.Length; i++)
        {
            state = state * 1664525u + 1013904223u;
            modules[i] = (byte)(state >> 31);
        }
        return modules;
    }

    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task Constructor_ExposesRectangularDimensions_WithQuietZone(RmQRVersion version)
    {
        var data = new RmQRCodeData(version, quietZoneSize: 2);

        await Assert.That(data.Version).IsEqualTo(version);
        await Assert.That(data.Width).IsEqualTo(RmQRConstants.GetWidth(version) + 4);
        await Assert.That(data.Height).IsEqualTo(RmQRConstants.GetHeight(version) + 4);
        await Assert.That(data.GetCoreWidth()).IsEqualTo(RmQRConstants.GetWidth(version));
        await Assert.That(data.GetCoreHeight()).IsEqualTo(RmQRConstants.GetHeight(version));

        // Fresh matrix is all light, including the quiet zone.
        for (var row = 0; row < data.Height; row++)
            for (var col = 0; col < data.Width; col++)
                if (data[row, col])
                    Assert.Fail($"fresh matrix must be light at ({row},{col})");
    }

    [Test]
    public async Task Constructor_RejectsInvalidVersionAndQuietZone()
    {
        await Assert.That(() => new RmQRCodeData((RmQRVersion)0, 2)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new RmQRCodeData((RmQRVersion)33, 2)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new RmQRCodeData(RmQRVersion.R7x43, -1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new RmQRCodeData(RmQRVersion.R7x43, 10_001)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task SetCoreData_GetCoreData_RoundTripsEveryVersion(RmQRVersion version)
    {
        var data = new RmQRCodeData(version, 0);
        var source = SyntheticCore(version, (int)version);
        data.SetCoreData(source);

        var back = new byte[source.Length];
        data.GetCoreData(back);
        await Assert.That(back).IsEquivalentTo(source);

        // Indexer and GetCoreModule agree with the byte-per-module view.
        var w = data.GetCoreWidth();
        for (var i = 0; i < source.Length; i++)
        {
            var row = i / w;
            var col = i % w;
            var expected = source[i] != 0;
            if (data[row, col] != expected || data.GetCoreModule(row, col) != expected)
                Assert.Fail($"module ({row},{col}) mismatch for {version}");
        }
    }

    [Test]
    public async Task SetCoreData_SecondCall_ReplacesPreviousMatrix()
    {
        var data = new RmQRCodeData(RmQRVersion.R7x43, 0);
        var allDark = new byte[7 * 43];
        Array.Fill(allDark, (byte)1);
        data.SetCoreData(allDark);
        data.SetCoreData(new byte[7 * 43]);

        for (var row = 0; row < 7; row++)
            for (var col = 0; col < 43; col++)
                if (data[row, col])
                    Assert.Fail($"Dark module leaked from the previous SetCoreData at ({row},{col})");
        await Assert.That(data[0, 0]).IsFalse();
    }

    [Test]
    public async Task SetCoreData_GetCoreData_RejectWrongSizes()
    {
        var data = new RmQRCodeData(RmQRVersion.R7x43, 0);
        await Assert.That(() => data.SetCoreData(new byte[43 * 7 - 1])).Throws<ArgumentException>();
        await Assert.That(() => data.SetCoreData(new byte[43 * 7 + 1])).Throws<ArgumentException>();
        await Assert.That(() => data.GetCoreData(new byte[43 * 7 - 1])).Throws<ArgumentException>();
        // Transposed dimensions have the same module count: accepted as a flat buffer (row-major over the true width).
        await Assert.That(() => data.SetCoreData(new byte[7 * 43])).ThrowsNothing();
    }

    [Test]
    public async Task Indexer_QuietZoneReadsLight_CoreOffsetByQuietZone_OutOfRangeThrows()
    {
        var (modules, width, height, version) = LoadFixture("zint-libzint/r7x43-m-numeric-1");
        var data = new RmQRCodeData(version, quietZoneSize: 3);
        data.SetCoreData(modules);

        await Assert.That(data.Width).IsEqualTo(width + 6);
        await Assert.That(data.Height).IsEqualTo(height + 6);
        await Assert.That(data[0, 0]).IsFalse();                              // quiet zone
        await Assert.That(data[3, 3]).IsTrue();                               // core (0,0) = finder corner
        await Assert.That(data[3 + height - 1, 3 + width - 1]).IsTrue();      // core (h-1,w-1) = sub-finder corner
        await Assert.That(data[3 + height, 3]).IsFalse();                     // bottom quiet zone
        await Assert.That(() => data[height + 6, 0]).Throws<IndexOutOfRangeException>();
        await Assert.That(() => data[0, width + 6]).Throws<IndexOutOfRangeException>();
        await Assert.That(() => data[-1, 0]).Throws<IndexOutOfRangeException>();

        // Every core module matches the fixture through the quiet-zone-offset indexer.
        for (var row = 0; row < height; row++)
            for (var col = 0; col < width; col++)
                if (data[row + 3, col + 3] != (modules[row * width + col] != 0))
                    Assert.Fail($"module ({row},{col}) mismatch");
    }

    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task GetRawData_RoundTripsThroughConstructor_EveryVersion(RmQRVersion version)
    {
        var original = new RmQRCodeData(version, quietZoneSize: 2);
        original.SetCoreData(SyntheticCore(version, 100 + (int)version));

        var raw = original.GetRawData();
        await Assert.That(raw.Length).IsEqualTo(original.GetRawDataSize());

        var restored = new RmQRCodeData(raw, quietZoneSize: 1);   // quiet zone is independent of the serialized data
        await Assert.That(restored.Version).IsEqualTo(version);
        await Assert.That(restored.Width).IsEqualTo(original.GetCoreWidth() + 2);
        await Assert.That(restored.Height).IsEqualTo(original.GetCoreHeight() + 2);

        var expected = new byte[original.GetCoreWidth() * original.GetCoreHeight()];
        var actual = new byte[expected.Length];
        original.GetCoreData(expected);
        restored.GetCoreData(actual);
        await Assert.That(actual).IsEquivalentTo(expected);
    }

    [Test]
    public async Task GetRawData_HeaderIsQrxWithSymbolType2AndDimensions()
    {
        var (modules, width, height, version) = LoadFixture("qrtool/r17x139-m-alphanumeric-max");
        var data = new RmQRCodeData(version, 2);
        data.SetCoreData(modules);
        var raw = data.GetRawData();

        // "QRX" magic, symbol type 2 (rMQR), width, height, then packed core bits (MSB-first, row-major).
        await Assert.That(raw[0]).IsEqualTo((byte)0x51);
        await Assert.That(raw[1]).IsEqualTo((byte)0x52);
        await Assert.That(raw[2]).IsEqualTo((byte)0x58);
        await Assert.That(raw[3]).IsEqualTo((byte)2);
        await Assert.That(raw[4]).IsEqualTo((byte)139);
        await Assert.That(raw[5]).IsEqualTo((byte)17);
        await Assert.That(raw.Length).IsEqualTo(6 + (139 * 17 + 7) / 8);
        await Assert.That(raw[6] >> 7).IsEqualTo(1); // core (0,0) finder corner is dark

        // Padding bits of the final byte are canonical zero.
        var remainder = (139 * 17) & 7;
        await Assert.That(raw[^1] & (0xFF >> remainder)).IsEqualTo(0);
    }

    [Test]
    public async Task GetRawData_BufferWriter_MatchesArrayOverload()
    {
        var (modules, _, _, version) = LoadFixture("zint-libzint/r11x27-m-numeric-1");
        var data = new RmQRCodeData(version, 2);
        data.SetCoreData(modules);

        var writer = new ArrayBufferWriter<byte>();
        var written = data.GetRawData(writer);

        await Assert.That(written).IsEqualTo(data.GetRawDataSize());
        await Assert.That(writer.WrittenSpan.ToArray()).IsEquivalentTo(data.GetRawData());
    }

    [Test]
    public async Task Constructor_Deserialize_RejectsInvalidHeaders()
    {
        var data = new RmQRCodeData(RmQRVersion.R11x27, 0);
        data.SetCoreData(SyntheticCore(RmQRVersion.R11x27, 7));
        var valid = data.GetRawData();

        var badMagic = (byte[])valid.Clone();
        badMagic[2] = (byte)'R'; // "QRR" is the Standard QR container
        await Assert.That(() => new RmQRCodeData(badMagic, 0)).Throws<InvalidDataException>();

        var microType = (byte[])valid.Clone();
        microType[3] = 1; // Micro QR symbol type must be rejected by the rMQR reader
        await Assert.That(() => new RmQRCodeData(microType, 0)).Throws<InvalidDataException>();

        var transposed = (byte[])valid.Clone();
        (transposed[4], transposed[5]) = (transposed[5], transposed[4]); // 11 wide × 27 high is not an rMQR size
        await Assert.That(() => new RmQRCodeData(transposed, 0)).Throws<InvalidDataException>();

        var badSize = (byte[])valid.Clone();
        badSize[4] = 28;
        await Assert.That(() => new RmQRCodeData(badSize, 0)).Throws<InvalidDataException>();

        var truncated = valid.AsSpan(0, valid.Length - 1).ToArray();
        await Assert.That(() => new RmQRCodeData(truncated, 0)).Throws<InvalidOperationException>();

        await Assert.That(() => new RmQRCodeData(valid.AsSpan(0, 5).ToArray(), 0)).Throws<InvalidDataException>();
        await Assert.That(() => new RmQRCodeData(valid, -1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new RmQRCodeData(valid, 10_001)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Constructor_Deserialize_CanonicalizesPaddingBits()
    {
        var data = new RmQRCodeData(RmQRVersion.R7x43, 0);
        data.SetCoreData(SyntheticCore(RmQRVersion.R7x43, 3));
        var raw = data.GetRawData();

        // 301 bits → 38 bytes, 3 padding bits; dirty padding must not survive a round trip.
        var dirty = (byte[])raw.Clone();
        dirty[^1] |= 0x07;
        var restored = new RmQRCodeData(dirty, 0);
        await Assert.That(restored.GetRawData()).IsEquivalentTo(raw);
    }

    [Test]
    public async Task MicroQrContainer_IsRejected_ByRmqrReader_AndViceVersa()
    {
        var micro = MicroQRCodeGenerator.Create("123", MicroQREccLevel.L).GetRawData();
        await Assert.That(() => new RmQRCodeData(micro, 0)).Throws<InvalidDataException>();

        var rmqr = new RmQRCodeData(RmQRVersion.R7x43, 0).GetRawData();
        await Assert.That(() => new MicroQRCodeData(rmqr, 0)).Throws<InvalidDataException>();
    }

#if !DEBUG
    [Test]
    public async Task CoreAccessors_AreAllocationFree()
    {
        // Steady-state SetCoreData / GetCoreData / GetCoreModule / GetRawData(IBufferWriter)
        // must not allocate (Debug builds are excluded per repo notes).
        var (modules, width, height, version) = LoadFixture("zint-libzint/r17x139-m-numeric-max");
        var data = new RmQRCodeData(version, 2);
        var back = new byte[modules.Length];
        var writer = new ArrayBufferWriter<byte>(data.GetRawDataSize() * 4);

        for (var i = 0; i < 3; i++)
        {
            data.SetCoreData(modules);
            data.GetCoreData(back);
            writer.Clear();
            data.GetRawData(writer);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var dark = 0;
        for (var i = 0; i < 16; i++)
        {
            data.SetCoreData(modules);
            data.GetCoreData(back);
            for (var row = 0; row < height; row++)
                for (var col = 0; col < width; col++)
                    if (data.GetCoreModule(row, col))
                        dark++;
            writer.Clear();
            data.GetRawData(writer);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(allocated).IsEqualTo(0);
        await Assert.That(dark).IsGreaterThan(0);
    }
#endif
}
