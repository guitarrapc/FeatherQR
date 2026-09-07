using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// The table-driven Standard QR placer (<see cref="ModulePlacer.GetLayout"/>:
/// cached function template + blocked mask, and
/// <see cref="ModulePlacer.PlaceDataWords(Span{byte}, ModulePlacer.PlacementLayout, ReadOnlySpan{byte})"/>
/// with its expand + run-store pass) versus the per-module references
/// (<see cref="QRCodeGenerator.PlaceFunctionModulesReference"/> and the walk-based
/// <see cref="ModulePlacer.PlaceDataWords(Span{byte}, int, ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>),
/// byte for byte, for every version 1..40 over full / over-long / short / empty
/// streams and several seeds, on dirty buffers.
/// </summary>
public class ModulePlacerLayoutParityTest
{
    public static IEnumerable<int> AllVersions() => Enumerable.Range(1, 40);

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

    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task FunctionModules_FastMatchesReference(int version)
    {
        var size = QRCodeData.SizeFromVersion(version);
        var expected = new byte[size * size];
        var expectedMask = new byte[(size * size + 7) / 8];
        QRCodeGenerator.PlaceFunctionModulesReference(expected, size, version, expectedMask);

        var actual = new byte[size * size];
        actual.AsSpan().Fill(0xA5); // dirty: the fast path must write every module
        var actualMask = new byte[(size * size + 7) / 8];
        actualMask.AsSpan().Fill(0x3C);
        QRCodeGenerator.PlaceFunctionModules(actual, size, version, actualMask);

        await Assert.That(actual.AsSpan().SequenceEqual(expected)).IsTrue();
        await Assert.That(actualMask.AsSpan().SequenceEqual(expectedMask)).IsTrue();

        var layout = ModulePlacer.GetLayout(version);
        await Assert.That(layout.Size).IsEqualTo(size);
        await Assert.That(layout.BlockedMask.AsSpan().SequenceEqual(expectedMask)).IsTrue();
        await Assert.That(layout.Template.AsSpan().SequenceEqual(expected)).IsTrue();
        // free modules = every bit not set in the blocked mask, in the layout's index
        var free = 0;
        for (var i = 0; i < size * size; i++)
            if ((expectedMask[i >> 3] & (1 << (i & 7))) == 0) free++;
        await Assert.That(layout.FreeModules).IsEqualTo(free);
        await Assert.That(layout.Index.Distinct().Count()).IsEqualTo(free);
    }

    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task DataPlacement_FastMatchesReference_EveryStreamShape(int version)
    {
        var size = QRCodeData.SizeFromVersion(version);
        var layout = ModulePlacer.GetLayout(version);
        var free = layout.FreeModules;
        var template = new byte[size * size];
        var mask = new byte[(size * size + 7) / 8];
        QRCodeGenerator.PlaceFunctionModulesReference(template, size, version, mask);

        int[] lengths = [free / 8, (free + 7) / 8, free / 8 + 3, free / 16, 5, 1, 0];
        foreach (var length in lengths.Distinct())
        {
            for (var seed = 0; seed < 3; seed++)
            {
                var data = seed == 2 ? Enumerable.Repeat((byte)0xFF, length).ToArray() : PseudoRandom(length, version * 7 + seed);

                var expected = (byte[])template.Clone();
                ModulePlacer.PlaceDataWords(expected, size, data, mask);

                var actual = (byte[])template.Clone();
                ModulePlacer.PlaceDataWords(actual, layout, data);

                if (!actual.AsSpan().SequenceEqual(expected))
                {
                    var first = 0;
                    while (first < expected.Length && actual[first] == expected[first]) first++;
                    Assert.Fail($"v{version} len {length} seed {seed}: first mismatch at {first} (row {first / size}, col {first % size}): expected {expected[first]}, actual {actual[first]}");
                }
            }
        }
        await Assert.That(layout.Ops.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task DataPlacement_RejectsUndersizedBuffer()
    {
        var layout = ModulePlacer.GetLayout(1);
        await Assert.That(() => ModulePlacer.PlaceDataWords(new byte[21 * 21 - 1], layout, new byte[26])).Throws<ArgumentException>();
        await Assert.That(() => ModulePlacer.GetLayout(0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => ModulePlacer.GetLayout(41)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => QRCodeGenerator.PlaceFunctionModules(new byte[21 * 21], 25, 1, new byte[64])).Throws<ArgumentException>();
    }
}
