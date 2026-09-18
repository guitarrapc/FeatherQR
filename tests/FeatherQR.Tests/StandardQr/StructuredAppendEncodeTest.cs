using System.Text;
namespace FeatherQR.Tests;

/// <summary>
/// <see cref="QRCodeGenerator.CreateStructuredAppend"/> through the library's own decoder:
/// every symbol of a set decodes to its own part, the parts concatenate to the input, the
/// set shares one version, one count and one parity, and the parity is the XOR of the
/// whole input's bytes in the charset the set is written in. The option rules of the plan
/// each have a case that fails when the rule is dropped.
/// </summary>
public class StructuredAppendEncodeTest
{
    private const string Sentence = "The quick brown fox jumps over the lazy dog. ";

    [Test]
    [Arguments(600, 5, QREccLevel.M)]
    [Arguments(1201, 6, QREccLevel.M)]
    [Arguments(150, 1, QREccLevel.L)]
    public async Task Create_SplitsAndEverySymbolDecodesToItsOwnPart(int length, int maxVersion, QREccLevel ecc)
    {
        var text = Repeat(Sentence, length);
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(maxVersion) };

        var symbols = QRCodeGenerator.CreateStructuredAppend(text, ecc, options);

        await Assert.That(symbols.Length).IsBetween(2, 16);
        var parts = await DecodeSet(symbols, ecc);
        await Assert.That(string.Concat(parts)).IsEqualTo(text);
        await Assert.That(symbols.Select(s => s.Version).Distinct().Count()).IsEqualTo(1);
        await Assert.That(symbols[0].Version).IsLessThanOrEqualTo(maxVersion);
    }

    [Test]
    public async Task Create_TextThatFitsOneSymbol_ReturnsThePlainSymbol()
    {
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(10) };
        var expected = QRCodeGenerator.Create("fits in one", QREccLevel.M, options);

        var symbols = QRCodeGenerator.CreateStructuredAppend("fits in one", QREccLevel.M, options);

        await Assert.That(symbols.Length).IsEqualTo(1);
        await Assert.That(symbols[0].Version).IsEqualTo(expected.Version);
        await Assert.That(QRCodeDecoder.TryDecode(symbols[0], out var text, out var info)).IsTrue();
        await Assert.That(text).IsEqualTo("fits in one");
        await Assert.That(info.StructuredAppend.IsEmpty).IsTrue();
        await Assert.That(ModulesOf(symbols[0])).IsEquivalentTo(ModulesOf(expected));
    }

    [Test]
    public async Task Create_MoreThanSixteenSymbolsNeeded_ThrowsOnOptions()
    {
        var text = Repeat(Sentence, 600);
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(1) };

        var exception = Assert.Throws<ArgumentException>(() => QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.H, options));

        await Assert.That(exception.ParamName).IsEqualTo(nameof(options));
    }

    [Test]
    public async Task Create_ExactlySixteenSymbols_IsAccepted()
    {
        // Version 2-L holds 30 Byte-mode bytes beside the header; 470 characters need sixteen.
        var text = Repeat(Sentence, 470);
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(2) };

        var symbols = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, options);

        await Assert.That(symbols.Length).IsEqualTo(16);
        var parts = await DecodeSet(symbols, QREccLevel.L);
        await Assert.That(string.Concat(parts)).IsEqualTo(text);
    }

    [Test]
    [Arguments("ascii")]
    [Arguments("latin1")]
    [Arguments("utf8")]
    public async Task Create_ParityIsTheXorOfTheWholeTextInTheSetsCharset(string kind)
    {
        // Odd character counts of texts with an odd number of non-ASCII characters, so the
        // parity is non-zero and differs between charsets.
        var (text, bytes) = kind switch
        {
            "ascii" => (Repeat(Sentence, 301), Encoding.ASCII.GetBytes(Repeat(Sentence, 301))),
            "latin1" => (Repeat("Crème brûlée à la carte, jalapeño, naïve café. ", 329), Encoding.Latin1.GetBytes(Repeat("Crème brûlée à la carte, jalapeño, naïve café. ", 329))),
            _ => (Repeat("こんにちは世界、QRコードの分割テストです。", 67), Encoding.UTF8.GetBytes(Repeat("こんにちは世界、QRコードの分割テストです。", 67))),
        };
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(3) };

        var symbols = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.M, options);

        var expectedParity = bytes.Aggregate(0, (p, b) => p ^ b);
        await Assert.That(symbols.Length).IsGreaterThan(1);
        foreach (var symbol in symbols)
        {
            await Assert.That(QRCodeDecoder.TryDecode(symbol, out _, out var info)).IsTrue();
            await Assert.That((int)info.StructuredAppend.Parity).IsEqualTo(expectedParity);
        }
        var parts = await DecodeSet(symbols, QREccLevel.M);
        await Assert.That(string.Concat(parts)).IsEqualTo(text);
    }

    [Test]
    public async Task Create_Utf8Bom_IsWrittenOnceAndCountedInTheParity()
    {
        var text = Repeat("Zürich, naïve résumé, 🎉 emoji. ", 372);
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(3), EciMode = EciMode.Utf8, Utf8Bom = true };

        var symbols = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.M, options);

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text));
        var expectedParity = bytes.Aggregate(0, (p, b) => p ^ b);
        var parts = await DecodeSet(symbols, QREccLevel.M);
        await Assert.That(string.Concat(parts)).IsEqualTo(text).Because("the decoder consumes the byte order mark, and only symbol 0 carries one");
        await Assert.That(QRCodeDecoder.TryDecode(symbols[0], out _, out var first)).IsTrue();
        await Assert.That((int)first.StructuredAppend.Parity).IsEqualTo(expectedParity);
    }

    [Test]
    public async Task Create_NeverSplitsInsideASurrogatePair()
    {
        var text = Repeat("🎉🎊🎈", 120);
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(2) };

        var symbols = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, options);

        var parts = await DecodeSet(symbols, QREccLevel.L);
        foreach (var part in parts)
        {
            await Assert.That(char.IsLowSurrogate(part[0])).IsFalse();
            await Assert.That(char.IsHighSurrogate(part[^1])).IsFalse();
        }
        await Assert.That(string.Concat(parts)).IsEqualTo(text);
    }

    [Test]
    public async Task Create_MaskPattern_AppliesToEverySymbol()
    {
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(3), MaskPattern = 5 };

        var symbols = QRCodeGenerator.CreateStructuredAppend(Repeat(Sentence, 300), QREccLevel.M, options);

        foreach (var symbol in symbols)
        {
            await Assert.That(QRCodeDecoder.TryDecode(symbol, out _, out var info)).IsTrue();
            await Assert.That(info.MaskPattern).IsEqualTo(5);
        }
    }

    [Test]
    public async Task Create_QuietZone_AppliesToEverySymbol()
    {
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(3), QuietZoneSize = 1 };

        var symbols = QRCodeGenerator.CreateStructuredAppend(Repeat(Sentence, 300), QREccLevel.M, options);

        foreach (var symbol in symbols)
            await Assert.That((symbol.Size - (17 + 4 * symbol.Version)) / 2).IsEqualTo(1);
    }

    [Test]
    public async Task Create_BoostEccLevel_RaisesTheWholeSetToOneLevel()
    {
        // Sized so that a per-symbol boost would leave the symbols at different levels.
        var text = Repeat(Sentence, 470);
        var plain = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(2) };
        var boosted = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(2), BoostEccLevel = true };

        var plainSymbols = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, plain);
        var boostedSymbols = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, boosted);

        var plainLevels = new List<QREccLevel>();
        foreach (var symbol in plainSymbols)
        {
            await Assert.That(QRCodeDecoder.TryDecode(symbol, out _, out var info)).IsTrue();
            plainLevels.Add(info.EccLevel);
        }
        await Assert.That(plainLevels.Distinct()).IsEquivalentTo([QREccLevel.L]);

        var boostedLevels = new List<QREccLevel>();
        foreach (var symbol in boostedSymbols)
        {
            await Assert.That(QRCodeDecoder.TryDecode(symbol, out _, out var info)).IsTrue();
            boostedLevels.Add(info.EccLevel);
        }
        await Assert.That(boostedLevels.Distinct().Count()).IsEqualTo(1).Because("the boost is decided for the set, not per symbol");
        await Assert.That(boostedSymbols.Length).IsEqualTo(plainSymbols.Length);
        await Assert.That(boostedSymbols[0].Version).IsEqualTo(plainSymbols[0].Version);
    }

    [Test]
    [Arguments(QRSegmentation.Single)]
    [Arguments(QRSegmentation.Optimal)]
    public async Task Create_BoostEccLevel_ClimbsEveryLevelTheSetCanTake(QRSegmentation segmentation)
    {
        // Two symbols at a pinned version each fill about half of it, so the boost has room
        // to climb more than one level; that is the walk that asks every chunk again per
        // level, and the level it stops at must not depend on how the asking is done.
        var text = Repeat(Sentence, 280);
        var options = new QRCodeGeneratorOptions { Version = 10, BoostEccLevel = true, Segmentation = segmentation };

        var symbols = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, options);

        await Assert.That(symbols.Length).IsEqualTo(2);
        var levels = new List<QREccLevel>();
        foreach (var symbol in symbols)
        {
            await Assert.That(QRCodeDecoder.TryDecode(symbol, out _, out var info)).IsTrue();
            levels.Add(info.EccLevel);
        }
        await Assert.That(levels.Distinct().Count()).IsEqualTo(1);
        await Assert.That((int)levels[0]).IsGreaterThanOrEqualTo((int)QREccLevel.Q).Because("the set has room for more than one step up");
        await Assert.That(string.Concat(await DecodeSet(symbols, levels[0]))).IsEqualTo(text);
    }

    [Test]
    public async Task Create_OptimalSegmentation_RoundTripsAndNeverNeedsMoreSymbols()
    {
        // Runs of digits and letters: a mixed plan is cheaper than one Byte-mode stream.
        var text = Repeat("order 20260915 item 0000123456 qty 42 ", 600);
        var single = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(6) };
        var optimal = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(6), Segmentation = QRSegmentation.Optimal };

        var singleSymbols = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.M, single);
        var optimalSymbols = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.M, optimal);

        await Assert.That(optimalSymbols.Length).IsLessThanOrEqualTo(singleSymbols.Length);
        var parts = await DecodeSet(optimalSymbols, QREccLevel.M);
        await Assert.That(string.Concat(parts)).IsEqualTo(text);
    }

    [Test]
    [Arguments(1201, 6, QREccLevel.M)]
    [Arguments(150, 1, QREccLevel.L)]
    public async Task Create_OptimalSegmentation_OnDigits_IsTheSingleModeSet(int length, int maxVersion, QREccLevel ecc)
    {
        // One Numeric run is the optimum of all-digit content, so the set a plan would
        // build is the set the single mode builds; the planner may skip planning it, but
        // only while the two stay module for module identical.
        var text = Repeat("0123456789", length);
        var single = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(maxVersion) };
        var optimal = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(maxVersion), Segmentation = QRSegmentation.Optimal };

        var singleSymbols = QRCodeGenerator.CreateStructuredAppend(text, ecc, single);
        var optimalSymbols = QRCodeGenerator.CreateStructuredAppend(text, ecc, optimal);

        await Assert.That(optimalSymbols.Length).IsEqualTo(singleSymbols.Length);
        for (var i = 0; i < singleSymbols.Length; i++)
            await Assert.That(ModulesOf(optimalSymbols[i])).IsEquivalentTo(ModulesOf(singleSymbols[i])).Because($"symbol {i}");
    }

    [Test]
    public async Task Create_OptimalSegmentation_WithBoost_OnDigits_IsTheSingleModeSet()
    {
        // The boost re-costs every chunk per level, which is the other place planning is
        // skipped for digits; the boosted set must still match the single-mode one.
        var text = Repeat("0123456789", 1201);
        var single = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(6), BoostEccLevel = true };
        var optimal = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(6), BoostEccLevel = true, Segmentation = QRSegmentation.Optimal };

        var singleSymbols = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.M, single);
        var optimalSymbols = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.M, optimal);

        await Assert.That(optimalSymbols.Length).IsEqualTo(singleSymbols.Length);
        for (var i = 0; i < singleSymbols.Length; i++)
            await Assert.That(ModulesOf(optimalSymbols[i])).IsEquivalentTo(ModulesOf(singleSymbols[i])).Because($"symbol {i}");
        await Assert.That(string.Concat(await DecodeSet(optimalSymbols, QREccLevel.M))).IsEqualTo(text);
    }

    [Test]
    public async Task Create_ForcedEci_StillSplitsAndComesBack()
    {
        // ASCII text with a forced UTF-8 declaration still splits and comes back; that each symbol
        // carries the declaration cannot be seen here, since the decoder reports the same text
        // either way, and is read off the wire in StructuredAppendStreamTest. Its 12 bits a symbol can only add symbols.
        var text = Repeat(Sentence, 300);
        var plain = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(3) };
        var forced = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(3), EciMode = EciMode.Utf8 };

        var plainSymbols = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.M, plain);
        var forcedSymbols = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.M, forced);

        var parts = await DecodeSet(forcedSymbols, QREccLevel.M);
        await Assert.That(string.Concat(parts)).IsEqualTo(text);
        await Assert.That(forcedSymbols.Length).IsGreaterThanOrEqualTo(plainSymbols.Length);
    }

    [Test]
    public async Task Create_EmptyText_ReturnsOneSymbolLikeCreate()
    {
        var symbols = QRCodeGenerator.CreateStructuredAppend("", QREccLevel.L);

        await Assert.That(symbols.Length).IsEqualTo(1);
        await Assert.That(ModulesOf(symbols[0])).IsEquivalentTo(ModulesOf(QRCodeGenerator.Create("", QREccLevel.L)));
    }

    [Test]
    public async Task Create_UncappedVersion_ReturnsOneSymbolLikeCreate()
    {
        // Version 40 holds it, and no cap means no reason to split.
        var text = Repeat(Sentence, 1500);

        var symbols = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.M);

        await Assert.That(symbols.Length).IsEqualTo(1);
        await Assert.That(ModulesOf(symbols[0])).IsEquivalentTo(ModulesOf(QRCodeGenerator.Create(text, QREccLevel.M)));
    }

    [Test]
    public async Task Create_ExactVersion_UsesItForEverySymbol()
    {
        var options = new QRCodeGeneratorOptions { Version = 4 };

        var symbols = QRCodeGenerator.CreateStructuredAppend(Repeat(Sentence, 300), QREccLevel.M, options);

        await Assert.That(symbols.Length).IsGreaterThan(1);
        await Assert.That(symbols.Select(s => s.Version).Distinct()).IsEquivalentTo([4]);
        await Assert.That(string.Concat(await DecodeSet(symbols, QREccLevel.M))).IsEqualTo(Repeat(Sentence, 300));
    }

    [Test]
    public async Task Create_CharacterThatFitsNoSymbol_ThrowsOnOptions()
    {
        // One emoji under a UTF-8 declaration needs 76 bits; version 1-H holds 72.
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(1), EciMode = EciMode.Utf8 };

        var exception = Assert.Throws<ArgumentException>(() => QRCodeGenerator.CreateStructuredAppend("🎉🎉", QREccLevel.H, options));

        await Assert.That(exception.ParamName).IsEqualTo(nameof(options));
    }

    [Test]
    public async Task Create_ContradictoryOptions_ThrowLikeCreate()
    {
        var text = Repeat(Sentence, 300);

        await Assert.That(() => QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(3), QuietZoneSize = -1 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(3), Segmentation = (QRSegmentation)7 })).Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>Decodes every symbol, checks the set rules, and returns the parts in index order.</summary>
    private static async Task<string[]> DecodeSet(QRCodeData[] symbols, QREccLevel minimumEcc)
    {
        var parts = new string[symbols.Length];
        var parities = new HashSet<byte>();
        for (var i = 0; i < symbols.Length; i++)
        {
            await Assert.That(QRCodeDecoder.TryDecode(symbols[i], out var text, out var info)).IsTrue().Because($"symbol {i}: {info.Status}");
            await Assert.That(info.StructuredAppend.IsEmpty).IsFalse();
            await Assert.That(info.StructuredAppend.Index).IsEqualTo(i);
            await Assert.That(info.StructuredAppend.Count).IsEqualTo(symbols.Length);
            await Assert.That((int)info.EccLevel).IsGreaterThanOrEqualTo((int)minimumEcc);
            parities.Add(info.StructuredAppend.Parity);
            parts[i] = text;
        }
        await Assert.That(parities.Count).IsEqualTo(1);
        return parts;
    }

    private static byte[] ModulesOf(QRCodeData data)
    {
        var size = data.Size;
        var modules = new byte[size * size];
        for (var row = 0; row < size; row++)
            for (var col = 0; col < size; col++)
                modules[row * size + col] = data[row, col] ? (byte)1 : (byte)0;
        return modules;
    }

    private static string Repeat(string text, int length)
    {
        var sb = new StringBuilder(length);
        while (sb.Length < length)
            sb.Append(text);
        return sb.ToString(0, length);
    }
}
