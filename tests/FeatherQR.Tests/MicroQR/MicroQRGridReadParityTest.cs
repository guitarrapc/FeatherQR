using FeatherQR.Internals.BinaryDecoders;
using FeatherQR.Internals.MicroQR;

namespace FeatherQR.Tests;

/// <summary>
/// The Micro QR grid path against the one it replaced: codewords read through a placement table and a view of the grid, against the whole-matrix extraction with a transposed copy for the mirrored read; the vector affine sampler against the scalar one; and the format table against the nearest-candidate search.
/// </summary>
/// <remarks>
/// Status, diagnostics, characters written and the whole destination buffer are compared, since a failing bitstream can leave text behind.
/// </remarks>
public class MicroQRGridReadParityTest
{
    private static readonly int[] Sizes = [11, 13, 15, 17];

    public static IEnumerable<int> Seeds() => Enumerable.Range(0, 16);

    [Test]
    [MethodDataSource(nameof(Seeds))]
    public async Task MatrixDecode_MatchesTheWholeMatrixExtraction(int seed)
    {
        var random = new Random(seed);
        var decoded = 0;
        var first = "";
        var mismatches = 0;
        for (var i = 0; i < 300; i++)
        {
            var (matrix, size) = i % 3 == 2 ? RandomMatrix(random) : SymbolMatrix(random, damage: i % 3 == 1 ? random.Next(1, 12) : 0);
            var got = Decode(matrix, size, reference: false);
            var want = Decode(matrix, size, reference: true);
            if (got != want)
            {
                if (mismatches++ == 0)
                    first = $"case {i} size {size}: {got} against {want}";
            }
            if (want.Status == DecodeStatus.Success)
                decoded++;
        }

        await Assert.That(mismatches).IsEqualTo(0).Because(first);
        await Assert.That(decoded).IsGreaterThanOrEqualTo(60).Because("the real symbols have to read, or only failures are compared");
    }

    /// <summary>A mirrored grid read through the transposing view, against the same grid copied transposed.</summary>
    [Test]
    [MethodDataSource(nameof(Seeds))]
    public async Task TransposedView_MatchesATransposedCopy(int seed)
    {
        var random = new Random(50 + seed);
        var decoded = 0;
        var first = "";
        var mismatches = 0;
        for (var i = 0; i < 300; i++)
        {
            var (matrix, size) = i % 3 == 2 ? RandomMatrix(random) : SymbolMatrix(random, damage: i % 3 == 1 ? random.Next(1, 12) : 0);
            // The symbol mirrored, so the view reads it back upright
            Transpose(matrix, size);
            var copy = (byte[])matrix.Clone();
            Transpose(copy, size);
            var got = Decode(matrix, new TransposedModules<MatrixModules>(new MatrixModules(size)), size);
            var want = Decode(copy, size, reference: true);
            if (got != want && mismatches++ == 0)
                first = $"case {i} size {size}: {got} against {want}";
            if (want.Status == DecodeStatus.Success)
                decoded++;
        }

        await Assert.That(mismatches).IsEqualTo(0).Because(first);
        await Assert.That(decoded).IsGreaterThanOrEqualTo(60);
    }

    /// <summary>The vector affine sampler against the scalar one, module for module, over noise, rendered symbols and frames that run off the image.</summary>
    [Test]
    [MethodDataSource(nameof(Seeds))]
    public async Task AffineSampler_Vector128AndScalar_AreIdentical(int seed)
    {
#if NET8_0_OR_GREATER
        if (!System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
        {
            Skip.Test("Vector128 not accelerated on this machine");
            return;
        }

        var random = new Random(100 + seed);
        var first = "";
        var mismatches = 0;
        for (var i = 0; i < 200; i++)
        {
            var scene = Scene.Build(random, i % 4);
            var originX = scene.CenterX - 3.5f * (scene.UX + scene.VX);
            var originY = scene.CenterY - 3.5f * (scene.UY + scene.VY);
            foreach (var size in Sizes)
            {
                var scalar = new byte[size * size];
                var vector = new byte[size * size];
                MicroQRImageDecoder.SampleGridScalar(scene.Luminance, scene.Width, scene.Height, scene.Threshold, originX, originY, scene.UX, scene.UY, scene.VX, scene.VY, size, scalar);
                MicroQRImageDecoder.SampleGridVector128(scene.Luminance, scene.Width, scene.Height, scene.Threshold, originX, originY, scene.UX, scene.UY, scene.VX, scene.VY, size, vector);
                if (!scalar.AsSpan().SequenceEqual(vector) && mismatches++ == 0)
                    first = $"case {i} ({scene.Kind}) size {size}";
            }
        }

        await Assert.That(mismatches).IsEqualTo(0).Because(first);
#else
        await Task.CompletedTask;
#endif
    }

    /// <summary>The format table against the nearest-candidate search it replaced, for every 16-bit word.</summary>
    [Test]
    public async Task FormatTable_MatchesTheSearch_EveryWord()
    {
        var candidates = new ushort[32];
        for (var symbolNumber = 0; symbolNumber < 8; symbolNumber++)
        {
            MicroQRConstants.GetVersionAndEccFromSymbolNumber(symbolNumber, out var version, out var level);
            for (var mask = 0; mask < 4; mask++)
                candidates[symbolNumber * 4 + mask] = MicroQRConstants.GetFormatBits(version, level, mask);
        }

        var mismatches = 0;
        var accepted = 0;
        var first = "";
        for (var raw = 0; raw <= ushort.MaxValue; raw++)
        {
            var best = 0;
            var bestDistance = int.MaxValue;
            for (var i = 0; i < 32; i++)
            {
                var distance = System.Numerics.BitOperations.PopCount((uint)(raw ^ candidates[i]));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }
            var wantOk = bestDistance <= 3;
            MicroQRConstants.GetVersionAndEccFromSymbolNumber(best >> 2, out var wantVersion, out var wantLevel);

            var gotOk = MicroQRFormatInformationDecoder.TryDecode((ushort)raw, out var gotVersion, out var gotLevel, out var gotMask);
            var same = gotOk == wantOk && (!wantOk
                ? gotVersion == default && gotLevel == default && gotMask == -1
                : gotVersion == wantVersion && gotLevel == wantLevel && gotMask == (best & 3));
            if (!same && mismatches++ == 0)
                first = $"word {raw:X4}";
            if (wantOk)
                accepted++;
        }

        await Assert.That(mismatches).IsEqualTo(0).Because(first);
        // 32 balls of 576 words in 15 bits, and the 32 × 105 words at distance 3 once bit 15 is set
        await Assert.That(accepted).IsEqualTo(32 * 576 + 32 * 121);
    }

    private readonly record struct Outcome(DecodeStatus Status, MicroQRCodeDecodeInfo Info, int CharsWritten, string Destination);

    private static Outcome Decode(byte[] matrix, int size, bool reference)
    {
        var destination = new char[64];
        Array.Fill(destination, '~');
        var status = reference
            ? ReferenceDecode(matrix, size, destination, out var charsWritten, out var info)
            : MicroQRMatrixDecoder.DecodeMatrix(matrix, size, destination, out charsWritten, out info);
        return new Outcome(status, info, charsWritten, new string(destination));
    }

    private static Outcome Decode<TModules>(byte[] luminance, TModules modules, int size)
        where TModules : struct, IMicroQRModules
    {
        var destination = new char[64];
        Array.Fill(destination, '~');
        var status = MicroQRMatrixDecoder.DecodeMatrix(luminance, modules, size, destination, out var charsWritten, out var info);
        return new Outcome(status, info, charsWritten, new string(destination));
    }

    private static void Transpose(byte[] modules, int size)
    {
        for (var y = 0; y < size; y++)
        {
            for (var x = y + 1; x < size; x++)
                (modules[y * size + x], modules[x * size + y]) = (modules[x * size + y], modules[y * size + x]);
        }
    }

    /// <summary>The matrix decode before modules were read on demand: format bits and codewords straight from the matrix, function modules and masks tested per module.</summary>
    private static DecodeStatus ReferenceDecode(ReadOnlySpan<byte> modules, int size, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info)
    {
        charsWritten = 0;
        var version = MicroQRConstants.VersionFromSize(size);
        if (version == 0 || modules.Length < size * size)
        {
            info = new MicroQRCodeDecodeInfo(DecodeStatus.InvalidMatrix, 0, default, -1, 0);
            return DecodeStatus.InvalidMatrix;
        }

        var raw = 0;
        var bit = 14;
        for (var col = 1; col <= 8; col++, bit--)
        {
            if (modules[8 * size + col] != 0)
                raw |= 1 << bit;
        }
        for (var row = 7; row >= 1; row--, bit--)
        {
            if (modules[row * size + 8] != 0)
                raw |= 1 << bit;
        }
        if (!MicroQRFormatInformationDecoder.TryDecode((ushort)raw, out var formatVersion, out var eccLevel, out var maskPattern) || formatVersion != version)
        {
            info = new MicroQRCodeDecodeInfo(DecodeStatus.FormatInformationInvalid, version, default, -1, 0);
            return DecodeStatus.FormatInformationInvalid;
        }

        var dataBitCount = MicroQRConstants.GetDataBitCapacity(version, eccLevel);
        var dataCodewords = MicroQRConstants.GetDataCodewordCount(version, eccLevel);
        var eccCodewords = MicroQRConstants.GetEccCodewordCount(version, eccLevel);
        var block = new byte[dataCodewords + eccCodewords];
        var totalBits = dataBitCount + eccCodewords * 8;
        var bitIndex = 0;
        var upward = true;
        for (var right = size - 1; right >= 2 && bitIndex < totalBits; right -= 2)
        {
            for (var step = 0; step < size && bitIndex < totalBits; step++)
            {
                var row = upward ? size - 1 - step : step;
                for (var side = 0; side < 2 && bitIndex < totalBits; side++)
                {
                    var col = right - side;
                    if (MicroQRModulePlacer.IsFunctionModule(row, col))
                        continue;
                    var dark = (modules[row * size + col] != 0) ^ MicroQRModulePlacer.GetMaskBit(maskPattern, row, col);
                    if (dark)
                    {
                        if (bitIndex < dataBitCount)
                            block[bitIndex >> 3] |= (byte)(0x80 >> (bitIndex & 7));
                        else
                            block[dataCodewords + ((bitIndex - dataBitCount) >> 3)] |= (byte)(0x80 >> ((bitIndex - dataBitCount) & 7));
                    }
                    bitIndex++;
                }
            }
            upward = !upward;
        }

        if (!EccBinaryDecoder.TryCorrect(block, eccCodewords, out var errorsCorrected)
            || errorsCorrected > MicroQRConstants.GetErrorCorrectionCapacity(version, eccLevel))
        {
            info = new MicroQRCodeDecodeInfo(DecodeStatus.DataUncorrectable, version, eccLevel, maskPattern, errorsCorrected);
            return DecodeStatus.DataUncorrectable;
        }

        var status = MicroQRBinaryDecoder.DecodeBitStream(block.AsSpan(0, dataCodewords), dataBitCount, version, destination, out charsWritten);
        info = new MicroQRCodeDecodeInfo(status, version, eccLevel, maskPattern, errorsCorrected);
        return status;
    }

    private static readonly (string Text, MicroQREccLevel Level)[] Payloads =
    [
        ("12345", MicroQREccLevel.ErrorDetectionOnly),
        ("0123456789", MicroQREccLevel.L),
        ("12345678", MicroQREccLevel.M),
        ("HELLO WORLD 14", MicroQREccLevel.L),
        ("byte hi", MicroQREccLevel.M),
        ("HELLO WORLD PLUS 21ST", MicroQREccLevel.L),
        ("bytes m4 mode", MicroQREccLevel.M),
        ("bytes!!!!", MicroQREccLevel.Q),
        ("", MicroQREccLevel.L),
    ];

    private static (byte[] Matrix, int Size) SymbolMatrix(Random random, int damage)
    {
        var (text, level) = Payloads[random.Next(Payloads.Length)];
        var data = MicroQRCodeGenerator.Create(text, level, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });
        var size = data.Size;
        var matrix = new byte[size * size];
        for (var row = 0; row < size; row++)
        {
            for (var col = 0; col < size; col++)
                matrix[row * size + col] = data[row, col] ? (byte)1 : (byte)0;
        }
        for (var d = 0; d < damage; d++)
            matrix[random.Next(matrix.Length)] ^= 1;
        return (matrix, size);
    }

    /// <summary>Random modules under a valid format word, so the codewords reach error correction.</summary>
    private static (byte[] Matrix, int Size) RandomMatrix(Random random)
    {
        var size = Sizes[random.Next(Sizes.Length)];
        var matrix = new byte[size * size];
        for (var p = 0; p < matrix.Length; p++)
            matrix[p] = (byte)random.Next(2);
        var version = MicroQRConstants.VersionFromSize(size);
        var level = version switch
        {
            MicroQRVersion.M1 => MicroQREccLevel.ErrorDetectionOnly,
            MicroQRVersion.M4 => (MicroQREccLevel)random.Next(1, 4),
            _ => (MicroQREccLevel)random.Next(1, 3),
        };
        var format = MicroQRConstants.GetFormatBits(version, level, random.Next(4));
        var bit = 14;
        for (var col = 1; col <= 8; col++, bit--)
            matrix[8 * size + col] = (byte)(format >> bit & 1);
        for (var row = 7; row >= 1; row--, bit--)
            matrix[row * size + 8] = (byte)(format >> bit & 1);
        return (matrix, size);
    }

    private sealed record Scene(string Kind, byte[] Luminance, int Width, int Height, byte Threshold, float CenterX, float CenterY, float UX, float UY, float VX, float VY)
    {
        /// <summary>Kinds: 0 a symbol turned, framed on its finder; 1 the same framed a little off; 2 8-bit noise; 3 a symbol against the image edge, so the frame runs off it.</summary>
        public static Scene Build(Random random, int kind)
        {
            var ppm = 1.5f + (float)random.NextDouble() * 5f;
            var angle = (float)(random.NextDouble() * 2 * Math.PI);
            var cos = MathF.Cos(angle);
            var sin = MathF.Sin(angle);
            if (kind == 2)
            {
                var width = random.Next(40, 300);
                var height = random.Next(40, 300);
                var noise = new byte[width * height];
                random.NextBytes(noise);
                return new Scene("noise", noise, width, height, 128, (float)random.NextDouble() * width, (float)random.NextDouble() * height, ppm * cos, ppm * sin, -ppm * sin, ppm * cos);
            }

            var (text, level) = Payloads[random.Next(Payloads.Length)];
            var data = MicroQRCodeGenerator.Create(text, level, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });
            var span = (data.Size + 4) * ppm * 1.5f;
            var side = (int)span + 4;
            var luminance = new byte[side * side];
            // Symbol centre; against the edge the symbol's corner sits a module past the image
            var centreX = kind == 3 ? data.Size * ppm / 2f - ppm : side / 2f + (float)random.NextDouble();
            var centreY = kind == 3 ? data.Size * ppm / 2f - ppm : side / 2f + (float)random.NextDouble();
            for (var y = 0; y < side; y++)
            {
                for (var x = 0; x < side; x++)
                {
                    var dx = x + 0.5f - centreX;
                    var dy = y + 0.5f - centreY;
                    var gx = (cos * dx + sin * dy) / ppm + data.Size / 2f;
                    var gy = (-sin * dx + cos * dy) / ppm + data.Size / 2f;
                    var dark = gx >= 0 && gy >= 0 && gx < data.Size && gy < data.Size && data[(int)gy, (int)gx];
                    luminance[y * side + x] = dark ? (byte)30 : (byte)220;
                }
            }

            // The finder centre, grid (3.5, 3.5)
            var fx = centreX + cos * (3.5f - data.Size / 2f) * ppm - sin * (3.5f - data.Size / 2f) * ppm;
            var fy = centreY + sin * (3.5f - data.Size / 2f) * ppm + cos * (3.5f - data.Size / 2f) * ppm;
            var jitter = kind == 1 ? 0.3f * ppm : 0f;
            var scale = kind == 1 ? 0.97f + (float)random.NextDouble() * 0.06f : 1f;
            return new Scene(kind == 3 ? "against the edge" : kind == 1 ? "framed off" : "turned symbol", luminance, side, side, 128,
                fx + (float)(random.NextDouble() - 0.5) * 2 * jitter, fy + (float)(random.NextDouble() - 0.5) * 2 * jitter,
                ppm * scale * cos, ppm * scale * sin, -ppm * scale * sin, ppm * scale * cos);
        }
    }
}
