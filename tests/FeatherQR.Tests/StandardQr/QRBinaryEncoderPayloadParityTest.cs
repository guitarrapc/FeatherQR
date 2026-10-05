using TUnit.Assertions.Enums;
using FeatherQR.Internals;
using FeatherQR.Internals.BinaryEncoders;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// The Alphanumeric and Numeric payload writers of <see cref="QRBinaryEncoder"/>, the portable, x64 and ARM64 tiers entered directly (the
/// WebAssembly tier is held to the old writers by the timing mode's parity check on its build), against the payload written
/// from the definition (ISO/IEC 18004 7.4.3 and 7.4.4): the character's index in the alphabet, first * 45 + second in 11 bits and 6 for a
/// last odd character; three digits in 10 bits, two in 7, one in 4; every field MSB first.
/// </summary>
/// <remarks>
/// A run inside a mixed-mode plan starts wherever the previous run ended, so every run is written after 0 to 31 bits of a known pattern,
/// and the whole buffer is compared: the writer keeps up to 31 bits pending between appends. A character outside the alphabet, at any
/// position of a vector step, must still throw, and only after the fields of the pairs ahead of it.
/// </remarks>
public class QRBinaryEncoderPayloadParityTest
{
    public enum Route
    {
        Scalar,
        Ssse3,
        AdvSimd,
    }

    public static IEnumerable<Route> Routes() => Enum.GetValues<Route>();

    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    private static readonly int[] Lengths = [.. Enumerable.Range(0, 301), 511, 512, 513, 1000, 4295, 4296];

    [Test]
    [MethodDataSource(nameof(Routes))]
    public async Task Alphanumeric_MatchesDefinition(Route route)
    {
        if (!Available(route, alphanumeric: true))
        {
            Skip.Test($"{route} not run on this machine");
            return;
        }

        foreach (var length in Lengths)
        {
            // random text, and the alphabet from each of 16 starting points so every character falls on every lane of a vector step
            var texts = new List<string> { Pick(length, Alphabet, length * 31 + 1), Pick(length, Alphabet, length * 31 + 2) };
            if (length is >= 16 and <= 64)
            {
                for (var rotation = 0; rotation < 16; rotation++)
                    texts.Add(new string(Enumerable.Range(0, length).Select(k => Alphabet[(k + rotation) % Alphabet.Length]).ToArray()));
            }

            foreach (var text in texts)
            {
                for (var align = 0; align < 32; align += length > 300 ? 5 : 1)
                {
                    var (actual, bits, error) = Write(route, writeAlphanumeric: true, text, align);
                    await Assert.That(error).IsNull().Because($"{route}, length {length}, align {align}");
                    var expected = Expected(align, AlphanumericFields(text, text.Length), actual.Length);
                    await Assert.That(bits).IsEqualTo(align + PayloadBits(AlphanumericFields(text, text.Length))).Because($"{route}, length {length}, align {align}");
                    await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching).Because($"{route}, length {length}, align {align}");
                }
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(Routes))]
    public async Task Alphanumeric_CharacterOutsideTheAlphabet_ThrowsAfterTheFieldsAheadOfIt(Route route)
    {
        if (!Available(route, alphanumeric: true))
        {
            Skip.Test($"{route} not run on this machine");
            return;
        }

        // Below 0x80 (where a table can hold a sentinel), Latin-1 above it, and past 0xFF, where the portable writer's table index keeps
        // the low seven bits ('Ł' and '聁' index 'A', 'Ａ' '!') and the vector step's pack saturates ('Ł' to 0xFF, '聁' and 'Ａ' to 0).
        foreach (var bad in new[] { 'a', '#', '@', '[', '\0', '\u007F', 'Á', 'Ł', '聁', 'Ａ' })
        {
            foreach (var length in new[] { 1, 2, 3, 7, 8, 9, 15, 16, 17, 31, 32, 33, 40 })
            {
                for (var position = 0; position < length; position++)
                {
                    var chars = Pick(length, Alphabet, length * 131 + position).ToCharArray();
                    chars[position] = bad;
                    var text = new string(chars);
                    foreach (var align in new[] { 0, 5, 31 })
                    {
                        var because = $"{route}, U+{(int)bad:X4} at {position} of {length}, align {align}";
                        var (actual, bits, error) = Write(route, writeAlphanumeric: true, text, align);
                        await Assert.That(error).IsEqualTo(nameof(ArgumentException)).Because(because);

                        // the pairs ahead of the bad character's pair are written, and nothing after them
                        var ahead = position & ~1;
                        var fields = AlphanumericFields(text, ahead);
                        await Assert.That(bits).IsEqualTo(align + PayloadBits(fields)).Because(because);
                        await Assert.That(actual).IsEquivalentTo(Expected(align, fields, actual.Length), CollectionOrdering.Matching).Because(because);
                    }
                }
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(Routes))]
    public async Task Alphanumeric_EveryCharacterOutsideTheAlphabet_Throws(Route route)
    {
        if (!Available(route, alphanumeric: true))
        {
            Skip.Test($"{route} not run on this machine");
            return;
        }

        // A vector tier decides membership by the character's row and low nibble, so every character outside the alphabet up to 0xFF is
        // put in the lanes of an eight- and a sixteen-character step, with some past 0xFF whose low byte is in the alphabet.
        var outside = Enumerable.Range(0, 256).Select(c => (char)c).Where(c => Alphabet.IndexOf(c) < 0)
            .Concat(['Ā', 'Ł', 'İ', '翿', '聁', 'Ａ', '￿']);
        foreach (var bad in outside)
        {
            foreach (var (length, position) in new[] { (9, 0), (9, 7), (9, 8), (17, 0), (17, 9), (17, 15), (17, 16), (33, 31) })
            {
                var chars = Pick(length, Alphabet, bad * 7 + position).ToCharArray();
                chars[position] = bad;
                var text = new string(chars);
                var because = $"{route}, U+{(int)bad:X4} at {position} of {length}";
                var (actual, bits, error) = Write(route, writeAlphanumeric: true, text, 3);
                await Assert.That(error).IsEqualTo(nameof(ArgumentException)).Because(because);
                var fields = AlphanumericFields(text, position & ~1);
                await Assert.That(bits).IsEqualTo(3 + PayloadBits(fields)).Because(because);
                await Assert.That(actual).IsEquivalentTo(Expected(3, fields, actual.Length), CollectionOrdering.Matching).Because(because);
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(Routes))]
    public async Task Numeric_MatchesDefinition(Route route)
    {
        if (!Available(route, alphanumeric: false))
        {
            Skip.Test($"{route} not run on this machine");
            return;
        }

        foreach (var length in Lengths.Concat([7088, 7089]))
        {
            // random digits, and all '0' and all '9': each group's smallest and largest value (0 and 999)
            var texts = new[] { Pick(length, "0123456789", length * 17 + 1), new string('0', length), new string('9', length) };
            foreach (var text in texts)
            {
                for (var align = 0; align < 32; align += length > 300 ? 5 : 1)
                {
                    var (actual, bits, error) = Write(route, writeAlphanumeric: false, text, align);
                    await Assert.That(error).IsNull().Because($"{route}, length {length}, align {align}");
                    var fields = NumericFields(text);
                    await Assert.That(bits).IsEqualTo(align + PayloadBits(fields)).Because($"{route}, length {length}, align {align}");
                    await Assert.That(actual).IsEquivalentTo(Expected(align, fields, actual.Length), CollectionOrdering.Matching).Because($"{route}, length {length}, align {align}");
                }
            }
        }
    }

    /// <summary>Whether the route's writer of that kind runs here, as its dispatch asks: the Alphanumeric step blends with SSE4.1, the Numeric step needs SSSE3 alone.</summary>
    private static bool Available(Route route, bool alphanumeric) => route switch
    {
        Route.Scalar => true,
        Route.Ssse3 => System.Runtime.Intrinsics.X86.Ssse3.IsSupported && (!alphanumeric || System.Runtime.Intrinsics.X86.Sse41.IsSupported),
        Route.AdvSimd => System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported,
        _ => false,
    };

    [Test]
    [MethodDataSource(nameof(Routes))]
    public async Task Writers_ReadNothingPastTheRun(Route route)
    {
        // the Numeric writer asks the least, so a route without it has neither
        if (!Available(route, alphanumeric: false) || !PageEndMemory.IsSupported)
        {
            Skip.Test($"{route} or protected pages not available on this machine");
            return;
        }

        // A load wider than what it uses (four chars for three digits, sixteen read for twelve written) is kept inside the run by its
        // guard alone, and a read past it shows nowhere in the output: the run ends where readable memory does, so such a read faults.
        using var memory = new PageEndMemory();
        for (var length = 0; length < 70; length++)
        {
            foreach (var alphanumeric in new[] { true, false })
            {
                if (!Available(route, alphanumeric))
                    continue;
                var text = Pick(length, alphanumeric ? Alphabet : "0123456789", length * 7 + 3);
                var (actual, bits, error) = Write(route, alphanumeric, memory.AtPageEnd(text), 0);
                var fields = alphanumeric ? AlphanumericFields(text, text.Length) : NumericFields(text);
                await Assert.That(error).IsNull().Because($"{route}, {(alphanumeric ? "alphanumeric" : "numeric")} length {length}");
                await Assert.That(bits).IsEqualTo(PayloadBits(fields)).Because($"{route}, length {length}");
                await Assert.That(actual).IsEquivalentTo(Expected(0, fields, actual.Length), CollectionOrdering.Matching).Because($"{route}, length {length}");
            }
        }
    }

    /// <summary>
    /// The run as a writer gets it inside a plan: a slice of a longer text whose next characters are in the alphabet, so a step that read
    /// past the run would take them into the stream.
    /// </summary>
    private static (byte[] Buffer, int Bits, string? Error) Write(Route route, bool writeAlphanumeric, string text, int align)
        => Write(route, writeAlphanumeric, (text + (writeAlphanumeric ? "ABCDEFGHIJKLMNOP" : "0123456789012345")).AsSpan(0, text.Length), align);

    /// <summary>The prefix of <paramref name="align"/> bits, then the run through the route; the whole buffer flushed, the bit position, and the exception's type if one was thrown.</summary>
    private static (byte[] Buffer, int Bits, string? Error) Write(Route route, bool writeAlphanumeric, ReadOnlySpan<char> run, int align)
    {
        var buffer = new byte[(run.Length * 6 + 64) / 8 + 8];
        var writer = new BitWriter(buffer);
        if (align > 0)
            writer.Write(Prefix >> (32 - align), align);
        string? error = null;
        try
        {
            switch (route, writeAlphanumeric)
            {
                case (Route.Scalar, true): QRBinaryEncoder.WriteAlphanumericScalar(ref writer, run); break;
                case (Route.Scalar, false): QRBinaryEncoder.WriteNumericScalar(ref writer, run); break;
                case (Route.Ssse3, true): QRBinaryEncoder.WriteAlphanumericSsse3(ref writer, run); break;
                case (Route.Ssse3, false): QRBinaryEncoder.WriteNumericSsse3(ref writer, run); break;
                case (Route.AdvSimd, true): QRBinaryEncoder.WriteAlphanumericAdvSimd(ref writer, run); break;
                case (Route.AdvSimd, false): QRBinaryEncoder.WriteNumericAdvSimd(ref writer, run); break;
            }
        }
        catch (Exception e)
        {
            error = e.GetType().Name;
        }
        var bits = writer.BitPosition;
        writer.Flush();
        return (buffer, bits, error);
    }

    private const int Prefix = unchecked((int)0xA5C3_9E71);

    /// <summary>The fields of the first <paramref name="count"/> characters by the definition: (value, width), MSB first.</summary>
    private static List<(int Value, int Width)> AlphanumericFields(string text, int count)
    {
        var fields = new List<(int, int)>();
        var i = 0;
        for (; i + 1 < count; i += 2)
            fields.Add((Alphabet.IndexOf(text[i]) * 45 + Alphabet.IndexOf(text[i + 1]), 11));
        if (i < count)
            fields.Add((Alphabet.IndexOf(text[i]), 6));
        return fields;
    }

    private static List<(int Value, int Width)> NumericFields(string digits)
    {
        var fields = new List<(int, int)>();
        for (var i = 0; i < digits.Length; i += 3)
        {
            var group = digits.Substring(i, Math.Min(3, digits.Length - i));
            fields.Add((int.Parse(group), group.Length switch { 3 => 10, 2 => 7, _ => 4 }));
        }
        return fields;
    }

    private static int PayloadBits(List<(int Value, int Width)> fields) => fields.Sum(f => f.Width);

    /// <summary>The buffer the prefix and the fields make, MSB first, zero past them.</summary>
    private static byte[] Expected(int align, List<(int Value, int Width)> fields, int length)
    {
        var bytes = new byte[length];
        var position = 0;
        void Put(int value, int width)
        {
            for (var b = width - 1; b >= 0; b--, position++)
            {
                if (((value >> b) & 1) != 0)
                    bytes[position >> 3] |= (byte)(0x80 >> (position & 7));
            }
        }
        if (align > 0)
            Put((Prefix >> (32 - align)) & (int)((1L << align) - 1), align);
        foreach (var (value, width) in fields)
            Put(value, width);
        return bytes;
    }

    private static string Pick(int length, string alphabet, int seed)
    {
        var random = new Random(seed);
        return new string(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
    }
}
