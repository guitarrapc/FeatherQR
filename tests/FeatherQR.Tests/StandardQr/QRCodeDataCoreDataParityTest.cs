namespace FeatherQR.Tests;

/// <summary>
/// <see cref="QRCodeData.GetCoreData"/> against the per-module read (<see cref="QRCodeData.GetCoreModule"/>), for every version.
/// The bulk unpack goes through a vector kernel that stores whole words; a symbol's module
/// count is never a multiple of 8, so the last few modules and the byte after them are the
/// cases a kernel gets wrong.
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
}
