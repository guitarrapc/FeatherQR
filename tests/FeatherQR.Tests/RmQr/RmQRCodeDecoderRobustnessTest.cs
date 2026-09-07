using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// Damage equivalence classes for the rMQR matrix decoder: per-block Reed-Solomon
/// correction within / beyond ⌊ecc/2⌋ codewords, format copies damaged singly and
/// both within / beyond the BCH distance, format-vs-dimension contradiction, and
/// remainder-bit tolerance.
/// </summary>
public class RmQRCodeDecoderRobustnessTest
{
    public static IEnumerable<(RmQRVersion version, RmQREccLevel ecc)> AllVersionEcc()
    {
        foreach (var v in Enum.GetValues<RmQRVersion>())
        {
            yield return (v, RmQREccLevel.M);
            yield return (v, RmQREccLevel.H);
        }
    }

    private static (byte[] Modules, int Width, int Height, string Text) Symbol(RmQRVersion version, RmQREccLevel ecc)
    {
        var text = "R" + (int)version; // 2-3 alphanumeric chars: fits every version × ECC
        var size = Sizing.Required(text.AsSpan(), ecc, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = 0 });
        var modules = new byte[size.BufferSize];
        RmQRCodeGenerator.Create(text.AsSpan(), ecc, modules, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = 0 });
        return (modules, size.Width, size.Height, text);
    }

    /// <summary>
    /// The module coordinates of codeword <paramref name="codewordIndex"/> in
    /// placement (interleaved) order, from the naive walk.
    /// </summary>
    private static List<(int Row, int Col)> CodewordModules(int codewordIndex, int height, int width)
    {
        var function = RmQRNaiveReference.FunctionModuleMap(height, width);
        var coords = new List<(int, int)>();
        var index = 0;
        var upward = true;
        for (var col = width - 2; col >= 1; col -= 2)
        {
            for (var step = 0; step < height; step++)
            {
                var row = upward ? height - 1 - step : step;
                foreach (var c in new[] { col, col - 1 })
                {
                    if (function[row * width + c])
                        continue;
                    if (index >= codewordIndex * 8 && index < codewordIndex * 8 + 8)
                        coords.Add((row, c));
                    index++;
                }
            }
            upward = !upward;
        }
        return coords;
    }

    private static void FlipCodeword(byte[] modules, int width, int height, int codewordIndex)
    {
        foreach (var (r, c) in CodewordModules(codewordIndex, height, width))
            modules[r * width + c] ^= 1;
    }

    /// <summary>Interleaved stream index of data codeword <paramref name="k"/> of block <paramref name="block"/> (round-robin).</summary>
    private static int InterleavedIndexOfBlockCodeword(RmQRVersion version, RmQREccLevel ecc, int block, int k)
    {
        var info = RmQRConstants.GetEccInfo(version, ecc);
        var blocks = info.BlocksInGroup1 + info.BlocksInGroup2;
        var shortLength = info.CodewordsInGroup1;
        if (k < shortLength)
            return k * blocks + block;
        // The extra data codeword of the long blocks comes after all short-length rows.
        return shortLength * blocks + (block - info.BlocksInGroup1);
    }

    [Test]
    [MethodDataSource(nameof(AllVersionEcc))]
    public async Task Damage_WithinCorrectionCapacity_OfEveryBlock_IsCorrected(RmQRVersion version, RmQREccLevel ecc)
    {
        var info = RmQRConstants.GetEccInfo(version, ecc);
        var blocks = info.BlocksInGroup1 + info.BlocksInGroup2;
        // Deliberately the Reed-Solomon strength, NOT GetErrorCorrectionCapacity: this
        // is the test that pins "rMQR reserves no misdecode-protection codewords p", so
        // it must fail if the decoder's capacity ever drops below full RS strength.
        // Deriving t from the API under test makes it self-consistent with any wrong
        // (lower) capacity — verified by mutation: capacity - 1 leaves this whole class
        // green. RmQRConstantsUnitTest pins the capacity table against the same value.
        var t = info.ECCPerBlock / 2;
        var (modules, width, height, text) = Symbol(version, ecc);

        // Flip t whole data codewords in EVERY block (the maximum RS can correct per block).
        var damaged = (byte[])modules.Clone();
        var flipped = 0;
        for (var b = 0; b < blocks; b++)
        {
            var length = b < info.BlocksInGroup1 ? info.CodewordsInGroup1 : info.CodewordsInGroup2;
            for (var k = 0; k < Math.Min(t, length); k++)
            {
                FlipCodeword(damaged, width, height, InterleavedIndexOfBlockCodeword(version, ecc, b, k));
                flipped++;
            }
        }

        await Assert.That(RmQRCodeDecoder.TryDecode(damaged, width, height, out var decoded, out var decodeInfo)).IsTrue().Because($"{version}-{ecc}: {flipped} flipped codewords");
        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(decodeInfo.ErrorsCorrected).IsEqualTo(flipped);
    }

    [Test]
    [MethodDataSource(nameof(AllVersionEcc))]
    public async Task Damage_BeyondCorrectionCapacity_InOneBlock_IsRejected(RmQRVersion version, RmQREccLevel ecc)
    {
        var info = RmQRConstants.GetEccInfo(version, ecc);
        var t = info.ECCPerBlock / 2; // Reed-Solomon strength, see the within-capacity test
        var (modules, width, height, _) = Symbol(version, ecc);

        // t + 1 data codewords of block 0 flipped (needs at least t + 1 data codewords in the block; else flip ECC codewords too).
        var damaged = (byte[])modules.Clone();
        var blocks = info.BlocksInGroup1 + info.BlocksInGroup2;
        var dataInBlock0 = info.CodewordsInGroup1;
        for (var k = 0; k < t + 1; k++)
        {
            var index = k < dataInBlock0
                ? InterleavedIndexOfBlockCodeword(version, ecc, 0, k)
                : info.TotalDataCodewords + (k - dataInBlock0) * blocks; // ECC codeword k' of block 0
            FlipCodeword(damaged, width, height, index);
        }

        var ok = RmQRCodeDecoder.TryDecode(damaged, width, height, out _, out var decodeInfo);
        // RS cannot correct t + 1 errors; the overwhelmingly likely outcome is detection
        // (DataUncorrectable). A silent miscorrection to a *different* payload would be a
        // decode "success" with wrong text; assert we never claim success with the original.
        await Assert.That(ok && decodeInfo.ErrorsCorrected <= t).IsFalse().Because($"{version}-{ecc}: {t + 1} errors in one block must not decode cleanly");
        if (!ok)
            await Assert.That(decodeInfo.Status).IsEqualTo(DecodeStatus.DataUncorrectable);
    }

    [Test]
    public async Task FormatDamage_OneCopyDestroyed_OtherCopyDecodes_BothBeyondDistance_Fails()
    {
        var (modules, width, height, text) = Symbol(RmQRVersion.R13x77, RmQREccLevel.H);
        void FlipFinderSide(byte[] m, int bits)
        {
            // finder-side copy: rows 1-5 × cols 8-10 (bits 0-14), col 11 rows 1-3 (bits 15-17)
            var flipped = 0;
            for (var c = 0; c < 3 && flipped < bits; c++)
                for (var r = 0; r < 5 && flipped < bits; r++, flipped++)
                    m[(r + 1) * width + c + 8] ^= 1;
        }
        void FlipSubFinderSide(byte[] m, int bits)
        {
            var flipped = 0;
            for (var c = 0; c < 3 && flipped < bits; c++)
                for (var r = 0; r < 5 && flipped < bits; r++, flipped++)
                    m[(height - 6 + r) * width + width - 8 + c] ^= 1;
        }

        var oneDestroyed = (byte[])modules.Clone();
        FlipFinderSide(oneDestroyed, 15);
        await Assert.That(RmQRCodeDecoder.TryDecode(oneDestroyed, width, height, out var d1, out var i1)).IsTrue();
        await Assert.That(d1).IsEqualTo(text);
        await Assert.That(i1.ErrorsCorrected).IsEqualTo(0);

        var bothWithin = (byte[])modules.Clone();
        FlipFinderSide(bothWithin, 3);
        FlipSubFinderSide(bothWithin, 3);
        await Assert.That(RmQRCodeDecoder.TryDecode(bothWithin, width, height, out var d2, out _)).IsTrue();
        await Assert.That(d2).IsEqualTo(text);

        var bothBeyond = (byte[])modules.Clone();
        FlipFinderSide(bothBeyond, 15);
        FlipSubFinderSide(bothBeyond, 15);
        var ok = RmQRCodeDecoder.TryDecode(bothBeyond, width, height, out _, out var i3);
        // 15 flips per copy may by chance land within 3 bits of another word (then the
        // version cross-check or RS rejects it); a clean success with the original text is impossible.
        await Assert.That(ok).IsFalse();
        await Assert.That(i3.Status == DecodeStatus.FormatInformationInvalid || i3.Status == DecodeStatus.DataUncorrectable).IsTrue();
    }

    /// <summary>
    /// One copy miscorrected toward ANOTHER version's word (distance 2 to the impostor,
    /// 6 to the truth; the two words are 8 apart) while the other copy carries 3
    /// correctable errors: a "closer copy wins" arbitration would name the wrong
    /// version whichever side carries the impostor, but the copy that agrees with the
    /// physical dimensions is valid, so the symbol must still decode ("either copy
    /// alone suffices"). Both roles are exercised and both regress under the old rule.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task FormatDamage_ImpostorCopyClaimsAnotherVersion_CopyAgreeingWithDimensionsWins(bool impostorOnFinderSide)
    {
        var (modules, width, height, text) = Symbol(RmQRVersion.R7x43, RmQREccLevel.M);
        var truth = RmQRConstants.GetFormatBits(RmQRVersion.R7x43, RmQREccLevel.M, subFinderSide: !impostorOnFinderSide);
        var impostor = RmQRConstants.GetFormatBits(RmQRVersion.R7x59, RmQREccLevel.M, subFinderSide: !impostorOnFinderSide);
        var differing = truth ^ impostor;
        // Flip 6 of the 8 differing bits toward the impostor: distance 6 from the truth
        // (undecodable as such), 2 from the impostor (a valid word of another version).
        var damaged = truth;
        var flipped = 0;
        for (var bit = 0; bit < 18 && flipped < 6; bit++)
        {
            if ((differing & (1 << bit)) != 0)
            {
                damaged ^= 1 << bit;
                flipped++;
            }
        }
        await Assert.That(flipped).IsEqualTo(6);

        void WriteCopy(byte[] m, int word, bool subFinderSide)
        {
            for (var c = 0; c < 3; c++)
                for (var r = 0; r < 5; r++)
                {
                    var index = subFinderSide ? (height - 6 + r) * width + width - 8 + c : (r + 1) * width + c + 8;
                    m[index] = (byte)((word >> (c * 5 + r)) & 1);
                }
            for (var k = 0; k < 3; k++)
            {
                var index = subFinderSide ? (height - 6) * width + width - 5 + k : (k + 1) * width + 11;
                m[index] = (byte)((word >> (15 + k)) & 1);
            }
        }

        var otherTruth = RmQRConstants.GetFormatBits(RmQRVersion.R7x43, RmQREccLevel.M, subFinderSide: impostorOnFinderSide);
        var otherDamaged = otherTruth ^ 0b111; // 3 correctable errors on the copy that agrees with the dimensions

        var damagedModules = (byte[])modules.Clone();
        WriteCopy(damagedModules, damaged, subFinderSide: !impostorOnFinderSide);
        WriteCopy(damagedModules, otherDamaged, subFinderSide: impostorOnFinderSide);

        await Assert.That(RmQRCodeDecoder.TryDecode(damagedModules, width, height, out var decoded, out var info)).IsTrue()
            .Because($"impostorOnFinderSide={impostorOnFinderSide}, status={info.Status}");
        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.Version).IsEqualTo(RmQRVersion.R7x43);
        await Assert.That(info.EccLevel).IsEqualTo(RmQREccLevel.M);
    }

    [Test]
    public async Task FormatClaimingAnotherVersion_ThanTheMatrixDimensions_IsRejected()
    {
        // Place an R7x59 symbol's format words into an R7x77-shaped matrix: both copies decode
        // cleanly to R7x59, but the physical size says R7x77.
        var (modules, width, height, _) = Symbol(RmQRVersion.R7x77, RmQREccLevel.M);
        var forged = (byte[])modules.Clone();
        var left = RmQRConstants.GetFormatBits(RmQRVersion.R7x59, RmQREccLevel.M, false);
        var right = RmQRConstants.GetFormatBits(RmQRVersion.R7x59, RmQREccLevel.M, true);
        for (var c = 0; c < 3; c++)
            for (var r = 0; r < 5; r++)
            {
                forged[(r + 1) * width + c + 8] = (byte)((left >> (c * 5 + r)) & 1);
                forged[(height - 6 + r) * width + width - 8 + c] = (byte)((right >> (c * 5 + r)) & 1);
            }
        for (var k = 0; k < 3; k++)
        {
            forged[(k + 1) * width + 11] = (byte)((left >> (15 + k)) & 1);
            forged[(height - 6) * width + width - 5 + k] = (byte)((right >> (15 + k)) & 1);
        }

        await Assert.That(RmQRCodeDecoder.TryDecode(forged, width, height, out _, out var info)).IsFalse();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.FormatInformationInvalid);
    }

    [Test]
    public async Task RemainderBits_Set_AreIgnored()
    {
        // R11x99 has 7 remainder bits at the end of the walk; setting them dark must not matter.
        var version = RmQRVersion.R11x99;
        var (modules, width, height, text) = Symbol(version, RmQREccLevel.M);
        var total = RmQRConstants.GetTotalCodewordCount(version);
        var function = RmQRNaiveReference.FunctionModuleMap(height, width);
        var index = 0;
        var upward = true;
        for (var col = width - 2; col >= 1; col -= 2)
        {
            for (var step = 0; step < height; step++)
            {
                var row = upward ? height - 1 - step : step;
                foreach (var c in new[] { col, col - 1 })
                {
                    if (function[row * width + c])
                        continue;
                    if (index >= total * 8)
                        modules[row * width + c] ^= 1;
                    index++;
                }
            }
            upward = !upward;
        }
        await Assert.That(RmQRCodeDecoder.TryDecode(modules, width, height, out var decoded, out var info)).IsTrue();
        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.ErrorsCorrected).IsEqualTo(0);
    }
}
