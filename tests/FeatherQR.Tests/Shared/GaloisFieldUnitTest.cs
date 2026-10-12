using FeatherQR.Internals;

namespace FeatherQR.Tests;

public class GaloisFieldUnitTest
{
    // exponent	value (exp[i])  note
    // -----------------------------
    // 0        0x01            a^0 = 1
    // 1        0x02            a^1 = 2
    // 2        0x04            a^2 = 4
    // 3        0x08            a^3 = 8
    // 4        0x10            a^4 = 16
    // 5        0x20            a^5 = 32
    // 6        0x40            a^6 = 64
    // 7        0x80            a^7 = 128
    // 8        0x1D            a^8 = 29 (reduction by irreducible polynomial)
    // 9        0x3A            a^9 = 58
    // ...      ...
    // 219      0xE2            a^219 = 226
    // ...      ...
    // 237      0xFF            a^237 = 255
    // ...      ...
    // 254      0x8D            a^254 = 141
    // 255      0x01            a^255 = 1 (period 255)
    [Test]
    public async Task Multiply_KnownValues_CorrectResults()
    {
        await Assert.That(GaloisField.Multiply(0x10, 0x20)).IsEqualTo((byte)0x3A);
        await Assert.That(GaloisField.Multiply(0x10, 0x01)).IsEqualTo((byte)0x10);
        await Assert.That(GaloisField.Multiply(0x40, 0x1D)).IsEqualTo((byte)0x13);
        await Assert.That(GaloisField.Multiply(0xFF, 0xFF)).IsEqualTo((byte)0xE2);
    }

    [Test]
    public async Task Divide_KnownValues_CorrectResults()
    {
        await Assert.That(GaloisField.Divide(0x8E, 0x20)).IsEqualTo((byte)0x36);
        await Assert.That(GaloisField.Divide(0x3A, 0x20)).IsEqualTo((byte)0x10);
        await Assert.That(GaloisField.Divide(0x1D, 0x80)).IsEqualTo((byte)0x02);
        await Assert.That(GaloisField.Divide(0xFF, 0xFF)).IsEqualTo((byte)0x01);
    }

    [Test]
    public async Task Multiply_WithZero_ReturnsZero_AndDoesNotTouchLogZero()
    {
        // Multiply by 0 should always return 0, and should not attempt to access log(0) which is undefined.
        await Assert.That(GaloisField.Multiply(0x00, 0x53)).IsEqualTo((byte)0x00);
        await Assert.That(GaloisField.Multiply(0x53, 0x00)).IsEqualTo((byte)0x00);
    }

    [Test]
    public async Task Divide_ByZero_Throws()
    {
        Assert.Throws<DivideByZeroException>(() => GaloisField.Divide(0x10, 0x00)); // 0 is no power of a, so it has no logarithm
    }

    [Test]
    public async Task ExpThenLog_AllExponents_AreConsistent()
    {
        // log(exp[i]) = i
        for (int i = 0; i < 255; i++)
        {
            byte v = GaloisField.Exp[i];
            await Assert.That(GaloisField.Log[v]).IsEqualTo((byte)i);
        }
    }

    [Test]
    public async Task LogThenExp_AllValues_AreConsistent()
    {
        // exp(log[v]) = v, v≠0
        for (int v = 1; v <= 255; v++) // 0 is not in non-zero field
        {
            byte vb = (byte)v;
            int i = GaloisField.Log[vb];
            await Assert.That(GaloisField.Exp[i]).IsEqualTo(vb);
        }
    }

    [Test]
    public async Task Exp_IsPeriodicWith255()
    {
        // exp[i+255] = exp[i]
        for (int i = 0; i < 255; i++)
        {
            await Assert.That(GaloisField.Exp[i + 255]).IsEqualTo(GaloisField.Exp[i]);
        }
    }

    [Test]
    public async Task Exp_FormsPermutationOfNonZeroElements()
    {
        // all non-zero element should be included exactly once in exp table
        var seen = new HashSet<byte>();
        for (int i = 0; i < 255; i++)
        {
            await Assert.That(GaloisField.Exp[i]).IsNotEqualTo((byte)0); // 0 is not in non-zero field
            await Assert.That(seen.Add(GaloisField.Exp[i])).IsTrue(); // no duplicates
        }
        await Assert.That(seen.Count).IsEqualTo(255);
    }
}
