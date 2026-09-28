using TUnit.Assertions.Enums;
using FeatherQR.Internals;
using FeatherQR.Internals.BinaryEncoders;

namespace FeatherQR.Tests;

/// <summary>
/// Verifies that every ECC kernel (scalar, SSSE3, GFNI, NEON) produces byte-identical
/// output to a naive reference implementation of ISO/IEC 18004 Section 8.5
/// polynomial division. CalculateECC dispatches by hardware capability, so these
/// tests exercise each kernel directly in addition to the public entry point.
/// </summary>
public class EccBinaryEncoderKernelParityTest
{
    // (dataLength, eccCount) pairs covering QR extremes and boundary conditions:
    // smallest/largest real blocks, the 16/17 register-width split, eccCount 1,
    // data shorter than one 4-byte quad, and data not a multiple of 4.
    public static IEnumerable<(int DataLength, int EccCount)> Combos =>
    [
        (1, 1),
        (1, 7),
        (3, 10),
        (16, 10),   // version 1-M block
        (19, 16),   // eccCount at the 128-bit register boundary
        (20, 17),   // eccCount just above the boundary
        (34, 28),
        (119, 30),  // version 40-L block
        (2956, 30), // far beyond any single QR block (large-data safety)
    ];

    [Test]
    [MethodDataSource(nameof(Combos))]
    public async Task CalculateECC_MatchesNaiveReference(int dataLength, int eccCount)
    {
        foreach (var data in EnumerateInputs(dataLength))
        {
            var expected = new byte[eccCount];
            NaiveReferenceECC(data, expected, eccCount);

            var actual = new byte[eccCount];
            EccBinaryEncoder.CalculateECC(data, actual, eccCount);

            await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching);
        }
    }

    [Test]
    [MethodDataSource(nameof(Combos))]
    public async Task ScalarKernel_MatchesNaiveReference(int dataLength, int eccCount)
    {
        foreach (var data in EnumerateInputs(dataLength))
        {
            var expected = new byte[eccCount];
            NaiveReferenceECC(data, expected, eccCount);

            var actual = new byte[eccCount];
            EccBinaryEncoder.CalculateEccScalar(data, actual, eccCount);

            await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching);
        }
    }

    [Test]
    [MethodDataSource(nameof(Combos))]
    public async Task Ssse3Kernel_MatchesNaiveReference(int dataLength, int eccCount)
    {
        if (!System.Runtime.Intrinsics.X86.Ssse3.IsSupported)
        {
            Skip.Test("SSSE3 not supported on this machine");
            return;
        }

        foreach (var data in EnumerateInputs(dataLength))
        {
            var expected = new byte[eccCount];
            NaiveReferenceECC(data, expected, eccCount);

            var actual = new byte[eccCount];
            EccBinaryEncoder.CalculateEccSsse3(data, actual, eccCount);

            await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching);
        }
    }

    [Test]
    [MethodDataSource(nameof(Combos))]
    public async Task AdvSimdKernel_MatchesNaiveReference(int dataLength, int eccCount)
    {
        if (!System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
        {
            Skip.Test("AdvSimd (ARM64 NEON) not supported on this machine");
            return;
        }

        foreach (var data in EnumerateInputs(dataLength))
        {
            var expected = new byte[eccCount];
            NaiveReferenceECC(data, expected, eccCount);

            var actual = new byte[eccCount];
            EccBinaryEncoder.CalculateEccAdvSimd(data, actual, eccCount);

            await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching);
        }
    }

#if NET10_0_OR_GREATER
    [Test]
    [MethodDataSource(nameof(Combos))]
    public async Task GfniKernel_MatchesNaiveReference(int dataLength, int eccCount)
    {
        if (!System.Runtime.Intrinsics.X86.Gfni.IsSupported)
        {
            Skip.Test("GFNI not supported on this machine");
            return;
        }

        foreach (var data in EnumerateInputs(dataLength))
        {
            var expected = new byte[eccCount];
            NaiveReferenceECC(data, expected, eccCount);

            var actual = new byte[eccCount];
            EccBinaryEncoder.CalculateEccGfni(data, actual, eccCount);

             await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task GfniMatrixTable_MatchesGaloisFieldMultiply()
    {
        // EccBinaryEncoder.GfniMatrix is baked into the assembly as constant data;
        // this recomputes every entry from GaloisField.Multiply so the constant can
        // never drift from the field definition. gf2p8affineqb convention: qword
        // byte (7-i) = matrix row for result bit i, row bit k = bit i of f繝ｻ繧托ｽｽ・ｷ2^k.
        var table = EccBinaryEncoder.GfniMatrix.ToArray();
         await Assert.That(table.Length).IsEqualTo(256);

        for (var f = 0; f < 256; f++)
        {
            var expected = 0UL;
            for (var i = 0; i < 8; i++)
            {
                byte row = 0;
                for (var k = 0; k < 8; k++)
                {
                    var prod = GaloisField.Multiply((byte)f, (byte)(1 << k));
                    row |= (byte)(((prod >> i) & 1) << k);
                }
                expected |= (ulong)row << ((7 - i) * 8);
            }

            var actual = table[(int)f];
            await Assert.That(actual).IsEquivalentTo(expected);
        }
    }
#endif

    [Test]
    public async Task CalculateECC_EccCountAboveVectorLimit_UsesScalarPath()
    {
        // eccCount > 32 bypasses the vector kernels; verify the dispatch stays correct.
        var data = new byte[50];
        new Random(7).NextBytes(data);
        var eccCount = 40;

        var expected = new byte[eccCount];
        NaiveReferenceECC(data, expected, eccCount);

        var actual = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data, actual, eccCount);

        await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(254)] // largest eccCount whose generator is log-domain representable
    [Arguments(255)] // generator degenerates to x^255 + 1 (zero coefficients) 驕ｯ・ｶ郢晢ｽｻnaive fallback
    public async Task CalculateECC_MaxEccCounts_MatchesNaiveReference(int eccCount)
    {
        // eccCount == 255 is the only count whose generator polynomial has zero
        // coefficients (all others are all-nonzero by the MDS property), so it cannot
        // use the log-domain fast path. Both counts must still produce correct ECC.
        foreach (var data in EnumerateInputs(64))
        {
            var expected = new byte[eccCount];
            NaiveReferenceECC(data, expected, eccCount);

            var viaPublicApi = new byte[eccCount];
            EccBinaryEncoder.CalculateECC(data, viaPublicApi, eccCount);
            await Assert.That(viaPublicApi).IsEquivalentTo(expected, CollectionOrdering.Matching);

            var viaScalarKernel = new byte[eccCount];
            EccBinaryEncoder.CalculateEccScalar(data, viaScalarKernel, eccCount);
            await Assert.That(viaScalarKernel).IsEquivalentTo(expected, CollectionOrdering.Matching);
        }
    }

    /// <summary>
    /// Input variations per size: seeded random (several seeds), all-zero
    /// (exercises every zero-skip path), and all-0xFF.
    /// </summary>
    private static IEnumerable<byte[]> EnumerateInputs(int dataLength)
    {
        for (var seed = 0; seed < 3; seed++)
        {
            var data = new byte[dataLength];
            new Random(seed).NextBytes(data);
            yield return data;
        }

        yield return new byte[dataLength]; // all zeros

        var ones = new byte[dataLength];
        ones.AsSpan().Fill(0xFF);
        yield return ones;
    }

    /// <summary>
    /// Naive ISO/IEC 18004 Section 8.5 polynomial division, kept deliberately
    /// simple and independent from the production kernels: builds the generator
    /// polynomial per call and multiplies element-wise via GaloisField.Multiply.
    /// </summary>
    private static void NaiveReferenceECC(ReadOnlySpan<byte> data, Span<byte> ecc, int eccCount)
    {
        // Generator polynomial (x - 繝ｻ雜｣・ｽ・ｱ^0)(x - 繝ｻ雜｣・ｽ・ｱ^1)...(x - 繝ｻ雜｣・ｽ・ｱ^(eccCount-1))
        var generator = new byte[eccCount + 1];
        generator[0] = 1;
        var temp = new byte[generator.Length];
        for (var i = 0; i < eccCount; i++)
        {
            Array.Clear(temp);
            for (var j = 0; j <= i; j++)
            {
                var coefficient = generator[j];
                if (coefficient == 0) continue;
                temp[j] ^= coefficient;
                temp[j + 1] ^= GaloisField.Multiply(coefficient, GaloisField.Exp[i]);
            }
            temp.CopyTo(generator.AsSpan());
        }

        // Polynomial division
        var message = new byte[data.Length + eccCount];
        data.CopyTo(message);
        for (var i = 0; i < data.Length; i++)
        {
            var coefficient = message[i];
            if (coefficient == 0) continue;
            for (var j = 0; j < eccCount; j++)
            {
                message[i + j + 1] ^= GaloisField.Multiply(generator[j + 1], coefficient);
            }
        }

        message.AsSpan(data.Length, eccCount).CopyTo(ecc);
    }
}
