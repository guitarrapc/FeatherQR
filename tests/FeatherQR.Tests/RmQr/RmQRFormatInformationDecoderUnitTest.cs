using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// rMQR format information decoding: each 18-bit copy is matched against the 64
/// valid words of its side (finder-side / sub-finder-side XOR masks), up to 3 bit
/// errors corrected (BCH(18,6) minimum distance ≥ 7); with two copies the closer
/// valid one wins (ties → finder side). Verified exhaustively against a naive
/// nearest-candidate reference over the full 18-bit space for both sides.
/// </summary>
public class RmQRFormatInformationDecoderUnitTest
{
    /// <summary>A raw word farther than 3 bits from every valid word of the side.</summary>
    private static int FindUndecodable(bool subFinderSide)
    {
        for (var raw = 0; raw < 1 << 18; raw++)
        {
            NaiveNearest(raw, subFinderSide, out var distance);
            if (distance > 3)
                return raw;
        }
        throw new InvalidOperationException("every 18-bit word decodes?");
    }

    private static int NaiveNearest(int raw, bool subFinderSide, out int distance)
    {
        var best = -1;
        distance = int.MaxValue;
        for (var index = 0; index < 64; index++)
        {
            var version = (RmQRVersion)((index & 31) + 1);
            var ecc = (RmQREccLevel)(index >> 5);
            var word = RmQRConstants.GetFormatBits(version, ecc, subFinderSide);
            var d = System.Numerics.BitOperations.PopCount((uint)(raw ^ word));
            if (d < distance)
            {
                distance = d;
                best = index;
            }
        }
        return best;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TryDecodeCopy_MatchesNaiveNearest_OverTheFull18BitSpace(bool subFinderSide)
    {
        var mismatches = 0;
        for (var raw = 0; raw < 1 << 18; raw++)
        {
            var expectedIndex = NaiveNearest(raw, subFinderSide, out var expectedDistance);
            var ok = RmQRFormatInformationDecoder.TryDecodeCopy(raw, subFinderSide, out var version, out var ecc, out var distance);
            var expectedOk = expectedDistance <= 3;
            if (ok != expectedOk)
            {
                mismatches++;
                continue;
            }
            if (ok && (((int)version - 1) | ((int)ecc << 5)) != expectedIndex || (ok && distance != expectedDistance))
                mismatches++;
        }
        await Assert.That(mismatches).IsEqualTo(0);
    }

    [Test]
    public async Task TryDecode_TwoCopies_CloserAgreeingCopyWins_TiesPreferFinderSide()
    {
        var left = RmQRConstants.GetFormatBits(RmQRVersion.R13x77, RmQREccLevel.H, false);
        var right = RmQRConstants.GetFormatBits(RmQRVersion.R13x77, RmQREccLevel.H, true);
        var leftM = RmQRConstants.GetFormatBits(RmQRVersion.R13x77, RmQREccLevel.M, false);
        var rightM = RmQRConstants.GetFormatBits(RmQRVersion.R13x77, RmQREccLevel.M, true);

        // Both clean → agree.
        await Assert.That(RmQRFormatInformationDecoder.TryDecode(left, right, RmQRVersion.R13x77, out var e, out var d)).IsTrue();
        await Assert.That((e, d)).IsEqualTo((RmQREccLevel.H, 0));

        // Finder side has 3 errors, sub-finder side clean → sub-finder wins with distance 0.
        await Assert.That(RmQRFormatInformationDecoder.TryDecode(left ^ 0b111, right, RmQRVersion.R13x77, out e, out d)).IsTrue();
        await Assert.That((e, d)).IsEqualTo((RmQREccLevel.H, 0));

        // Finder side beyond correction (4+ errors, chosen so no word is within 3), sub-finder 2 errors → sub-finder wins.
        var invalidLeft = FindUndecodable(false);
        var invalidRight = FindUndecodable(true);
        await Assert.That(RmQRFormatInformationDecoder.TryDecode(invalidLeft, right ^ 0b11, RmQRVersion.R13x77, out e, out d)).IsTrue();
        await Assert.That((e, d)).IsEqualTo((RmQREccLevel.H, 2));

        // Both copies agree on the version but name different ECC levels: the closer copy decides …
        await Assert.That(RmQRFormatInformationDecoder.TryDecode(leftM ^ 0b111, right ^ 0b1, RmQRVersion.R13x77, out e, out d)).IsTrue();
        await Assert.That((e, d)).IsEqualTo((RmQREccLevel.H, 1));
        await Assert.That(RmQRFormatInformationDecoder.TryDecode(left ^ 0b1, rightM ^ 0b111, RmQRVersion.R13x77, out e, out d)).IsTrue();
        await Assert.That((e, d)).IsEqualTo((RmQREccLevel.H, 1));
        // … and an exact tie goes to the finder side (the only observable tie-break among agreeing copies).
        await Assert.That(RmQRFormatInformationDecoder.TryDecode(leftM ^ 0b1, right ^ 0b1, RmQRVersion.R13x77, out e, out d)).IsTrue();
        await Assert.That((e, d)).IsEqualTo((RmQREccLevel.M, 1));
        await Assert.That(RmQRFormatInformationDecoder.TryDecode(left ^ 0b1, rightM ^ 0b1, RmQRVersion.R13x77, out e, out d)).IsTrue();
        await Assert.That((e, d)).IsEqualTo((RmQREccLevel.H, 1));

        // Both beyond the correction distance → fail.
        await Assert.That(RmQRFormatInformationDecoder.TryDecode(invalidLeft, invalidRight, RmQRVersion.R13x77, out _, out _)).IsFalse();
        await Assert.That(RmQRFormatInformationDecoder.TryDecodeCopy(invalidLeft, false, out _, out _, out _)).IsFalse();
    }

    [Test]
    public async Task TryDecode_WithExpectedVersion_OnlyAgreeingCopiesCount()
    {
        var left = RmQRConstants.GetFormatBits(RmQRVersion.R13x77, RmQREccLevel.H, false);
        var right = RmQRConstants.GetFormatBits(RmQRVersion.R13x77, RmQREccLevel.H, true);
        var otherLeft = RmQRConstants.GetFormatBits(RmQRVersion.R7x43, RmQREccLevel.M, false);
        var otherRight = RmQRConstants.GetFormatBits(RmQRVersion.R7x43, RmQREccLevel.M, true);

        // Both clean and agreeing → finder side, distance 0.
        await Assert.That(RmQRFormatInformationDecoder.TryDecode(left, right, RmQRVersion.R13x77, out var e, out var d)).IsTrue();
        await Assert.That((e, d)).IsEqualTo((RmQREccLevel.H, 0));

        // The finder-side copy is a closer valid word of ANOTHER version: the plain
        // arbitration would pick it, the dimension-aware one takes the agreeing copy.
        await Assert.That(RmQRFormatInformationDecoder.TryDecode(otherLeft ^ 0b1, right ^ 0b111, RmQRVersion.R13x77, out e, out d)).IsTrue();
        await Assert.That((e, d)).IsEqualTo((RmQREccLevel.H, 3));
        await Assert.That(RmQRFormatInformationDecoder.TryDecode(left ^ 0b111, otherRight, RmQRVersion.R13x77, out e, out d)).IsTrue();
        await Assert.That((e, d)).IsEqualTo((RmQREccLevel.H, 3));

        // Both agree → the closer one (sub-finder here).
        await Assert.That(RmQRFormatInformationDecoder.TryDecode(left ^ 0b111, right ^ 0b1, RmQRVersion.R13x77, out e, out d)).IsTrue();
        await Assert.That((e, d)).IsEqualTo((RmQREccLevel.H, 1));

        // Neither copy is a word of the expected version → fail (this is the
        // format-vs-dimension contradiction, not a version override).
        await Assert.That(RmQRFormatInformationDecoder.TryDecode(otherLeft, otherRight, RmQRVersion.R13x77, out _, out _)).IsFalse();
        await Assert.That(RmQRFormatInformationDecoder.TryDecode(FindUndecodable(false), FindUndecodable(true), RmQRVersion.R13x77, out _, out _)).IsFalse();
    }
}
