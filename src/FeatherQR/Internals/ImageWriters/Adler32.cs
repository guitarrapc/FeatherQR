namespace FeatherQR.Internals.ImageWriters;

/// <summary>Adler-32 (RFC 1950) of a run of scanlines, where each scanline repeats a number of times.</summary>
internal struct Adler32()
{
    private const uint Modulus = 65521;
    private ulong _a = 1;
    private ulong _b = 0;

    public readonly uint Value => (uint)(_b << 16 | _a);

    /// <summary>
    /// Adds <paramref name="line"/> <paramref name="times"/> times in closed form: with the line's own sums from zero (s1, s2) and length L,
    /// k copies add k s1 to A, and k L A + k s2 + s1 L k(k - 1) / 2 to B.
    /// </summary>
    public void AddRepeated(ReadOnlySpan<byte> line, int times)
    {
        ulong s1 = 0, s2 = 0;
        var i = 0;
        while (i < line.Length)
        {
            // Reduced every 5,552 bytes, so the sums stay far inside a ulong.
            var end = Math.Min(line.Length, i + 5552);
            for (; i < end; i++)
            {
                s1 += line[i];
                s2 += s1;
            }
            s1 %= Modulus;
            s2 %= Modulus;
        }
        var length = (ulong)line.Length % Modulus;
        var k = (ulong)times % Modulus;
        var pairs = (ulong)times * (ulong)(times - 1) / 2 % Modulus;
        var newA = (_a + k * s1) % Modulus;
        _b = (_b + k * length % Modulus * _a % Modulus + k * s2 % Modulus + s1 * length % Modulus * pairs % Modulus) % Modulus;
        _a = newA;
    }
}
