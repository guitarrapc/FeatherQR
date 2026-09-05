using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="RmQRModulePlacer.PlaceSymbol"/> (fast path: cached per-version tables,
/// vector bit expansion, pair stores + scatter) versus
/// <see cref="RmQRModulePlacer.PlaceSymbolReference"/> (per-module painters), byte for
/// byte, for every version × ECC level over all-zero, all-one, pseudo-random and
/// over-long messages, on a dirty core (every module must be written), plus the
/// undersized-buffer contracts of the fast path.
/// </summary>
public class RmQRModulePlacerParityTest
{
    public static IEnumerable<(RmQRVersion version, RmQREccLevel ecc)> AllVersionEcc()
    {
        foreach (var v in Enum.GetValues<RmQRVersion>())
        {
            yield return (v, RmQREccLevel.M);
            yield return (v, RmQREccLevel.H);
        }
    }

    private static byte[] PseudoRandom(int length, int seed)
    {
        var bytes = new byte[length];
        var state = (uint)seed * 2654435761u + 7u;
        for (var i = 0; i < length; i++)
        {
            state = state * 1664525u + 1013904223u;
            bytes[i] = (byte)(state >> 16);
        }
        return bytes;
    }

    /// <summary>
    /// The ARM64 store tier (transpose blocks + row runs + single scatter) against the
    /// per-module reference, forced on rather than auto-selected, for every version ×
    /// ECC × message shape, on a dirty core and through both the tight and the strided
    /// (quiet-zoned) destination — the strided path derives its own row pitch, so a
    /// tier that is byte-exact at stride == width can still be wrong at stride > width.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(AllVersionEcc))]
    public async Task NeonKernel_MatchesReference_EveryMessageShape(RmQRVersion version, RmQREccLevel ecc)
    {
        if (!RmQRModulePlacer.IsNeonTierSupported)
        {
            Skip.Test("No ARM64 store tier on this machine");
            return;
        }

        var width = RmQRConstants.GetWidth(version);
        var height = RmQRConstants.GetHeight(version);
        var total = RmQRConstants.GetTotalCodewordCount(version);
        var size = width * height;
        var messages = new[]
        {
            new byte[total],
            Enumerable.Repeat((byte)0xFF, total).ToArray(),
            PseudoRandom(total, (int)version * 31 + (int)ecc),
            PseudoRandom(total, (int)version * 17 + (int)ecc + 5),
            PseudoRandom(total + 3, (int)version + (int)ecc),
        };

        foreach (var message in messages)
        {
            var expected = new byte[size];
            Array.Fill(expected, (byte)0xA5);
            RmQRModulePlacer.PlaceSymbolReference(expected, version, ecc, message);

            var tight = new byte[size];
            Array.Fill(tight, (byte)0xA5);
            RmQRModulePlacer.PlaceSymbol(tight, width, version, ecc, message, RmQRModulePlacer.PlaceKernel.Neon);
            await Assert.That(tight).IsEquivalentTo(expected);

            // Strided: 4 modules of quiet zone on every side; the bytes between rows
            // must survive untouched.
            const int Quiet = 4;
            var stride = width + 2 * Quiet;
            var padded = new byte[stride * (height + 2 * Quiet)];
            Array.Fill(padded, (byte)0x5A);
            RmQRModulePlacer.PlaceSymbol(padded.AsSpan(Quiet * stride + Quiet), stride, version, ecc, message, RmQRModulePlacer.PlaceKernel.Neon);
            for (var row = 0; row < height; row++)
            {
                var actualRow = padded.AsSpan((Quiet + row) * stride + Quiet, width).ToArray();
                await Assert.That(actualRow).IsEquivalentTo(expected.AsSpan(row * width, width).ToArray());
            }
            for (var row = 0; row < height; row++)
            {
                for (var q = 0; q < Quiet; q++)
                {
                    await Assert.That(padded[(Quiet + row) * stride + q]).IsEqualTo((byte)0x5A);
                    await Assert.That(padded[(Quiet + row) * stride + Quiet + width + q]).IsEqualTo((byte)0x5A);
                }
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(AllVersionEcc))]
    public async Task FastPath_MatchesReference_EveryMessageShape(RmQRVersion version, RmQREccLevel ecc)
    {
        var coreWidth = RmQRConstants.GetWidth(version);
        var coreHeight = RmQRConstants.GetHeight(version);
        var total = RmQRConstants.GetTotalCodewordCount(version);
        var size = RmQRConstants.GetWidth(version) * RmQRConstants.GetHeight(version);
        var messages = new[]
        {
            new byte[total],
            Enumerable.Repeat((byte)0xFF, total).ToArray(),
            PseudoRandom(total, (int)version * 31 + (int)ecc),
            PseudoRandom(total, (int)version * 17 + (int)ecc + 5),
            PseudoRandom(total + 3, (int)version + (int)ecc), // over-long: only the first `total` codewords count
            PseudoRandom(RmQRCodewordEncoder.GetFinalMessageSize(version), 99 + (int)version), // the generator's buffer size
        };
        foreach (var message in messages)
        {
            var expected = new byte[size];
            expected.AsSpan().Fill(0xA5);
            RmQRModulePlacer.PlaceSymbolReference(expected, version, ecc, message);

            // Pinned Portable, not the automatic overload: on ARM64 `Auto` is the NEON
            // tier, so without this the portable pair-store + index scatter would be
            // exercised on x64 legs only.
            var portable = new byte[size];
            portable.AsSpan().Fill(0xA5);
            RmQRModulePlacer.PlaceSymbol(portable, coreWidth, version, ecc, message, RmQRModulePlacer.PlaceKernel.Portable);
            await Assert.That(portable).IsEquivalentTo(expected);

            // Strided as well as tight. `ScatterPairs` branches on `strided` and derives
            // its own row pitch, so the two arms are different code; pinning only the
            // tight one leaves the strided portable arm unexercised wherever `Auto` picks
            // a vector tier, which on ARM64 is everywhere.
            const int PortableQuiet = 3;
            var portableStride = coreWidth + 2 * PortableQuiet;
            var portablePadded = new byte[portableStride * (coreHeight + 2 * PortableQuiet)];
            portablePadded.AsSpan().Fill(0x5A);
            RmQRModulePlacer.PlaceSymbol(portablePadded.AsSpan(PortableQuiet * portableStride + PortableQuiet), portableStride, version, ecc, message, RmQRModulePlacer.PlaceKernel.Portable);
            for (var row = 0; row < coreHeight; row++)
            {
                await Assert.That(portablePadded.AsSpan((PortableQuiet + row) * portableStride + PortableQuiet, coreWidth).ToArray())
                    .IsEquivalentTo(expected.AsSpan(row * coreWidth, coreWidth).ToArray());
                for (var q = 0; q < PortableQuiet; q++)
                {
                    await Assert.That(portablePadded[(PortableQuiet + row) * portableStride + q]).IsEqualTo((byte)0x5A);
                    await Assert.That(portablePadded[(PortableQuiet + row) * portableStride + PortableQuiet + coreWidth + q]).IsEqualTo((byte)0x5A);
                }
            }

            var actual = new byte[size + 3]; // oversized core: only the first w×h bytes are written
            actual.AsSpan().Fill(0xA5);
            RmQRModulePlacer.PlaceSymbol(actual, version, ecc, message);

            if (!actual.AsSpan(0, size).SequenceEqual(expected))
            {
                var first = 0;
                while (first < size && actual[first] == expected[first]) first++;
                var width = RmQRConstants.GetWidth(version);
                Assert.Fail($"{version}-{ecc} msg[0]={message[0]:X2}: first mismatch at {first} (row {first / width}, col {first % width}): expected {expected[first]}, actual {actual[first]}");
            }
            await Assert.That(actual.AsSpan(size).ToArray()).IsEquivalentTo(new byte[] { 0xA5, 0xA5, 0xA5 });
        }
    }

    [Test]
    public async Task FastPath_RejectsUndersizedBuffers_LikeReference()
    {
        var message = new byte[RmQRCodewordEncoder.GetFinalMessageSize(RmQRVersion.R17x139)];
        await Assert.That(() => RmQRModulePlacer.PlaceSymbol(new byte[17 * 139 - 1], RmQRVersion.R17x139, RmQREccLevel.M, message)).Throws<ArgumentException>();
        await Assert.That(() => RmQRModulePlacer.PlaceSymbol(new byte[17 * 139], RmQRVersion.R17x139, RmQREccLevel.M, message.AsSpan(0, RmQRConstants.GetTotalCodewordCount(RmQRVersion.R17x139) - 1).ToArray())).Throws<ArgumentException>();
        await Assert.That(() => RmQRModulePlacer.PlaceSymbolReference(new byte[17 * 139 - 1], RmQRVersion.R17x139, RmQREccLevel.M, message)).Throws<ArgumentException>();
    }

    [Test]
    public async Task UndersizedBuffers_NameTheCallersParameter()
    {
        // the core overload must report `core` (its own parameter), the strided overload `destination`
        var message = new byte[RmQRCodewordEncoder.GetFinalMessageSize(RmQRVersion.R7x43)];
        var coreError = Assert.Throws<ArgumentException>(() => RmQRModulePlacer.PlaceSymbol(new byte[7 * 43 - 1], RmQRVersion.R7x43, RmQREccLevel.M, message));
        await Assert.That(coreError.ParamName).IsEqualTo("core");
        var stridedError = Assert.Throws<ArgumentException>(() => RmQRModulePlacer.PlaceSymbol(new byte[50 * 6 + 42], 50, RmQRVersion.R7x43, RmQREccLevel.M, message));
        await Assert.That(stridedError.ParamName).IsEqualTo("destination");
        var referenceError = Assert.Throws<ArgumentException>(() => RmQRModulePlacer.PlaceSymbolReference(new byte[7 * 43 - 1], RmQRVersion.R7x43, RmQREccLevel.M, message));
        await Assert.That(referenceError.ParamName).IsEqualTo("core");
    }

    [Test]
    public async Task FastPath_IsRepeatable_AcrossEccAndVersionsSharingTables()
    {
        // the per-version cache serves both ECC levels and repeated calls; alternate them
        var v = RmQRVersion.R11x59;
        var total = RmQRConstants.GetTotalCodewordCount(v);
        var size = RmQRConstants.GetWidth(v) * RmQRConstants.GetHeight(v);
        for (var i = 0; i < 6; i++)
        {
            var ecc = (i & 1) == 0 ? RmQREccLevel.M : RmQREccLevel.H;
            var message = PseudoRandom(total, i);
            var expected = new byte[size];
            RmQRModulePlacer.PlaceSymbolReference(expected, v, ecc, message);
            var actual = new byte[size];
            RmQRModulePlacer.PlaceSymbol(actual, v, ecc, message);
            await Assert.That(actual.AsSpan().SequenceEqual(expected)).IsTrue();
        }
    }
}

public class RmQRModulePlacerStridedParityTest
{
    public static IEnumerable<(RmQRVersion version, RmQREccLevel ecc)> AllVersionEcc() => RmQRModulePlacerParityTest.AllVersionEcc();

    private static byte[] PseudoRandom(int length, int seed)
    {
        var bytes = new byte[length];
        var state = (uint)seed * 2654435761u + 7u;
        for (var i = 0; i < length; i++)
        {
            state = state * 1664525u + 1013904223u;
            bytes[i] = (byte)(state >> 16);
        }
        return bytes;
    }

    /// <summary>
    /// The strided overload writes the core into a wider destination (rows `stride`
    /// apart, e.g. a quiet-zoned buffer) and touches nothing else: every row equals
    /// the reference core row, the gap bytes and the tail keep their poison.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(AllVersionEcc))]
    public async Task StridedPlacement_MatchesReferenceRows_LeavesGapsUntouched(RmQRVersion version, RmQREccLevel ecc)
    {
        var width = RmQRConstants.GetWidth(version);
        var height = RmQRConstants.GetHeight(version);
        var message = PseudoRandom(RmQRConstants.GetTotalCodewordCount(version), (int)version * 3 + (int)ecc);
        var expected = new byte[width * height];
        RmQRModulePlacer.PlaceSymbolReference(expected, version, ecc, message);

        foreach (var stride in new[] { width, width + 4, width + 13 })
        {
            var buffer = new byte[stride * height + 5];
            buffer.AsSpan().Fill(0xA5);
            RmQRModulePlacer.PlaceSymbol(buffer, stride, version, ecc, message);
            for (var row = 0; row < height; row++)
            {
                if (!buffer.AsSpan(row * stride, width).SequenceEqual(expected.AsSpan(row * width, width)))
                    Assert.Fail($"{version}-{ecc} stride {stride}: row {row} differs");
                for (var c = width; c < stride; c++)
                    if (buffer[row * stride + c] != 0xA5)
                        Assert.Fail($"{version}-{ecc} stride {stride}: gap byte written at row {row} col {c}");
            }
            await Assert.That(buffer.AsSpan(stride * height).ToArray()).IsEquivalentTo(new byte[] { 0xA5, 0xA5, 0xA5, 0xA5, 0xA5 });
        }
    }

    [Test]
    public async Task StridedPlacement_RejectsStrideBelowWidth_AndShortBuffers()
    {
        var message = new byte[RmQRCodewordEncoder.GetFinalMessageSize(RmQRVersion.R7x43)];
        await Assert.That(() => RmQRModulePlacer.PlaceSymbol(new byte[50 * 7], 42, RmQRVersion.R7x43, RmQREccLevel.M, message)).Throws<ArgumentException>();
        await Assert.That(() => RmQRModulePlacer.PlaceSymbol(new byte[50 * 6 + 42], 50, RmQRVersion.R7x43, RmQREccLevel.M, message)).Throws<ArgumentException>();
        // exactly enough: last row needs only `width` bytes
        RmQRModulePlacer.PlaceSymbol(new byte[50 * 6 + 43], 50, RmQRVersion.R7x43, RmQREccLevel.M, message);
    }
}
