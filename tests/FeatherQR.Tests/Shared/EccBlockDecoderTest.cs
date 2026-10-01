using TUnit.Assertions.Enums;
using FeatherQR.Internals;
using FeatherQR.Internals.BinaryDecoders;
using FeatherQR.Internals.BinaryEncoders;
using FeatherQR.Internals.RmQR;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="EccBlockDecoder"/> is the Reed-Solomon block stage both Standard QR's and rMQR's matrix decoders run:
/// the deinterleave, which has to be the exact inverse of the <see cref="BinaryInterleaver"/> both encoders run, and the
/// correction of each block, with the error count each decoder reports.
/// </summary>
public class EccBlockDecoderTest
{
    /// <summary>Every block structure of the two capacity tables, and a few outside them, by name; a test takes the name, since <see cref="ECCInfo"/> is internal.</summary>
    private static readonly Dictionary<string, ECCInfo> Structures = BuildStructures();

    public static IEnumerable<string> EveryBlockStructure() => Structures.Keys;

    private static Dictionary<string, ECCInfo> BuildStructures()
    {
        var structures = new Dictionary<string, ECCInfo>();
        for (var version = 1; version <= 40; version++)
            foreach (var level in new[] { QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H })
                structures[$"QR {version}-{level}"] = QRCodeConstants.GetEccInfo(version, level);
        foreach (var version in Enum.GetValues<RmQRVersion>())
            foreach (var level in new[] { RmQREccLevel.M, RmQREccLevel.H })
                structures[$"rMQR {version}-{level}"] = RmQRConstants.GetEccInfo(version, level);

        // Outside the capacity tables: equal block lengths across groups, one block per group, group 2 only.
        structures["group 1 only"] = new ECCInfo(1, QREccLevel.M, 6, 2, 2, 3, 0, 0);
        structures["1 + 1 blocks"] = new ECCInfo(5, QREccLevel.H, 7, 2, 1, 3, 1, 4);
        structures["equal lengths"] = new ECCInfo(5, QREccLevel.H, 12, 2, 2, 3, 2, 3);
        structures["group 2 only"] = new ECCInfo(5, QREccLevel.H, 8, 2, 0, 0, 2, 4);
        return structures;
    }

    [Test]
    [MethodDataSource(nameof(EveryBlockStructure))]
    public async Task Deinterleave_InvertsTheInterleaver(string name)
    {
        var eccInfo = Structures[name];

        // Each codeword is numbered by its position in the encoder's input, and the number is carried a byte at a time,
        // so every position is told apart even where the stream is longer than 256 codewords.
        var total = TotalCodewords(eccInfo);
        for (var shift = 0; shift < 16; shift += 8)
        {
            var data = new byte[eccInfo.TotalDataCodewords];
            var ecc = new byte[total - eccInfo.TotalDataCodewords];
            for (var i = 0; i < data.Length; i++)
                data[i] = (byte)(i >> shift);
            for (var i = 0; i < ecc.Length; i++)
                ecc[i] = (byte)((data.Length + i) >> shift);

            var interleaved = new byte[total];
            BinaryInterleaver.InterleaveCodewords(data, ecc, interleaved, eccInfo);

            var blocks = new byte[total];
            blocks.AsSpan().Fill(0xCC);
            EccBlockDecoder.Deinterleave(interleaved, blocks, eccInfo);

            await Assert.That(blocks).IsEquivalentTo(Blocks(data, ecc, eccInfo), CollectionOrdering.Matching).Because($"{name}, bits {shift}-{shift + 7} of each position");
        }
    }

    [Test]
    [MethodDataSource(nameof(EveryBlockStructure))]
    public async Task TryCorrect_CleanStream_GivesTheDataInBlockOrder(string name)
    {
        var eccInfo = Structures[name];
        var (stream, data) = Encode(eccInfo, seed: 1);

        var read = EccBlockDecoder.TryCorrect(stream, new byte[stream.Length], eccInfo, eccInfo.ECCPerBlock / 2, out var errorsCorrected);

        await Assert.That(read).IsTrue().Because(name);
        await Assert.That(errorsCorrected).IsEqualTo(0);
        await Assert.That(stream.AsSpan(0, data.Length).ToArray()).IsEquivalentTo(data, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("QR", 5, QREccLevel.Q)]   // 2 + 2 blocks of 15 and 16 data codewords
    [Arguments("QR", 40, QREccLevel.H)]  // 20 + 61 blocks
    [Arguments("rMQR", (int)RmQRVersion.R17x139, QREccLevel.H)]
    [Arguments("rMQR", (int)RmQRVersion.R13x77, QREccLevel.M)]
    public async Task TryCorrect_ErrorsInEveryBlock_CountsThemAll(string symbology, int version, QREccLevel level)
    {
        var eccInfo = EccInfo(symbology, version, level);
        var (stream, data) = Encode(eccInfo, seed: 2);
        var capacity = eccInfo.ECCPerBlock / 2;
        var injected = 0;
        for (var b = 0; b < BlockCount(eccInfo); b++)
            injected += Damage(stream, eccInfo, b, errors: 1 + b % capacity);

        var read = EccBlockDecoder.TryCorrect(stream, new byte[stream.Length], eccInfo, capacity, out var errorsCorrected);

        await Assert.That(read).IsTrue();
        await Assert.That(errorsCorrected).IsEqualTo(injected);
        await Assert.That(stream.AsSpan(0, data.Length).ToArray()).IsEquivalentTo(data, CollectionOrdering.Matching);
    }

    /// <summary>
    /// The stage stops at the first block that does not read and counts the blocks before it, not the ones after: every
    /// other block carries an error it could correct, so a stage that went on would count them too.
    /// </summary>
    [Test]
    [Arguments("QR", 5, QREccLevel.Q, "first")]   // 2 + 2 blocks
    [Arguments("QR", 5, QREccLevel.Q, "middle")]
    [Arguments("QR", 5, QREccLevel.Q, "last")]
    [Arguments("QR", 40, QREccLevel.H, "first")]  // 20 + 61 blocks
    [Arguments("QR", 40, QREccLevel.H, "middle")]
    [Arguments("rMQR", (int)RmQRVersion.R17x139, QREccLevel.H, "first")]
    [Arguments("rMQR", (int)RmQRVersion.R17x139, QREccLevel.H, "middle")]
    [Arguments("rMQR", (int)RmQRVersion.R17x139, QREccLevel.H, "last")]
    public async Task TryCorrect_UncorrectableBlock_StopsThere_CountingTheBlocksBeforeIt(string symbology, int version, QREccLevel level, string where)
    {
        // Every codeword of the failing block is changed, far past what its ECC codewords can locate.
        var eccInfo = EccInfo(symbology, version, level);
        var (stream, _) = Encode(eccInfo, seed: 3);
        var blocks = BlockCount(eccInfo);
        var failing = where switch { "first" => 0, "middle" => blocks / 2, _ => blocks - 1 };
        var before = 0;
        for (var b = 0; b < blocks; b++)
        {
            if (b == failing)
                Damage(stream, eccInfo, b, errors: BlockLength(eccInfo, b));
            else if (b < failing)
                before += Damage(stream, eccInfo, b, errors: 1);
            else
                Damage(stream, eccInfo, b, errors: 1);
        }

        var read = EccBlockDecoder.TryCorrect(stream, new byte[stream.Length], eccInfo, eccInfo.ECCPerBlock / 2, out var errorsCorrected);

        await Assert.That(read).IsFalse();
        await Assert.That(errorsCorrected).IsEqualTo(before).Because($"{blocks} blocks, block {failing} uncorrectable");
    }

    /// <summary>
    /// A block Reed-Solomon corrects but with more errors than the capacity allows is refused, and its errors are
    /// reported with the ones before it, the rule the capacity cap has in rMQR's and Micro QR's decoders; the blocks after
    /// it, each with an error it could correct, are not reached.
    /// </summary>
    [Test]
    [Arguments("QR", 5, QREccLevel.Q, 0)]
    [Arguments("QR", 5, QREccLevel.Q, 1)]
    [Arguments("rMQR", (int)RmQRVersion.R17x139, QREccLevel.H, 0)]
    [Arguments("rMQR", (int)RmQRVersion.R17x139, QREccLevel.H, 1)]
    public async Task TryCorrect_BlockOverTheCapacity_CountsItsErrorsToo(string symbology, int version, QREccLevel level, int refusedBlock)
    {
        var eccInfo = EccInfo(symbology, version, level);
        var (stream, _) = Encode(eccInfo, seed: 4);
        const int capacity = 2;
        var before = 0;
        for (var b = 0; b < refusedBlock; b++)
            before += Damage(stream, eccInfo, b, errors: capacity);
        var refused = Damage(stream, eccInfo, refusedBlock, errors: capacity + 1);
        for (var b = refusedBlock + 1; b < BlockCount(eccInfo); b++)
            Damage(stream, eccInfo, b, errors: 1);

        var read = EccBlockDecoder.TryCorrect(stream, new byte[stream.Length], eccInfo, capacity, out var errorsCorrected);

        await Assert.That(read).IsFalse();
        await Assert.That(errorsCorrected).IsEqualTo(before + refused);
    }

    /// <summary>A Standard QR version and level, or an rMQR version (its enum value) and level (M or H).</summary>
    private static ECCInfo EccInfo(string symbology, int version, QREccLevel level)
        => symbology == "QR"
            ? QRCodeConstants.GetEccInfo(version, level)
            : RmQRConstants.GetEccInfo((RmQRVersion)version, level == QREccLevel.M ? RmQREccLevel.M : RmQREccLevel.H);

    private static int BlockCount(in ECCInfo eccInfo) => eccInfo.BlocksInGroup1 + eccInfo.BlocksInGroup2;

    private static int TotalCodewords(in ECCInfo eccInfo) => eccInfo.TotalDataCodewords + BlockCount(eccInfo) * eccInfo.ECCPerBlock;

    private static int DataLength(in ECCInfo eccInfo, int block) => block < eccInfo.BlocksInGroup1 ? eccInfo.CodewordsInGroup1 : eccInfo.CodewordsInGroup2;

    private static int BlockLength(in ECCInfo eccInfo, int block) => DataLength(eccInfo, block) + eccInfo.ECCPerBlock;

    /// <summary>Each block's data then its ECC codewords, group 1 first, as the encoders split them.</summary>
    private static byte[] Blocks(byte[] data, byte[] ecc, in ECCInfo eccInfo)
    {
        var blocks = new List<byte>();
        var dataOffset = 0;
        for (var b = 0; b < BlockCount(eccInfo); b++)
        {
            var length = DataLength(eccInfo, b);
            blocks.AddRange(data.AsSpan(dataOffset, length).ToArray());
            blocks.AddRange(ecc.AsSpan(b * eccInfo.ECCPerBlock, eccInfo.ECCPerBlock).ToArray());
            dataOffset += length;
        }
        return [.. blocks];
    }

    /// <summary>The interleaved stream of random data codewords, each block's ECC computed as the encoders do.</summary>
    private static (byte[] Stream, byte[] Data) Encode(in ECCInfo eccInfo, int seed)
    {
        var data = new byte[eccInfo.TotalDataCodewords];
        new Random(seed).NextBytes(data);
        var ecc = new byte[BlockCount(eccInfo) * eccInfo.ECCPerBlock];
        var dataOffset = 0;
        for (var b = 0; b < BlockCount(eccInfo); b++)
        {
            var length = DataLength(eccInfo, b);
            EccBinaryEncoder.CalculateECC(data.AsSpan(dataOffset, length), ecc.AsSpan(b * eccInfo.ECCPerBlock, eccInfo.ECCPerBlock), eccInfo.ECCPerBlock);
            dataOffset += length;
        }
        var stream = new byte[TotalCodewords(eccInfo)];
        BinaryInterleaver.InterleaveCodewords(data, ecc, stream, eccInfo);
        return (stream, data);
    }

    /// <summary>Changes the first <paramref name="errors"/> codewords of one block where they sit in the stream, and returns how many it changed.</summary>
    private static int Damage(byte[] stream, in ECCInfo eccInfo, int block, int errors)
    {
        var origins = StreamOrigins(eccInfo);
        var dataStart = 0;
        for (var b = 0; b < block; b++)
            dataStart += DataLength(eccInfo, b);
        var dataLength = DataLength(eccInfo, block);
        var eccStart = eccInfo.TotalDataCodewords + block * eccInfo.ECCPerBlock;
        var changed = Math.Min(errors, BlockLength(eccInfo, block));
        for (var k = 0; k < changed; k++)
        {
            var origin = k < dataLength ? dataStart + k : eccStart + (k - dataLength);
            stream[Array.IndexOf(origins, origin)] ^= 0x5A;
        }
        return changed;
    }

    /// <summary>For each stream position, the codeword's position in the encoder's input (data, then ECC), found by interleaving the positions themselves a byte at a time.</summary>
    private static int[] StreamOrigins(in ECCInfo eccInfo)
    {
        var total = TotalCodewords(eccInfo);
        var origins = new int[total];
        var source = new byte[total];
        var interleaved = new byte[total];
        for (var shift = 0; shift < 16; shift += 8)
        {
            for (var i = 0; i < total; i++)
                source[i] = (byte)(i >> shift);
            BinaryInterleaver.InterleaveCodewords(source.AsSpan(0, eccInfo.TotalDataCodewords), source.AsSpan(eccInfo.TotalDataCodewords), interleaved, eccInfo);
            for (var s = 0; s < total; s++)
                origins[s] |= interleaved[s] << shift;
        }
        return origins;
    }
}
