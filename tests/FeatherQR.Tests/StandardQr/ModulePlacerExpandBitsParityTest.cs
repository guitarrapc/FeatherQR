using FeatherQR.Internals.StandardQR;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics.Arm;
#endif

namespace FeatherQR.Tests;

/// <summary>Expansion writes exactly eight MSB-first 0/1 bytes per codeword, without requiring scratch slack.</summary>
public class ModulePlacerExpandBitsParityTest
{
    public static IEnumerable<(int Length, int Seed)> Inputs()
    {
        foreach (var length in new[] { 0, 1, 2, 3, 4, 7, 8, 9, 15, 16, 17, 25, 26, 27, 31, 32, 33, 43, 44, 45, 70, 134, 255, 256, 257, 345, 346, 347, 3705, 3706, 3707 })
        foreach (var seed in new[] { -1, 0, 7, 42, 20260920 })
            yield return (length, seed);
    }

    [Test]
    [MethodDataSource(nameof(Inputs))]
    public async Task Expand_ExactAndOffsetBuffers_MatchReference(int length, int seed)
    {
        var source = new byte[length];
        if (seed == -1)
            Array.Fill(source, (byte)0xFF);
        else if (seed != 0)
            new Random(seed).NextBytes(source);
        await Check(source);
    }

    [Test]
    public async Task Expand_EveryByteValue_MatchesReference()
    {
        foreach (var length in new[] { 1, 2, 7, 15, 16, 17, 33 })
        for (var value = 0; value <= byte.MaxValue; value++)
            await Check(Enumerable.Repeat((byte)value, length).ToArray());
    }

#if NET8_0_OR_GREATER
    [Test]
    [Arguments(255)]
    [Arguments(256)]
    [Arguments(257)]
    [Arguments(3706)]
    public async Task Expand_AlignmentPrefixBoundary_MatchesReference(int length)
    {
        if (!AdvSimd.Arm64.IsSupported)
            return;
        var source = Enumerable.Range(0, length).Select(i => (byte)(i * 73 + 0xA5)).ToArray();
        var output = Enumerable.Repeat((byte)0xA5, length * 8 + 32).ToArray();
        var expected = (byte[])output.Clone();
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(output, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            // Pin only in the test so mod16 == 8 is guaranteed, independently of GC layout.
            var offset = (int)((8 - handle.AddrOfPinnedObject().ToInt64()) & 15);
            for (var i = 0; i < length * 8; i++)
                expected[offset + i] = (byte)((source[i / 8] >> (7 - i % 8)) & 1);
            ModulePlacer.ExpandBitsAdvSimd(source, length, output.AsSpan(offset, length * 8));
            await Assert.That(output.AsSpan().SequenceEqual(expected)).IsTrue();
        }
        finally
        {
            handle.Free();
        }
    }
#endif

    private delegate void ExpandKernel(ReadOnlySpan<byte> source, int count, Span<byte> destination);

    private static async Task Check(byte[] source)
    {
        ExpandKernel[] kernels = [ModulePlacer.ExpandBits, ModulePlacer.ExpandBitsScalar];
        foreach (var kernel in kernels)
            await CheckKernel(source, kernel);
#if NET8_0_OR_GREATER
        if (AdvSimd.Arm64.IsSupported)
            await CheckKernel(source, ModulePlacer.ExpandBitsAdvSimd);
#endif
    }

    private static async Task CheckKernel(byte[] source, ExpandKernel kernel)
    {
        var expected = new byte[source.Length * 8];
        for (var i = 0; i < expected.Length; i++)
            expected[i] = (byte)((source[i / 8] >> (7 - i % 8)) & 1);
        var exact = new byte[expected.Length];
        exact.AsSpan().Fill(0xA5);
        kernel(source, source.Length, exact);
        await Assert.That(exact.AsSpan().SequenceEqual(expected)).IsTrue().Because(kernel.Method.Name);

        // The requested prefix may be shorter than the source span. Neither the prefix
        // offset nor dirty bytes after it may alter the expanded stream or its guards.
        var paddedSource = Enumerable.Repeat((byte)0x3C, source.Length + 11).ToArray();
        source.CopyTo(paddedSource, 3);
        foreach (var offset in new[] { 0, 1, 5, 8, 13, 16, 24 })
        {
            var output = Enumerable.Repeat((byte)0xA5, expected.Length + 43).ToArray();
            var expectedOutput = (byte[])output.Clone();
            expected.CopyTo(expectedOutput, offset);
            kernel(paddedSource.AsSpan(3), source.Length, output.AsSpan(offset, expected.Length));
            await Assert.That(output.AsSpan().SequenceEqual(expectedOutput)).IsTrue().Because($"{kernel.Method.Name}, offset {offset}");
        }
        await Assert.That(paddedSource.AsSpan(3, source.Length).SequenceEqual(source)).IsTrue();
    }
}
