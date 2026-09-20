using FeatherQR.Internals;
using FeatherQR.Internals.BinaryDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// Parity tests for the syndrome-pass kernels: the scalar log-domain kernel is
/// checked against a naive ISO/IEC 18004 reference (Horner with GaloisField.Multiply),
/// and the GFNI kernel (net10.0+ x64) against the scalar kernel, byte for byte.
/// </summary>
public class EccBinaryDecoderKernelParityTest
{
    [Test]
    public async Task ScalarKernel_MatchesNaiveReference()
    {
        var random = new Random(20260712);
        for (var round = 0; round < 300; round++)
        {
            var length = random.Next(8, 256);
            var eccCount = random.Next(1, 31);
            var codeword = new byte[length];
            if (round % 10 != 0)
                random.NextBytes(codeword); // every 10th round keeps all-zero data

            var expected = new byte[eccCount];
            var expectedHasError = NaiveSyndromes(codeword, eccCount, expected);

            var actual = new byte[eccCount];
            var actualHasError = EccBinaryDecoder.ComputeSyndromesScalar(codeword, eccCount, actual);

            await Assert.That(actualHasError).IsEquivalentTo(expectedHasError);
            await Assert.That(actual).IsEquivalentTo(expected);
        }
    }

#if NET10_0_OR_GREATER
    /// <summary>
    /// The GFNI kernel stores its accumulator register whole, so its destination is
    /// SyndromeLanes bytes; lanes at or past eccCount hold syndromes of unused roots
    /// and are not compared.
    /// </summary>
    [Test]
    public async Task GfniKernel_MatchesScalarKernel()
    {
        if (!System.Runtime.Intrinsics.X86.Gfni.V256.IsSupported)
        {
            Skip.Test("GFNI not supported on this machine");
            return;
        }

        var random = new Random(20260712);
        for (var round = 0; round < 300; round++)
        {
            var length = random.Next(8, 256);
            var eccCount = random.Next(1, 31);
            var codeword = new byte[length];
            if (round % 10 != 0)
                random.NextBytes(codeword);

            var expected = new byte[eccCount];
            var expectedHasError = EccBinaryDecoder.ComputeSyndromesScalar(codeword, eccCount, expected);

            var actual = new byte[EccBinaryDecoder.SyndromeLanes];
            Array.Fill(actual, (byte)0xA5); // poison: a kernel that writes nothing must fail
            var actualHasError = EccBinaryDecoder.ComputeSyndromesGfni(codeword, eccCount, actual);

            await Assert.That(actualHasError).IsEquivalentTo(expectedHasError);
            await Assert.That(actual.AsSpan(0, eccCount).ToArray()).IsEquivalentTo(expected);
        }
    }

    /// <summary>
    /// The GFNI kernel stores 32 bytes unconditionally, whatever eccCount is, so every
    /// caller must hand it a SyndromeLanes-wide destination. This pins that the store is
    /// exactly that wide, the x64 half of the same contract
    /// AdvSimdKernel_WritesExactlySyndromeLanes pins on ARM64.
    /// </summary>
    [Test]
    public async Task GfniKernel_WritesExactlySyndromeLanes()
    {
        if (!System.Runtime.Intrinsics.X86.Gfni.V256.IsSupported)
        {
            Skip.Test("GFNI not supported on this machine");
            return;
        }

        const byte Poison = 0x5A;
        const int TailBytes = 32;

        var random = new Random(20260821);
        for (var eccCount = 1; eccCount <= 30; eccCount++)
        {
            foreach (var length in new[] { eccCount + 1, 13, 58, 255 })
            {
                var codeword = new byte[length];
                random.NextBytes(codeword);

                var backing = new byte[EccBinaryDecoder.SyndromeLanes + TailBytes];
                backing.AsSpan().Fill(Poison);
                EccBinaryDecoder.ComputeSyndromesGfni(codeword, eccCount, backing.AsSpan(0, EccBinaryDecoder.SyndromeLanes));

                for (var i = EccBinaryDecoder.SyndromeLanes; i < backing.Length; i++)
                {
                    await Assert.That(backing[i]).IsEqualTo(Poison)
                        .Because($"eccCount {eccCount}, length {length}: wrote {i - EccBinaryDecoder.SyndromeLanes + 1} byte(s) past SyndromeLanes");
                }
            }
        }
    }

    [Test]
    public async Task GfniIsomorphismConstants_MatchFirstPrinciplesConstruction()
    {
        // Rebuild the GF(0x11D) 竊・GF(0x11B) isomorphism from scratch and verify the
        // constants baked into EccBinaryDecoder.Simd.cs. ﾎｲ is the first root of
        // x^8+x^4+x^3+x^2+1 in GF(0x11B); ﾏ・maps by linearity over the ﾎｲ^i basis.
        var beta = 0;
        for (var cand = 2; cand < 256; cand++)
        {
            var p2 = MulPoly((byte)cand, (byte)cand, 0x11b);
            var p3 = MulPoly(p2, (byte)cand, 0x11b);
            var p4 = MulPoly(p2, p2, 0x11b);
            var p8 = MulPoly(p4, p4, 0x11b);
            if ((p8 ^ p4 ^ p3 ^ p2 ^ 1) == 0)
            {
                beta = cand;
                break;
            }
        }
        await Assert.That(beta).IsNotEqualTo(0);

        Span<byte> basis = stackalloc byte[8];
        basis[0] = 1;
        for (var i = 1; i < 8; i++)
        {
            basis[i] = MulPoly(basis[i - 1], (byte)beta, 0x11b);
        }
        var phi = new byte[256];
        var psi = new byte[256];
        for (var v = 0; v < 256; v++)
        {
            byte m = 0;
            for (var i = 0; i < 8; i++)
            {
                if ((v & (1 << i)) != 0) m ^= basis[i];
            }
            phi[v] = m;
        }
        for (var v = 0; v < 256; v++)
        {
            psi[phi[v]] = (byte)v;
        }

        // Exhaustive multiplicative homomorphism check
        for (var a = 0; a < 256; a++)
        {
            for (var b = 0; b < 256; b++)
            {
                await Assert.That(MulPoly(phi[a], phi[b], 0x11b)).IsEquivalentTo(phi[MulPoly((byte)a, (byte)b, 0x11d)]);
            }
        }

        // Baked matrix constants (ﾏ・happens to be an involution: ﾏ・== ﾏ・
        await Assert.That(BuildAffineMatrix(phi)).IsEquivalentTo(EccBinaryDecoder.GfniPhiMatrix);
        await Assert.That(BuildAffineMatrix(psi)).IsEquivalentTo(EccBinaryDecoder.GfniPhiMatrix);

        // Baked per-lane constants: lane i = ﾏ・ﾎｱ^ki) in the 0x11B domain, k = 1..4
        for (var i = 0; i < 32; i++)
        {
            await Assert.That(phi[GaloisField.Exp[i]]).IsEquivalentTo(EccBinaryDecoder.GfniAlphas[i]);
            await Assert.That(phi[GaloisField.Exp[2 * i]]).IsEquivalentTo(EccBinaryDecoder.GfniAlphasSquared[i]);
            await Assert.That(phi[GaloisField.Exp[3 * i]]).IsEquivalentTo(EccBinaryDecoder.GfniAlphasCubed[i]);
            await Assert.That(phi[GaloisField.Exp[4 * i]]).IsEquivalentTo(EccBinaryDecoder.GfniAlphasFourth[i]);
        }
    }

    private static ulong BuildAffineMatrix(byte[] map)
    {
        var m = 0UL;
        for (var i = 0; i < 8; i++)
        {
            byte row = 0;
            for (var k = 0; k < 8; k++)
            {
                row |= (byte)(((map[1 << k] >> i) & 1) << k);
            }
            m |= (ulong)row << ((7 - i) * 8);
        }
        return m;
    }

#endif


#if NET8_0_OR_GREATER
    /// <summary>
    /// The AdvSimd kernel stores both 16-lane accumulator groups whole, so its
    /// destination must be SyndromeLanes bytes; lanes at or past eccCount hold
    /// syndromes of unused roots and are not compared.
    /// </summary>
    [Test]
    public async Task AdvSimdKernel_MatchesScalarKernel()
    {
        if (!EccBinaryDecoder.IsAdvSimdTierSupported)
        {
            Skip.Test("AdvSimd.Arm64 not supported on this machine");
            return;
        }

        var random = new Random(20260820);
        for (var round = 0; round < 300; round++)
        {
            var length = random.Next(8, 256);
            var eccCount = random.Next(1, 31);
            var codeword = new byte[length];
            if (round % 10 != 0)
                random.NextBytes(codeword);

            var expected = new byte[eccCount];
            var expectedHasError = EccBinaryDecoder.ComputeSyndromesScalar(codeword, eccCount, expected);

            var actual = new byte[EccBinaryDecoder.SyndromeLanes];
            Array.Fill(actual, (byte)0xA5); // poison: a kernel that writes nothing must fail
            var actualHasError = EccBinaryDecoder.ComputeSyndromesAdvSimd(codeword, eccCount, actual);

            await Assert.That(actualHasError).IsEquivalentTo(expectedHasError);
            await Assert.That(actual.AsSpan(0, eccCount).ToArray()).IsEquivalentTo(expected);
        }
    }

    /// <summary>
    /// The AdvSimd kernel stores 32 bytes unconditionally, whatever eccCount is, so
    /// every caller must hand it a SyndromeLanes-wide destination. This pins that the
    /// store is exactly that wide: a kernel that grew to a third accumulator group
    /// would smash the caller's stack, and comparing only the first eccCount bytes
    /// cannot see it.
    /// </summary>
    [Test]
    public async Task AdvSimdKernel_WritesExactlySyndromeLanes()
    {
        if (!EccBinaryDecoder.IsAdvSimdTierSupported)
        {
            Skip.Test("AdvSimd.Arm64 not supported on this machine");
            return;
        }

        const byte Poison = 0x5A;
        const int TailBytes = 32;

        var random = new Random(20260821);
        for (var eccCount = 1; eccCount <= 30; eccCount++)
        {
            foreach (var length in new[] { eccCount + 1, 13, 58, 255 })
            {
                var codeword = new byte[length];
                random.NextBytes(codeword);

                var backing = new byte[EccBinaryDecoder.SyndromeLanes + TailBytes];
                backing.AsSpan().Fill(Poison);
                EccBinaryDecoder.ComputeSyndromesAdvSimd(codeword, eccCount, backing.AsSpan(0, EccBinaryDecoder.SyndromeLanes));

                for (var i = EccBinaryDecoder.SyndromeLanes; i < backing.Length; i++)
                {
                    await Assert.That(backing[i]).IsEqualTo(Poison)
                        .Because($"eccCount {eccCount}, length {length}: wrote {i - EccBinaryDecoder.SyndromeLanes + 1} byte(s) past SyndromeLanes");
                }
            }
        }
    }

    /// <summary>
    /// Boundary sweep: every ECC count the symbologies use, block lengths straddling
    /// the eight-byte tree step, its four-byte remainder and its scalar tail (every
    /// length mod 8, through eccCount + 1..+9), the 16-lane dispatch boundary, and the
    /// degenerate all-light / all-dark codewords. The has-error flag matters as much as
    /// the bytes: lanes past eccCount are non-zero even for a clean block, so a missing
    /// lane mask would report errors on a perfect symbol.
    /// </summary>
    [Test]
    public async Task AdvSimdKernel_MatchesScalarKernel_AtBoundaries()
    {
        if (!EccBinaryDecoder.IsAdvSimdTierSupported)
        {
            Skip.Test("AdvSimd.Arm64 not supported on this machine");
            return;
        }

        for (var eccCount = 1; eccCount <= 30; eccCount++)
        {
            foreach (var length in new[] { eccCount + 1, eccCount + 2, eccCount + 3, eccCount + 4, eccCount + 5, eccCount + 6, eccCount + 7, eccCount + 8, eccCount + 9, 13, 39, 40, 47, 48, 58, 149, 152, 153, 255 })
            {
                if (length <= eccCount || length > 255)
                    continue;

                foreach (var fill in new[] { 0x00, 0xFF, 0x01, 0x80 })
                {
                    var codeword = new byte[length];
                    Array.Fill(codeword, (byte)fill);

                    var expected = new byte[eccCount];
                    var expectedHasError = EccBinaryDecoder.ComputeSyndromesScalar(codeword, eccCount, expected);

                    var actual = new byte[EccBinaryDecoder.SyndromeLanes];
                    Array.Fill(actual, (byte)0xA5);
                    var actualHasError = EccBinaryDecoder.ComputeSyndromesAdvSimd(codeword, eccCount, actual);

                    await Assert.That(actualHasError).IsEquivalentTo(expectedHasError);
                    await Assert.That(actual.AsSpan(0, eccCount).ToArray()).IsEquivalentTo(expected);
                }
            }
        }
    }

    /// <summary>
    /// Rebuilds the baked AdvSimd constants from first principles. The lane vectors are
    /// the Horner multipliers; the two nibble tables are the reduction of a product's
    /// high byte, which is only valid because that byte has degree at most 6.
    /// </summary>
    [Test]
    public async Task AdvSimdConstants_MatchFirstPrinciplesConstruction()
    {
        for (var i = 0; i < EccBinaryDecoder.SyndromeLanes; i++)
        {
            await Assert.That(EccBinaryDecoder.AdvSimdAlpha1[i]).IsEquivalentTo(GaloisField.Exp[i % 255]);
            await Assert.That(EccBinaryDecoder.AdvSimdAlpha4[i]).IsEquivalentTo(GaloisField.Exp[(4 * i) % 255]);
            await Assert.That(EccBinaryDecoder.AdvSimdAlpha8[i]).IsEquivalentTo(GaloisField.Exp[(8 * i) % 255]);
        }

        // x^8 = 0x1D in GF(2^8)/0x11D, so reducing a high byte h means multiplying it
        // by 0x1D; splitting h into nibbles makes that two 16-entry lookups.
        for (var n = 0; n < 16; n++)
        {
            await Assert.That(EccBinaryDecoder.AdvSimdReduceLow[n]).IsEquivalentTo(MulPoly((byte)n, 0x1D, 0x11d));
            await Assert.That(EccBinaryDecoder.AdvSimdReduceHigh[n]).IsEquivalentTo(MulPoly((byte)(n << 4), 0x1D, 0x11d));
        }

        // The split is exact: low ^ high must reconstruct the whole reduction.
        for (var h = 0; h < 128; h++)
        {
            var split = EccBinaryDecoder.AdvSimdReduceLow[h & 0x0F] ^ EccBinaryDecoder.AdvSimdReduceHigh[h >> 4];
            await Assert.That((byte)split).IsEquivalentTo(MulPoly((byte)h, 0x1D, 0x11d));
        }
    }
#endif

    private static byte MulPoly(byte a, byte b, int poly)
    {
        var r = 0;
        var x = (int)a;
        var y = (int)b;
        while (y != 0)
        {
            if ((y & 1) != 0) r ^= x;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= poly;
            y >>= 1;
        }
        return (byte)r;
    }

    private static bool NaiveSyndromes(ReadOnlySpan<byte> codeword, int eccCount, Span<byte> syndromes)
    {
        var hasError = false;
        for (var i = 0; i < eccCount; i++)
        {
            var x = GaloisField.Exp[i];
            byte value = 0;
            for (var j = 0; j < codeword.Length; j++)
            {
                value = (byte)(GaloisField.Multiply(value, x) ^ codeword[j]);
            }
            syndromes[i] = value;
            hasError |= value != 0;
        }
        return hasError;
    }
}
