/// <summary>
/// Module damage for the matrix-decode scenarios that Reed-Solomon corrects, so Berlekamp-Massey, Chien and Forney run rather than syndrome generation alone (a clean symbol exits after its syndromes).
/// </summary>
internal static class CorrectableDamage
{
    /// <summary>
    /// Flips <paramref name="flips"/> distinct modules and keeps the draw only when the decoder reports exactly that many corrected errors, so the scenario measures the correction path at its stated strength rather than the failure path.
    /// </summary>
    /// <remarks>
    /// The ErrorsCorrected check is what makes the count honest.
    /// Drawing from the whole matrix spends flips on function patterns, which carry no codeword, and two flips can land in one codeword byte: either way a nominal 2-flip case injects one actual error and measures a shorter correction than its name promises.
    /// Rejecting those draws is conservative: every excluded sample is easier than the one kept.
    /// </remarks>
    /// <param name="decode">Decodes a matrix of the same symbol: whether it read, the text, and the errors corrected.</param>
    public static byte[] Flip(byte[] modules, int flips, int seed, Func<byte[], (bool Decoded, string Text, int ErrorsCorrected)> decode)
    {
        var expected = decode(modules).Text;

        for (var attempt = 0; attempt < 4096; attempt++)
        {
            var random = new Random(seed + attempt);
            var damaged = (byte[])modules.Clone();
            var picked = new HashSet<int>();
            while (picked.Count < flips)
                picked.Add(random.Next(damaged.Length));
            foreach (var index in picked)
                damaged[index] ^= 1;

            var (decoded, text, errorsCorrected) = decode(damaged);
            if (decoded && text == expected && errorsCorrected == flips)
                return damaged;
        }

        throw new InvalidOperationException($"Benchmark setup is wrong: no {flips}-error correctable damage found.");
    }
}
