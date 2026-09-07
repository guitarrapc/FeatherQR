using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FeatherQR.Internals.BinaryEncoders;

/// <summary>
/// Reed-Solomon error correction encoder for QR code generation.
/// </summary>
/// <remarks>
/// This encoder implements the Reed-Solomon error correction algorithm as specified in ISO/IEC 18004 Section 8.5.
/// The public entry point dispatches to the fastest kernel the runtime supports: GFNI (net10.0+, ~64x over the naive form), SSSE3 (net8.0+, ~52x), NEON (net8.0+ ARM64, ~21-36x), or a portable scalar kernel (~4.4x) used on netstandard and pre-SSSE3 x86.
/// All kernels produce byte-identical output; see the kernel parity tests.
/// </remarks>
internal static partial class EccBinaryEncoder
{
    // Algorithm (ISO/IEC 18004 Section 8.5):
    // 1. Create Reed-solomon generator polynomial G(x) = G(256) = (x-α^0)(x-α^1)...(x-α^(n-1))
    // 2. Convert data byte to message polynomial M(x)
    // 3. Multiply message polynomial M(x) by x^n to shift coefficients (shift by eccCount positions)
    // 4. Perform polynomial division in GF(256) using XOR operations: M(x)·x^n by G(x)
    // 5. Remainder R(x) contains the ECC codewords
    //
    // Example (simplified):
    // - Data: [64, 86, 134, 86] → M(x) = 64x^3 + 86x^2 + 134x + 86
    // - ECC count: 10
    // - Generator: G(x) = (x-α^0)(x-α^1)...(x-α^9)
    // - Result: 10 ECC codewords

    /// <summary>
    /// Calculates error correction codewords using Reed-Solomon algorithm.
    /// </summary>
    /// <param name="data">Data codewords.</param>
    /// <param name="ecc">Output buffer for ECC codewords (must be at least <paramref name="eccCount"/> bytes).</param>
    /// <param name="eccCount">Number of error correction codewords to generate.</param>
    /// <remarks>
    /// Uses Galois Field GF(256) arithmetic from <see cref="GaloisField"/> for polynomial operations.
    /// The generator polynomial is built using primitive polynomial x^8 + x^4 + x^3 + x^2 + 1 (0x11D).
    /// </remarks>
    public static void CalculateECC(ReadOnlySpan<byte> data, Span<byte> ecc, int eccCount)
    {
        if (ecc.Length < eccCount)
            throw new ArgumentException($"ECC buffer too small: required {eccCount}, got {ecc.Length}", nameof(ecc));
        if (eccCount < 1 || eccCount > 255)
            throw new ArgumentOutOfRangeException(nameof(eccCount), $"ECC count must be 1-255, got {eccCount}");

#if NET8_0_OR_GREATER
        // QR codes use at most 30 ECC codewords per block, so the vectorized kernels
        // (which keep the remainder register in one or two vector registers) cover
        // every real input; the <= 32 guard is a safety net for non-QR callers.
        if (eccCount <= 32)
        {
            if (System.Runtime.Intrinsics.X86.Ssse3.IsSupported)
            {
                CalculateEccSimd(data, ecc, eccCount);
                return;
            }
            if (System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
            {
                CalculateEccAdvSimd(data, ecc, eccCount);
                return;
            }
        }
#endif
        CalculateEccScalar(data, ecc, eccCount);
    }

    /// <summary>
    /// Portable scalar kernel.
    /// Runs on every target (netstandard2.0+, old x86, pre-NEON ARM).
    /// </summary>
    /// <remarks>
    /// Two optimizations over the naive polynomial division, both measured (~4.4x combined):
    /// - The generator polynomial depends only on <paramref name="eccCount"/> and QR uses a
    ///   small fixed set of counts, so it is cached in log domain per count. This removes the
    ///   O(eccCount²) per-call construction and lets the inner loop do a single Exp lookup
    ///   instead of a full GF multiply (2 Log lookups + zero checks) per element.
    /// - Table and buffer accesses go through refs so the JIT emits no bounds checks in the
    ///   inner loop (measured ~17% on top of the caching).
    /// </remarks>
    internal static void CalculateEccScalar(ReadOnlySpan<byte> data, Span<byte> ecc, int eccCount)
    {
        var logGen = GetLogGenerator(eccCount);
        if (logGen is null)
        {
            // eccCount == 255 degenerates to G(x) = x^255 + 1, which has zero coefficients
            // and therefore no log-domain representation. Never produced by QR; handled by
            // the naive kernel so every documented eccCount stays correct.
            CalculateEccNaive(data, ecc, eccCount);
            return;
        }

        // Message polynomial: data followed by eccCount zero bytes; the division
        // remainder accumulates in the zero tail.
        Span<byte> message = stackalloc byte[data.Length + eccCount];
        data.CopyTo(message);

        ref var exp = ref MemoryMarshal.GetReference(GaloisField.Exp);
        ref var log = ref MemoryMarshal.GetReference(GaloisField.Log);
        ref var gen = ref MemoryMarshal.GetReference(logGen.AsSpan());
        ref var msg = ref MemoryMarshal.GetReference(message);

        for (var i = 0; i < data.Length; i++)
        {
            var coefficient = Unsafe.Add(ref msg, i);
            if (coefficient == 0) continue;

            int logC = Unsafe.Add(ref log, coefficient);
            ref var target = ref Unsafe.Add(ref msg, i + 1);
            for (var j = 0; j < eccCount; j++)
            {
                // message[i + j + 1] ^= generator[j + 1] · coefficient, in log domain.
                // Exp is 512 entries, so logGen[j] + logC (max 508) needs no % 255.
                Unsafe.Add(ref target, j) ^= Unsafe.Add(ref exp, Unsafe.Add(ref gen, j) + logC);
            }
        }

        message.Slice(data.Length, eccCount).CopyTo(ecc);
    }

    /// <summary>
    /// Naive polynomial division (the pre-optimization implementation).
    /// Used only when the generator polynomial has no log-domain representation (eccCount == 255).
    /// </summary>
    private static void CalculateEccNaive(ReadOnlySpan<byte> data, Span<byte> ecc, int eccCount)
    {
        Span<byte> generator = stackalloc byte[eccCount + 1];
        GenerateGeneratorPolynomial(generator, eccCount);

        Span<byte> message = stackalloc byte[data.Length + eccCount];
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

        message.Slice(data.Length, eccCount).CopyTo(ecc);
    }

    // Log-domain generator polynomial cache, indexed by eccCount (1..255).
    // logGen[j] = Log[generator[j + 1]] (the leading 1 coefficient is implicit).
    // A sentinel empty array marks counts whose generator cannot be represented in
    // log domain. Benign race: concurrent builds produce identical arrays; published
    // with Volatile.Write so readers on weakly-ordered runtimes never observe a
    // partially initialized array.
    private static readonly byte[]?[] _logGenCache = new byte[]?[256];
    private static readonly byte[] _logGenUnrepresentable = [];

    /// <summary>
    /// Returns the cached log-domain generator polynomial, or null when it has no log-domain representation.
    /// </summary>
    /// <remarks>
    /// Reed-Solomon generator polynomials have all-nonzero coefficients for eccCount ≤ 254: G(x) is itself a codeword of the length-255 RS code with minimum distance eccCount + 1 (MDS), so its weight must be ≥ eccCount + 1 — every one of its eccCount + 1 coefficients.
    /// The single exception is eccCount == 255, where G(x) = Π(x - α^i) over all nonzero field elements collapses to x^255 + 1 (254 zero coefficients).
    /// Verified by exhaustive scan over eccCount 1..255.
    /// </remarks>
    private static byte[]? GetLogGenerator(int eccCount)
    {
        var cached = _logGenCache[eccCount];
        if (cached is not null) return cached.Length == 0 ? null : cached;

        Span<byte> generator = stackalloc byte[eccCount + 1];
        GenerateGeneratorPolynomial(generator, eccCount);

        var logGen = new byte[eccCount];
        for (var j = 0; j < eccCount; j++)
        {
            var coefficient = generator[j + 1];
            if (coefficient == 0)
            {
                Volatile.Write(ref _logGenCache[eccCount], _logGenUnrepresentable);
                return null;
            }
            logGen[j] = GaloisField.Log[coefficient];
        }

        Volatile.Write(ref _logGenCache[eccCount], logGen);
        return logGen;
    }

    /// <summary>
    /// Generates Reed-Solomon generator polynomial.
    /// </summary>
    /// <param name="generator">Output buffer for generator polynomial coefficients.</param>
    /// <param name="eccCount">Number of error correction codewords.</param>
    /// <remarks>
    /// Formula: (x - α^0)(x - α^1)...(x - α^(n-1)) where n = numEccWords Example for eccWordCount = 3: G(x) = (x - α^0)(x - α^1)(x - α^2)
    ///      = (x - 1)(x - α)(x - α^2)
    ///      = x^3 + α^0·x^2 + α^1·x + α^0
    ///
    /// The generator polynomial is built iteratively:
    /// 1. Start: G(x) = 1 (polynomial of degree 0)
    /// 2. Multiply by (x - α^0): G(x) = (1)(x - 1) = x - 1 (degree 1)
    /// 3. Multiply by (x - α^1): G(x) = (x - 1)(x - α) = x^2 + ...
    /// (degree 2)
    /// 4. Multiply by (x - α^2): G(x) = x^3 + ...
    /// (degree 3)
    /// 5. Continue until degree = eccCount. ... n.
    /// Result has degree = eccCount.
    /// </remarks>
    private static void GenerateGeneratorPolynomial(Span<byte> generator, int eccCount)
    {
        generator.Clear();

        // init with (x - α^0), start with polynomial "1"
        generator[0] = 1;

        Span<byte> temp = stackalloc byte[generator.Length];

        // Multiply by (x - α^i) for i = 1 to eccWordCount - 1
        for (var i = 0; i < eccCount; i++)
        {
            // Multiply current generator by (x - α^i)
            temp.Clear();

            for (var j = 0; j <= i; j++)
            {
                var coefficient = generator[j];
                if (coefficient == 0) continue;

                // (x - α^i) expansion
                // - Multiply existing term by x: shift to temp[j]
                // - Multiply existing term by -α^i: add to temp[j+1]
                temp[j] ^= coefficient; // x^1 term
                temp[j + 1] ^= GaloisField.Multiply(coefficient, GaloisField.Exp[i]);
            }

            temp.CopyTo(generator);
        }
    }
}
