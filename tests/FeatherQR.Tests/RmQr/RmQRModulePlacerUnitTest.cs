using FeatherQR.Internals;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="RmQRModulePlacer"/> reference implementation: function-module
/// predicate vs the independent naive painter, structural invariants of the placed
/// function patterns, format-copy read-back, and the module-exact oracle: placing
/// the same payload as every comparable committed external symbol must reproduce
/// that symbol module for module (libzint exactly; qrtool except its documented tail
/// defect). Legacy UTF-8 fixtures without ECI remain decoder-only oracles.
/// </summary>
public class RmQRModulePlacerUnitTest
{
    public static IEnumerable<RmQRVersion> AllVersions() => Enum.GetValues<RmQRVersion>();

    private static byte[] Place(RmQRVersion version, RmQREccLevel ecc, ReadOnlySpan<byte> finalMessage)
    {
        var core = new byte[RmQRConstants.GetWidth(version) * RmQRConstants.GetHeight(version)];
        core.AsSpan().Fill(0xCC); // dirty: every module must be written by the placer
        RmQRModulePlacer.PlaceSymbol(core, version, ecc, finalMessage);
        return core;
    }

    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task IsFunctionModule_MatchesNaivePainter_EveryModule(RmQRVersion version)
    {
        var height = RmQRConstants.GetHeight(version);
        var width = RmQRConstants.GetWidth(version);
        var expected = RmQRNaiveReference.FunctionModuleMap(height, width);
        var mismatches = 0;
        for (var row = 0; row < height; row++)
            for (var col = 0; col < width; col++)
                if (RmQRModulePlacer.IsFunctionModule(version, row, col) != expected[row * width + col])
                    mismatches++;
        await Assert.That(mismatches).IsEqualTo(0);
    }

    [Test]
    public async Task GetMaskBit_IsTheSingleRmqrMask()
    {
        for (var row = 0; row < 17; row++)
            for (var col = 0; col < 139; col++)
                if (RmQRModulePlacer.GetMaskBit(row, col) != RmQRNaiveReference.MaskBit(row, col))
                    Assert.Fail($"mask mismatch at ({row},{col})");
        await Assert.That(RmQRModulePlacer.GetMaskBit(0, 0)).IsTrue();
        await Assert.That(RmQRModulePlacer.GetMaskBit(0, 3)).IsFalse();
        await Assert.That(RmQRModulePlacer.GetMaskBit(2, 0)).IsFalse();
    }

    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task PlaceSymbol_FunctionPatterns_StructuralInvariants(RmQRVersion version)
    {
        var h = RmQRConstants.GetHeight(version);
        var w = RmQRConstants.GetWidth(version);
        var core = Place(version, RmQREccLevel.M, new byte[RmQRCodewordEncoder.GetFinalMessageSize(version)]);
        bool Dark(int r, int c) => core[r * w + c] == 1;

        // Every module written (no dirty 0xCC survives) and only 0/1 values.
        await Assert.That(core.All(b => b is 0 or 1)).IsTrue();

        // Finder 7×7: dark border, light ring, dark 3×3 center; separators light.
        for (var r = 0; r < 7; r++)
            for (var c = 0; c < 7; c++)
            {
                var ring = Math.Max(Math.Abs(r - 3), Math.Abs(c - 3));
                await Assert.That(Dark(r, c)).IsEqualTo(ring != 2).Because($"finder ({r},{c}) of {version}");
            }
        for (var r = 0; r < Math.Min(8, h); r++)
            await Assert.That(Dark(r, 7)).IsFalse().Because($"separator col 7 row {r} of {version}");
        if (h > 7)
            for (var c = 0; c <= 7; c++)
                await Assert.That(Dark(7, c)).IsFalse().Because($"separator row 7 col {c} of {version}");

        // Sub-finder 5×5 bottom-right: dark border, light ring, dark center.
        for (var r = 0; r < 5; r++)
            for (var c = 0; c < 5; c++)
            {
                var ring = Math.Max(Math.Abs(r - 2), Math.Abs(c - 2));
                await Assert.That(Dark(h - 5 + r, w - 5 + c)).IsEqualTo(ring != 1).Because($"sub-finder ({r},{c}) of {version}");
            }

        // Edge timing: top/bottom rows dark at even columns (outside finder/sub-finder/corners),
        // left/right columns dark at even rows.
        for (var c = 8; c < w - 5; c++)
        {
            await Assert.That(Dark(0, c)).IsEqualTo(c % 2 == 0 || RmQRNaiveReference.AlignmentColumns(w).Any(a => Math.Abs(a - c) <= 1)).Because($"top timing col {c} of {version}");
            await Assert.That(Dark(h - 1, c)).IsEqualTo(c % 2 == 0 || RmQRNaiveReference.AlignmentColumns(w).Any(a => Math.Abs(a - c) <= 1)).Because($"bottom timing col {c} of {version}");
        }
        for (var r = 8; r < h - 2; r++)
            await Assert.That(Dark(r, 0)).IsEqualTo(r % 2 == 0).Because($"left timing row {r} of {version}");
        for (var r = 2; r < h - 5; r++)
            await Assert.That(Dark(r, w - 1)).IsEqualTo(r % 2 == 0).Because($"right timing row {r} of {version}");

        // Corner patterns: top-right (0,w-1),(0,w-2),(1,w-1) dark, (1,w-2) light; bottom-left mirror.
        await Assert.That(Dark(0, w - 1)).IsTrue();
        await Assert.That(Dark(0, w - 2)).IsTrue();
        await Assert.That(Dark(1, w - 1)).IsTrue();
        await Assert.That(Dark(1, w - 2)).IsFalse();
        await Assert.That(Dark(h - 1, 0)).IsTrue();
        await Assert.That(Dark(h - 1, 1)).IsTrue();
        await Assert.That(Dark(h - 2, 0)).IsEqualTo(h != 9); // on height 9 that cell is separator row 7 (light), both lineages agree
        await Assert.That(Dark(h - 2, 1)).IsFalse();

        // Vertical timing columns and their 3×3 alignment patterns (dark ring, light center).
        foreach (var c in RmQRNaiveReference.AlignmentColumns(w))
        {
            for (var r = 3; r < h - 3; r++)
                await Assert.That(Dark(r, c)).IsEqualTo(r % 2 == 0).Because($"vertical timing ({r},{c}) of {version}");
            foreach (var top in new[] { 0, h - 3 })
                for (var dr = 0; dr < 3; dr++)
                    for (var dc = -1; dc <= 1; dc++)
                        await Assert.That(Dark(top + dr, c + dc)).IsEqualTo(!(dr == 1 && dc == 0)).Because($"alignment ({top + dr},{c + dc}) of {version}");
        }
    }

    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task PlaceSymbol_FormatCopies_ReadBackToTableWords_BothEcc(RmQRVersion version)
    {
        var h = RmQRConstants.GetHeight(version);
        var w = RmQRConstants.GetWidth(version);
        foreach (var ecc in new[] { RmQREccLevel.M, RmQREccLevel.H })
        {
            var core = Place(version, ecc, new byte[RmQRCodewordEncoder.GetFinalMessageSize(version)]);
            var (finderSide, subFinderSide) = RmQRNaiveReference.ReadFormatRegions(core, h, w);
            await Assert.That(finderSide).IsEqualTo(RmQRConstants.GetFormatBits(version, ecc, subFinderSide: false));
            await Assert.That(subFinderSide).IsEqualTo(RmQRConstants.GetFormatBits(version, ecc, subFinderSide: true));
        }
    }

    [Test]
    public async Task PlaceSymbol_RejectsUndersizedBuffers()
    {
        var message = new byte[RmQRCodewordEncoder.GetFinalMessageSize(RmQRVersion.R7x43)];
        await Assert.That(() => RmQRModulePlacer.PlaceSymbol(new byte[7 * 43 - 1], RmQRVersion.R7x43, RmQREccLevel.M, message)).Throws<ArgumentException>();
        await Assert.That(() => RmQRModulePlacer.PlaceSymbol(new byte[7 * 43], RmQRVersion.R7x43, RmQREccLevel.M, message.AsSpan(0, message.Length - 1).ToArray())).Throws<ArgumentException>();
    }

    public static IEnumerable<string> FixtureIdsWithoutEci() => RmQRBinaryEncoderUnitTest.FixtureIdsWithoutEci();

    [Test]
    [MethodDataSource(nameof(FixtureIdsWithoutEci))]
    public async Task PlaceSymbol_ReproducesEveryComparableExternalOracleSymbol_ModuleForModule(string fixtureId)
    {
        var fixture = FixtureLoader.Load("RmQr", fixtureId);
        var manifest = fixture.Manifest;
        var (oracle, width, height) = FixtureLoader.ReadRectangularMatrix(fixture.MatrixPath);
        RmQRConstants.TryGetVersion(height, width, out var version);
        var ecc = Enum.Parse<RmQREccLevel>(manifest.ErrorCorrectionLevel);

        var utf8 = manifest.PayloadText.Any(c => c > 0xFF);
        var analysis = manifest.Mode switch
        {
            "Numeric" => new TextAnalysisResult(EncodingMode.Numeric, EciMode.Default, manifest.PayloadText.Length),
            "Alphanumeric" => new TextAnalysisResult(EncodingMode.Alphanumeric, EciMode.Default, manifest.PayloadText.Length),
            _ => new TextAnalysisResult(EncodingMode.Byte, utf8 ? EciMode.Utf8 : EciMode.Default, utf8 ? System.Text.Encoding.UTF8.GetByteCount(manifest.PayloadText) : manifest.PayloadText.Length),
        };
        var data = new byte[RmQRConstants.GetDataCodewordCount(version, ecc)];
        RmQRBinaryEncoder.EncodeDataCodewords(manifest.PayloadText, version, ecc, in analysis, data);
        var message = new byte[RmQRCodewordEncoder.GetFinalMessageSize(version)];
        RmQRCodewordEncoder.AssembleFinalMessage(data, version, ecc, message);

        var ours = Place(version, ecc, message);

        // qrtool 0.13.2 never writes column 1, rows 8..h-3 (documented in the fixture record);
        // those modules are allowed to differ for that lineage only.
        var mismatches = new List<string>();
        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                if (ours[row * width + col] == oracle[row * width + col])
                    continue;
                if (manifest.Generator == "qrtool" && col == 1 && row >= 8 && row <= height - 3)
                    continue;
                mismatches.Add($"({row},{col})");
            }
        }
        await Assert.That(mismatches).IsEmpty().Because($"{fixtureId}: {manifest.Generator} {manifest.VersionName}-{manifest.ErrorCorrectionLevel} \"{manifest.PayloadText}\": {string.Join(" ", mismatches.Take(20))}");
    }
}
