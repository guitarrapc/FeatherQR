using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="QRCodeData.GetCoreData"/> against the per-module read (<see cref="QRCodeData.GetCoreModule"/>), for every version.
/// The bulk unpack goes through a vector kernel that stores whole words; a symbol's module
/// count is never a multiple of 8, so the last few modules and the byte after them are the
/// cases a kernel gets wrong. <see cref="QRCodeData.SetCoreData"/> against an MSB-first pack
/// written from the definition, over a payload that was all dark before, so a pack that left
/// a module bit of the earlier one would show. The padding bits are held to zero by the
/// comparison with the expected bytes.
/// </summary>
public class QRCodeDataCoreDataParityTest
{
    public static IEnumerable<int> AllVersions() => Enumerable.Range(1, 40);

    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task GetCoreData_MatchesPerModuleRead_AndWritesNothingPast(int version)
    {
        var size = QRCodeData.SizeFromVersion(version);
        var source = new byte[size * size];
        var state = (uint)version * 2654435761u + 7u;
        for (var i = 0; i < source.Length; i++)
        {
            state = state * 1664525u + 1013904223u;
            source[i] = (byte)(state >> 16 & 1);
        }
        var qr = new QRCodeData(version, 4);
        qr.SetCoreData(source);

        var expected = new byte[size * size];
        for (var row = 0; row < size; row++)
        {
            for (var col = 0; col < size; col++)
                expected[row * size + col] = qr.GetCoreModule(row, col) ? (byte)1 : (byte)0;
        }

        // exact-size window inside a larger sentinel buffer: no slack for a vector store to hide in
        var backing = new byte[size * size + 64];
        backing.AsSpan().Fill(0xA5);
        qr.GetCoreData(backing.AsSpan(0, size * size));

        await Assert.That(backing.AsSpan(0, size * size).SequenceEqual(expected)).IsTrue()
            .Because($"v{version}: unpacked modules differ from the per-module read");
        await Assert.That(backing.AsSpan(size * size).IndexOfAnyExcept((byte)0xA5)).IsEqualTo(-1)
            .Because($"v{version}: wrote past the {size * size}-byte destination");
    }

    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task SetCoreData_PacksMsbFirst_AndReplacesWhatWasThere(int version)
    {
        // The payload of GetRawData is the packed core, row-major and MSB first, its padding bits zero. A second matrix set over the
        // first must leave nothing of it: all dark first, then random, so a merge would show.
        var size = QRCodeData.SizeFromVersion(version);
        var source = new byte[size * size];
        var state = (uint)version * 40503u + 11u;
        for (var i = 0; i < source.Length; i++)
        {
            state = state * 1664525u + 1013904223u;
            source[i] = (byte)(state >> 16 & 1);
        }
        var expected = new byte[(source.Length + 7) / 8];
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] != 0)
                expected[i >> 3] |= (byte)(0x80 >> (i & 7));
        }

        var qr = new QRCodeData(version, 0);
        qr.SetCoreData(Enumerable.Repeat((byte)1, source.Length).ToArray());
        qr.SetCoreData(source);

        var raw = qr.GetRawData();
        await Assert.That(raw.AsSpan(4).ToArray()).IsEquivalentTo(expected, CollectionOrdering.Matching).Because($"v{version}");
    }
}
