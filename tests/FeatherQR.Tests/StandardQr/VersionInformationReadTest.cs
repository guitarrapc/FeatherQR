using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// The image path reads the version information of version 7+ symbols to settle a dimension
/// the module-size estimate got wrong. Up to three bit errors per copy are corrected, as the
/// standard allows; four are not, since the code's minimum distance is eight.
/// </summary>
public class VersionInformationReadTest
{
    public static IEnumerable<int> VersionsWithVersionInformation() => Enumerable.Range(7, 34);

    [Test]
    [MethodDataSource(nameof(VersionsWithVersionInformation))]
    public async Task Read_EveryVersion_NamesItsDimension(int version)
    {
        var (modules, dimension) = Matrix(version);

        await Assert.That(QRImageDecoder.ReadVersionDimension(modules, dimension)).IsEqualTo(dimension);
    }

    [Test]
    [Arguments(7)]
    [Arguments(22)]
    [Arguments(40)]
    public async Task Read_ThreeErrorsInBothCopies_StillNamesTheDimension(int version)
    {
        var (modules, dimension) = Matrix(version);
        FlipTopRight(modules, dimension, 3);
        FlipBottomLeft(modules, dimension, 3);

        await Assert.That(QRImageDecoder.ReadVersionDimension(modules, dimension)).IsEqualTo(dimension);
    }

    [Test]
    [Arguments(7)]
    [Arguments(22)]
    [Arguments(40)]
    public async Task Read_FourErrorsInBothCopies_NamesNothing(int version)
    {
        var (modules, dimension) = Matrix(version);
        FlipTopRight(modules, dimension, 4);
        FlipBottomLeft(modules, dimension, 4);

        await Assert.That(QRImageDecoder.ReadVersionDimension(modules, dimension)).IsEqualTo(0);
    }

    [Test]
    [Arguments(7)]
    [Arguments(40)]
    public async Task Read_OneCopyDestroyed_ReadsTheOther(int version)
    {
        var (modules, dimension) = Matrix(version);
        FlipTopRight(modules, dimension, 18);

        await Assert.That(QRImageDecoder.ReadVersionDimension(modules, dimension)).IsEqualTo(dimension);
    }

    /// <summary>The two copies are transposes of each other, so a mirrored capture reads the same.</summary>
    [Test]
    [Arguments(7)]
    [Arguments(31)]
    public async Task Read_TransposedMatrix_NamesTheSameDimension(int version)
    {
        var (modules, dimension) = Matrix(version);
        var transposed = new byte[modules.Length];
        for (var row = 0; row < dimension; row++)
        {
            for (var col = 0; col < dimension; col++)
                transposed[col * dimension + row] = modules[row * dimension + col];
        }

        await Assert.That(QRImageDecoder.ReadVersionDimension(transposed, dimension)).IsEqualTo(dimension);
    }

    [Test]
    [Arguments(1)]
    [Arguments(6)]
    public async Task Read_BelowVersion7_NamesNothing(int version)
    {
        var (modules, dimension) = Matrix(version);

        await Assert.That(QRImageDecoder.ReadVersionDimension(modules, dimension)).IsEqualTo(0);
    }

    [Test]
    public async Task Read_NoVersionInformation_NamesNothing()
    {
        // All light: 18 bits away from the all-zero word no version encodes
        var dimension = 45;
        var modules = new byte[dimension * dimension];

        await Assert.That(QRImageDecoder.ReadVersionDimension(modules, dimension)).IsEqualTo(0);
    }

    private static (byte[] Modules, int Dimension) Matrix(int version)
    {
        var qr = QRCodeGenerator.Create("V", QREccLevel.M, new QRCodeGeneratorOptions { Version = version, QuietZoneSize = 0 });
        var modules = new byte[qr.Size * qr.Size];
        for (var row = 0; row < qr.Size; row++)
        {
            for (var col = 0; col < qr.Size; col++)
                modules[row * qr.Size + col] = qr[row, col] ? (byte)1 : (byte)0;
        }
        return (modules, qr.Size);
    }

    // Copy layout (ISO/IEC 18004 7.10): bit x*3+y at row x, column dimension-11+y (top-right)
    // and at row dimension-11+y, column x (bottom-left).
    private static void FlipTopRight(byte[] modules, int dimension, int bits)
    {
        for (var i = 0; i < bits; i++)
            modules[i / 3 * dimension + dimension - 11 + i % 3] ^= 1;
    }

    private static void FlipBottomLeft(byte[] modules, int dimension, int bits)
    {
        // Flip from the other end of the word so the two copies differ in different bits
        for (var i = 17; i > 17 - bits; i--)
            modules[(dimension - 11 + i % 3) * dimension + i / 3] ^= 1;
    }
}
