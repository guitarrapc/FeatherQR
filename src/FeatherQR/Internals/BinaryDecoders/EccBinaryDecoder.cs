namespace FeatherQR.Internals.BinaryDecoders;

/// <summary>
/// Reed-Solomon error correction decoder for QR code decoding.
/// </summary>
/// <remarks>
/// Inverse of <see cref="BinaryEncoders.EccBinaryEncoder"/>.
/// The encoder builds its generator polynomial as G(x) = (x-α^0)(x-α^1)...(x-α^(n-1)), so the decoder evaluates syndromes at the same consecutive roots starting at α^0 (b = 0).
/// Implements the classical pipeline over GF(256) with primitive polynomial 0x11D:
/// <code>
/// 1. Syndromes:        S_i = R(α^i) for i = 0..eccCount-1 (all zero → no errors)
/// 2. Berlekamp-Massey: error locator polynomial Λ(x) with deg(Λ) = L errors
/// 3. Chien search:     roots of Λ(x) are X_k^-1 → error positions
/// 4. Forney:           magnitudes e_k = X_k·Ω(X_k^-1)/Λ'(X_k^-1)  (b = 0)
/// </code>
/// Corrects up to ⌊eccCount/2⌋ byte errors per block, in place.
/// <para>
/// The syndrome pass (the only cost clean blocks pay, and the dominant cost of the verification pass) has three tiers: a GFNI kernel on net10.0+ x64 (see EccBinaryDecoder.Simd.cs), an AdvSimd kernel on ARM64 (see EccBinaryDecoder.Simd.Arm.cs), and a scalar log-domain path with four interleaved Horner chains everywhere else.
/// All three produce byte-identical output; see the decoder kernel parity tests.
/// Berlekamp-Massey, Chien and Forney stay scalar on every target.
/// </para>
/// </remarks>
internal static partial class EccBinaryDecoder
{
    // QR codes use at most 30 ECC codewords per block (ISO/IEC 18004), so all
    // intermediate polynomials fit in fixed stackalloc buffers.
    private const int MaxEccPerBlock = 30;
    private const int MaxErrors = MaxEccPerBlock / 2;

    /// <summary>
    /// Syndrome buffer length.
    /// One Vector256 group (GFNI) or two Vector128 groups (AdvSimd) cover every QR/rMQR ECC count, and both kernels store their accumulators whole rather than copying eccCount bytes, so the buffer is padded to the vector width.
    /// Lanes at or past eccCount hold syndromes of roots the code does not use and are never read.
    /// </summary>
    internal const int SyndromeLanes = 32;

    /// <summary>
    /// Detects and corrects codeword errors in a single Reed-Solomon block, in place.
    /// </summary>
    /// <param name="codeword">Block codewords (data followed by ECC). Corrected in place.</param>
    /// <param name="eccCount">Number of ECC codewords at the tail of <paramref name="codeword"/> (1-30).</param>
    /// <param name="errorsCorrected">Number of byte errors corrected (0 when the block was clean).</param>
    /// <returns>True when the block is clean or was fully corrected; false when the block contains more errors than the code can correct.</returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    /// <exception cref="ArgumentException"></exception>
    public static bool TryCorrect(Span<byte> codeword, int eccCount, out int errorsCorrected)
    {
        if (eccCount < 1 || eccCount > MaxEccPerBlock)
            throw new ArgumentOutOfRangeException(nameof(eccCount), $"ECC count must be 1-{MaxEccPerBlock}, got {eccCount}");
        if (codeword.Length <= eccCount || codeword.Length > 255)
            throw new ArgumentException($"Codeword length must be in ({eccCount}, 255], got {codeword.Length}", nameof(codeword));

        errorsCorrected = 0;

        // 1. Syndromes: S_i = R(α^i) where codeword[0] is the highest-degree
        // coefficient (the same polynomial orientation the encoder divides in).
        Span<byte> syndromes = stackalloc byte[SyndromeLanes];
        var hasError = ComputeSyndromes(codeword, eccCount, syndromes);

        if (!hasError)
            return true;

        // 2. Berlekamp-Massey: find the minimal LFSR Λ(x) generating the syndromes.
        // Λ(x) = 1 + Λ_1·x + ... + Λ_L·x^L, roots at X_k^-1 for each error locator X_k.
        Span<byte> lambda = stackalloc byte[MaxEccPerBlock + 1];
        Span<byte> prev = stackalloc byte[MaxEccPerBlock + 1]; // B(x): copy of Λ at the last length change
        Span<byte> temp = stackalloc byte[MaxEccPerBlock + 1];
        lambda.Clear();
        prev.Clear();
        lambda[0] = 1;
        prev[0] = 1;
        var errorCountEstimate = 0; // L: current LFSR length
        var gap = 1;                // m: steps since B(x) was updated
        byte lastDiscrepancy = 1;   // b: discrepancy at the last length change

        for (var n = 0; n < eccCount; n++)
        {
            // Discrepancy δ = S_n + Σ_{i=1..L} Λ_i·S_(n-i)
            var delta = syndromes[n];
            for (var i = 1; i <= errorCountEstimate; i++)
            {
                delta ^= GaloisField.Multiply(lambda[i], syndromes[n - i]);
            }

            if (delta == 0)
            {
                gap++;
                continue;
            }

            var coefficient = GaloisField.Divide(delta, lastDiscrepancy);
            if (2 * errorCountEstimate <= n)
            {
                // Length change: Λ(x) ← Λ(x) - (δ/b)·x^m·B(x), then B(x) ← old Λ(x)
                lambda.CopyTo(temp);
                for (var i = 0; i + gap <= MaxEccPerBlock; i++)
                {
                    lambda[i + gap] ^= GaloisField.Multiply(coefficient, prev[i]);
                }
                errorCountEstimate = n + 1 - errorCountEstimate;
                temp.CopyTo(prev);
                lastDiscrepancy = delta;
                gap = 1;
            }
            else
            {
                // Same length: Λ(x) ← Λ(x) - (δ/b)·x^m·B(x)
                for (var i = 0; i + gap <= MaxEccPerBlock; i++)
                {
                    lambda[i + gap] ^= GaloisField.Multiply(coefficient, prev[i]);
                }
                gap++;
            }
        }

        // More errors than the code can correct
        if (2 * errorCountEstimate > eccCount)
            return false;

        // 3. Chien search: codeword[idx] is the coefficient of x^(n-1-idx), so an
        // error at idx has locator X = α^(n-1-idx). Test every position p by
        // evaluating Λ(α^-p); a zero means an error at idx = n-1-p.
        Span<int> errorIndexes = stackalloc int[MaxErrors];
        Span<byte> errorLocators = stackalloc byte[MaxErrors];        // X_k = α^p
        Span<byte> errorLocatorInverses = stackalloc byte[MaxErrors]; // X_k^-1 = α^-p
        var errorCount = 0;
        var length = codeword.Length;
        for (var p = 0; p < length; p++)
        {
            var xInverse = GaloisField.Exp[(255 - p) % 255]; // α^-p
            // Horner evaluation of Λ(xInverse), degree L
            byte value = lambda[errorCountEstimate];
            for (var i = errorCountEstimate - 1; i >= 0; i--)
            {
                value = (byte)(GaloisField.Multiply(value, xInverse) ^ lambda[i]);
            }

            if (value != 0)
                continue;

            if (errorCount == errorCountEstimate)
                return false; // more roots than deg(Λ), inconsistent, uncorrectable

            errorIndexes[errorCount] = length - 1 - p;
            errorLocators[errorCount] = GaloisField.Exp[p % 255];
            errorLocatorInverses[errorCount] = xInverse;
            errorCount++;
        }

        // Every root of Λ must lie inside the codeword; a deficit means errors
        // at positions that do not exist (degree mismatch), uncorrectable.
        if (errorCount != errorCountEstimate)
            return false;

        // 4. Forney: Ω(x) = S(x)·Λ(x) mod x^eccCount has degree < L when the error
        // count is within capacity, so only the first L coefficients are needed.
        Span<byte> omega = stackalloc byte[MaxErrors];
        for (var i = 0; i < errorCount; i++)
        {
            byte value = 0;
            var maxJ = Math.Min(i, errorCountEstimate);
            for (var j = 0; j <= maxJ; j++)
            {
                value ^= GaloisField.Multiply(syndromes[i - j], lambda[j]);
            }
            omega[i] = value;
        }

        for (var k = 0; k < errorCount; k++)
        {
            var xInverse = errorLocatorInverses[k];

            // Ω(X_k^-1) via Horner, degree L-1
            byte omegaValue = 0;
            for (var i = errorCount - 1; i >= 0; i--)
            {
                omegaValue = (byte)(GaloisField.Multiply(omegaValue, xInverse) ^ omega[i]);
            }

            // Formal derivative in GF(2^8) keeps only odd-power terms:
            // Λ'(x) = Λ_1 + Λ_3·x^2 + Λ_5·x^4 + ...
            byte denominator = 0;
            var xInverseSquared = GaloisField.Multiply(xInverse, xInverse);
            byte xPower = 1; // xInverse^(i-1) for i = 1, 3, 5, ...
            for (var i = 1; i <= errorCountEstimate; i += 2)
            {
                denominator ^= GaloisField.Multiply(lambda[i], xPower);
                xPower = GaloisField.Multiply(xPower, xInverseSquared);
            }

            if (denominator == 0)
                return false;

            // e_k = X_k^(1-b)·Ω(X_k^-1)/Λ'(X_k^-1); with b = 0 the X_k factor remains.
            var magnitude = GaloisField.Multiply(errorLocators[k], GaloisField.Divide(omegaValue, denominator));
            codeword[errorIndexes[k]] ^= magnitude;
        }

        // Verify the correction: recomputed syndromes must all be zero. This guards
        // against silent miscorrection (worse than failing) at the cost of one extra
        // syndrome pass, taken only on blocks that actually had errors. Note the size
        // of that cost: a corrected block runs the syndrome pass TWICE, which is most
        // of the gap between a clean and a corrected decode (about 10 us of the 12.8 us
        // difference at rMQR R17x139) and why a faster syndrome kernel pays more on
        // damaged input than on clean.
        Span<byte> check = stackalloc byte[SyndromeLanes];
        if (ComputeSyndromes(codeword, eccCount, check))
            return false;

        errorsCorrected = errorCount;
        return true;
    }

    /// <summary>
    /// Computes the syndromes S_i = R(α^i) for i = 0..eccCount-1 via Horner evaluation: v = (((c_0·x + c_1)·x + c_2)·x + ...) at x = α^i.
    /// Returns true when any syndrome is non-zero (the block has errors).
    /// </summary>
    /// <remarks>
    /// <paramref name="syndromes"/> must be at least <see cref="SyndromeLanes"/> bytes long, not <paramref name="eccCount"/>: both vector kernels store their accumulator registers whole and only the first <paramref name="eccCount"/> lanes are meaningful.
    /// A shorter span throws on the GFNI tier (Vector256.CopyTo bounds- checks the destination) but silently corrupts the caller's stack on ARM64, so x64 CI cannot see the ARM failure mode; EccBinaryDecoderKernelParityTest.GfniKernel_WritesExactlySyndromeLanes and .AdvSimdKernel_WritesExactlySyndromeLanes pin the store width from the kernel side.
    /// <para>
    /// Dispatches to the GFNI kernel on x64 (all accumulators in one vector register, one multiply per data byte for every syndrome at once) or the AdvSimd kernel on ARM64 (see EccBinaryDecoder.Simd.Arm.cs); both keep every syndrome in vector lanes rather than walking the codeword once per syndrome.
    /// </para>
    /// <para>
    /// The scalar path keeps the Horner multiply in log domain — the multiplier's log is the constant i, so each step is one zero-check + one log load + one exp load (measured 0.84-0.90x of the GaloisField.Multiply form) — and runs four syndromes per pass over the codeword.
    /// That interleaving is not cosmetic: the per-syndrome walk is bound by the dependent log → exp load chain rather than by throughput, so four independent accumulators measured 2.2-2.9x the single walk on large blocks.
    /// This is the tier netstandard2.0/2.1 and any CPU without GFNI or AdvSimd runs.
    /// </para>
    /// </remarks>
    private static bool ComputeSyndromes(ReadOnlySpan<byte> codeword, int eccCount, Span<byte> syndromes)
    {
#if NET10_0_OR_GREATER
        if (System.Runtime.Intrinsics.X86.Gfni.V256.IsSupported)
        {
            return ComputeSyndromesGfni(codeword, eccCount, syndromes);
        }
#endif
#if NET8_0_OR_GREATER
        // The property, not a second copy of the condition: the parity tests skip on
        // IsAdvSimdTierSupported, so a gate that drifted from it would let them compare
        // the scalar tier against itself.
        if (IsAdvSimdTierSupported)
        {
            return ComputeSyndromesAdvSimd(codeword, eccCount, syndromes);
        }
#endif
        return ComputeSyndromesScalar(codeword, eccCount, syndromes);
    }

    internal static bool ComputeSyndromesScalar(ReadOnlySpan<byte> codeword, int eccCount, Span<byte> syndromes)
    {
        var exp = GaloisField.Exp;
        var log = GaloisField.Log;
        var length = codeword.Length;

        byte any = 0;
        var i = 0;
        for (; i + 4 <= eccCount; i += 4)
        {
            // Four independent Horner chains over one pass of the codeword; each step
            // is value·α^i = exp[log[value] + i] (exp is 512 entries so no % 255).
            byte v0 = 0, v1 = 0, v2 = 0, v3 = 0;
            for (var j = 0; j < length; j++)
            {
                var c = codeword[j];
                v0 = (byte)((v0 == 0 ? (byte)0 : exp[log[v0] + i]) ^ c);
                v1 = (byte)((v1 == 0 ? (byte)0 : exp[log[v1] + i + 1]) ^ c);
                v2 = (byte)((v2 == 0 ? (byte)0 : exp[log[v2] + i + 2]) ^ c);
                v3 = (byte)((v3 == 0 ? (byte)0 : exp[log[v3] + i + 3]) ^ c);
            }
            syndromes[i] = v0;
            syndromes[i + 1] = v1;
            syndromes[i + 2] = v2;
            syndromes[i + 3] = v3;
            any |= (byte)(v0 | v1 | v2 | v3);
        }
        for (; i < eccCount; i++)
        {
            byte value = 0;
            for (var j = 0; j < length; j++)
            {
                value = (byte)((value == 0 ? (byte)0 : exp[log[value] + i]) ^ codeword[j]);
            }
            syndromes[i] = value;
            any |= value;
        }
        return any != 0;
    }
}
