using System.Runtime.CompilerServices;

namespace FeatherQR.Internals;

/// <summary>
/// 8-bit Galois Field GF(256) arithmetic for Reed-Solomon error correction.
/// Primitive polynomial: x^8 + x^4 + x^3 + x^2 + 1 (0x11d)
/// </summary>
/// <remarks>
/// For more information, see: <see href="https://en.wikipedia.org/wiki/Finite_field"/>
/// </remarks>
internal static class GaloisField
{
    // 1. What is Galois field?
    // A Galois field defines specialized arithmetic over 8-bit values (0-255).
    // In this field, every non-zero values can be represented as powers of a primitive element α.
    // GF(256) = {0, 1, α^0, α^1, α^2, ..., α^254}
    //
    // The value α is what called primitive element or generator, and it satisfies following properties:
    // α^255 = 1 (period 255)
    //
    // It means that period of α is 255, and we can represent all non-zero values in GF(256) as powers of α.
    // Math.Pow(α, 255) = 1

    // 2. finite field properties and exponential expression
    // non-zero elements in GF(256) can be expressed as powers of α.
    // --------------------
    // element   | exponent
    // --------------------
    // 1         | α^0
    // 2         | α^1
    // 4         | α^2
    // ....      | ....
    // 1 (again) | α^255

    // 3. Multiplication and Division
    // Multiplication:
    // In GF(256), multiplication and division can be performed using properties of exponents.
    // a^i * a^j = a^(i+j)
    //
    // If exponents exceed 255, we can reduce them using modulo operation:
    // a^i * a^j = a^(i+j mod 255)
    //
    // Division:
    // In GF(256), division can also be performed using properties of exponents.
    // a^i / a^j = a^(i-j)
    //
    // If the result of (i-j) is negative, we can adjust it by adding 255:
    // a^i / a^j = a^((i-j+255) mod 255)

    // 4. Implementation
    // To implement multiplication and division in GF(256), we can use lookup tables for exponentiation and logarithms.
    // This allows us to convert between integer values and their corresponding exponents efficiently.
    // We create two tables:
    // - Exponentiation table (expTable): Maps exponents to their corresponding integer values.
    // - Logarithm table (logTable): Maps integer values to their corresponding exponents.
    //
    // Using these tables, we can perform multiplication and division as follows:
    // - Multiplication: To multiply two elements a and b, we find their exponents i and j using the log table, compute (i+j) mod 255, and then use the exp table to find the resulting value.
    // - Division: To divide element a by b, we find their exponents i and
    //   j using the log table, compute (i-j+255) mod 255, and then use the exp table to find the resulting value.
    //
    // Division example:
    // ```
    // logTable[a] = a's exponent i (where α^i = a)
    // logTable[b] = b's exponent j (where α^j = b)
    // (i - j) can be negative, so we adjust it by adding 255
    // expTable[(i - j + 255) % 255] = α^(i - j)
    // ```

    // 5. Why 255, not 256?
    // Because α^255 = 1, the exponents repeat every 255 steps.
    // Exponents range from 0 to 254, and since α^255 = 1, the exponents wrap every 255 steps.
    // The `% 255` operation ensures the exponent wraps around due to α^255 = 1.

    /// <summary>
    /// Gets α^i → integer value lookup table.
    /// </summary>
    public static ReadOnlySpan<byte> Exp => expTable;
    private static readonly byte[] expTable = new byte[512];

    /// <summary>
    /// Gets integer value → i (where α^i = value) lookup table.
    /// </summary>
    public static ReadOnlySpan<byte> Log => logTable;
    private static readonly byte[] logTable = new byte[256];

    static GaloisField()
    {
        InitializeTables();
    }

    /// <summary>
    /// Multiplies two elements in GF(256)
    /// </summary>
    /// <param name="a"></param>
    /// <param name="b"></param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Multiply(byte a, byte b)
    {
        // Zero is no power of α, so it has no log.
        if (a == 0 || b == 0) return 0;

        var expSpan = expTable.AsSpan();
        var logSpan = logTable.AsSpan();

        // No % 255: two logs sum to at most 508, and the exp table repeats up to 511.
        return expSpan[(logSpan[a] + logSpan[b])];
    }

    /// <summary>
    /// Divides two elements in GF(256)
    /// </summary>
    /// <param name="a"></param>
    /// <param name="b"></param>
    /// <exception cref="DivideByZeroException"></exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Divide(byte a, byte b)
    {
        if (a == 0) return 0;
        if (b == 0) throw new DivideByZeroException("GF division by zero");

        var expSpan = expTable.AsSpan();
        var logSpan = logTable.AsSpan();

        // Adding 255 keeps the exponent from going negative.
        return expSpan[(logSpan[a] + 255 - logSpan[b]) % 255];
    }

    /// <summary>
    /// Initialize the exponent and logarithm tables
    /// </summary>
    private static void InitializeTables()
    {
        var expSpan = expTable.AsSpan();
        var logSpan = logTable.AsSpan();

        // Each power is the one before doubled, and reduced by the primitive polynomial when it leaves 8 bits.
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            expSpan[i] = (byte)x;
            logSpan[x] = (byte)i;

            x <<= 1;
            if (x >= 0x100)
            {
                x ^= 0x11d;
            }
        }

        // α^(i + 255) = α^i, so the table repeats past 254 and a sum of two logs indexes it without a modulo.
        for (var i = 255; i < 512; i++)
        {
            expSpan[i] = expSpan[i - 255];
        }
    }
}
