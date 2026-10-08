using FeatherQR;
using FeatherQR.Internals;
using FeatherQR.Internals.BinaryEncoders;
using FeatherQR.Internals.MicroQR;
using FeatherQR.Internals.RmQR;
using FeatherQR.Internals.StandardQR;

/// <summary>
/// The encode pipeline stage by stage (<c>--shape stage/</c>): each stage of one symbol timed alone, on the input the stage
/// before it produced, beside the same symbol's end-to-end rows. A stage's share of an encode on a build is its time over the
/// quiet-zone-free row's. The stages are composed here the way the generators compose them, and each symbol's composition is
/// checked once against the generator's own matrix, so a stage row stops being built the day it no longer times the encode.
/// </summary>
/// <remarks>
/// Mask selection masks in place, so its row restores the unmasked matrix each call and the copy is timed alone beside it.
/// The forced rows apply pattern 0 or 3 through the path a pinned <see cref="QRCodeGeneratorOptions.MaskPattern"/> takes, and the
/// <c>e2e-qz0-maskN</c> rows encode with the pattern pinned.
/// </remarks>
internal static partial class TierTiming
{
    private const string AlphanumericAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    /// <summary>The URL of the benchmark's version 6-M row (QRCodeEncodeEndToEnd), which <see cref="Url"/> is not: it fits version 5.</summary>
    private const string BenchmarkUrl = "https://github.com/guitarrapc/FeatherQR/blob/main/README.md?foo=sample&bar=dummy&baz=42";

    private static Shape[] EncodeStageShapes() =>
    [
        .. QRStageShapes("qr-v1-num-L", () => "0123456789", QREccLevel.L, 1),
        .. QRStageShapes("qr-v1-alnum-M", () => "HELLO WORLD 2026", QREccLevel.M, 1),
        .. QRStageShapes("qr-v6-url-M", () => BenchmarkUrl, QREccLevel.M, 6),
        .. QRStageShapes("qr-v10-alnum-M", () => Pick(300, AlphanumericAlphabet), QREccLevel.M, 10),
        // The smallest version the transposed scorer takes.
        .. QRStageShapes("qr-v12-byte-M", () => DeterministicText(270), QREccLevel.M, 12),
        .. QRStageShapes("qr-v19-byte-M", () => DeterministicText(620), QREccLevel.M, 19),
        // 1,000 characters at M, where a pinned mask once encoded in twice the automatic time (the forced rows below).
        .. QRStageShapes("qr-v26-byte-M", () => DeterministicText(1000), QREccLevel.M, 26),
        .. QRStageShapes("qr-v40-byte-L", () => DeterministicText(2900), QREccLevel.L, 40),
        .. QRStageShapes("qr-v39-byte-H", () => DeterministicText(1200), QREccLevel.H, 39),
        .. QRStageShapes("qr-v40-alnum-L", () => Pick(4296, AlphanumericAlphabet), QREccLevel.L, 40),
        .. QRStageShapes("qr-v40-num-L", () => Pick(7089, "0123456789"), QREccLevel.L, 40),
        // Structured Append sets of one mode, where the payload writer runs over the whole text: the writer plan's set shapes.
        .. SetStageShapes("sa-alnum-30000-L", () => Pick(30000, AlphanumericAlphabet), QREccLevel.L, 7),
        .. SetStageShapes("sa-num-50000-L", () => Pick(50000, "0123456789"), QREccLevel.L, 8),
        .. MicroStageShapes("micro-m2-num", "0123456789", MicroQREccLevel.L, MicroQRVersion.M2),
        .. MicroStageShapes("micro-m3-alnum", "HELLO WORLD 14", MicroQREccLevel.L, MicroQRVersion.M3),
        .. MicroStageShapes("micro-m4-byte", "bytes m4 mode", MicroQREccLevel.M, MicroQRVersion.M4),
        .. RmQRStageShapes("rmqr-r7x43-num", "012345678901", RmQRVersion.R7x43),
        .. RmQRStageShapes("rmqr-r11x59-alnum", "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 $%*+-.", RmQRVersion.R11x59),
        .. RmQRStageShapes("rmqr-r17x139-byte", RmQRByte, RmQRVersion.R17x139),
    ];

    /// <summary>A text of <paramref name="length"/> characters drawn from <paramref name="alphabet"/> with a fixed seed.</summary>
    private static string Pick(int length, string alphabet)
    {
        var random = new Random(7);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = alphabet[random.Next(alphabet.Length)];
        return new string(chars);
    }

    // ---- Standard QR ----

    /// <summary>One Standard QR symbol's intermediate products, built once and shared by its stage rows.</summary>
    private sealed class QRStages
    {
        public readonly string Text;
        public readonly QREccLevel Ecc;
        public readonly TextAnalysisResult Analysis;
        public readonly int Version;
        public readonly ECCInfo EccInfo;
        public readonly byte[] Data;
        public readonly int DataLength;
        public readonly byte[] Ecc2;
        public readonly byte[] Interleaved;
        public readonly ModulePlacer.PlacementLayout Layout;
        public readonly byte[] Placed;     // function patterns and data, unmasked
        public readonly byte[] Final;      // masked, format and version information in
        public readonly byte[] Work;
        public readonly byte[] Destination = new byte[1 << 16];

        public QRStages(string text, QREccLevel ecc, int expectedVersion)
        {
            Text = text;
            Ecc = ecc;
            Analysis = TextAnalyzer.Analyze(text, EciMode.Default, allowKanji: false);
            if (!QRCodeGenerator.TryGetVersion(Analysis.DataLength, Analysis.EncodingMode, ecc, Analysis.EciMode, utf8BOM: false, out Version) || Version != expectedVersion)
                throw new InvalidOperationException($"{text.Length} characters at {ecc} are not version {expectedVersion}, the version the shape is named for");
            EccInfo = QRCodeConstants.GetEccInfo(Version, ecc);
            Data = new byte[EccInfo.TotalDataCodewords];
            DataLength = WriteData();
            Ecc2 = new byte[(EccInfo.BlocksInGroup1 + EccInfo.BlocksInGroup2) * EccInfo.ECCPerBlock];
            ReedSolomon();
            Interleaved = new byte[BinaryInterleaver.CalculateInterleavedSize(EccInfo, QRCodeConstants.GetRemainderBits(Version))];
            Interleave();
            Layout = ModulePlacer.GetLayout(Version);
            Placed = new byte[Layout.Size * Layout.Size];
            Work = new byte[Placed.Length];
            Place(Placed);
            Final = (byte[])Placed.Clone();
            var mask = ModulePlacer.MaskCode(Final, Layout.Size, Version, Layout.BlockedMask, ecc);
            FormatAndVersion(Final, mask);

            // The composition above must be the encode: the generator's own matrix, quiet zone 0, byte for byte.
            var written = QRCodeGenerator.Create(text, ecc, Destination, new QRCodeGeneratorOptions { QuietZoneSize = 0 });
            if (written != Final.Length || !Destination.AsSpan(0, written).SequenceEqual(Final))
                throw new InvalidOperationException($"the stage composition no longer reproduces the encode of version {Version}-{ecc}");
        }

        public int WriteData()
        {
            var encoder = new QRBinaryEncoder(Data);
            encoder.WriteMode(Analysis.EncodingMode, Analysis.EciMode);
            encoder.WriteCharacterCount(Analysis.DataLength, Analysis.EncodingMode.GetCountIndicatorLength(Version));
            encoder.WriteData(Text, Analysis.EncodingMode, Analysis.EciMode, utf8Bom: false);
            encoder.WritePadding(EccInfo.TotalDataCodewords * 8);
            return encoder.ByteCount;
        }

        /// <summary>The payload writer alone (no mode, count or padding) into <paramref name="target"/>: the writer's own share of the data stage.</summary>
        public int WritePayload(byte[] target)
        {
            var encoder = new QRBinaryEncoder(target);
            encoder.WriteData(Text, Analysis.EncodingMode, Analysis.EciMode, utf8Bom: false);
            return encoder.BitPosition;
        }

        /// <summary>The payload's length in bits by the mode's definition, to check the payload row against.</summary>
        public int PayloadBits() => PayloadBitsOf(Analysis.EncodingMode, Analysis.DataLength);

        public int ReedSolomon()
        {
            EccBinaryEncoder.CalculateECCBlocks(Data.AsSpan(0, DataLength), Ecc2, EccInfo);
            return Ecc2[0];
        }

        public int Interleave()
        {
            BinaryInterleaver.InterleaveCodewords(Data.AsSpan(0, DataLength), Ecc2, Interleaved, EccInfo);
            return Interleaved[0];
        }

        public int Place(byte[] target)
        {
            Layout.Template.AsSpan().CopyTo(target);
            ModulePlacer.PlaceDataWords(target, Layout, Interleaved);
            return target[^1];
        }

        // The placed route's last step. The stream route (the mask-stream row) writes the version information with the winner, so
        // at versions 12 to 40 on AVX2 this row also times a PlaceVersion the encoder skips.
        public int FormatAndVersion(byte[] target, int mask)
        {
            ModulePlacer.PlaceFormat(target, Layout.Size, QRCodeConstants.GetFormatBits(Ecc, mask));
            if (Version >= 7)
                ModulePlacer.PlaceVersion(target, Layout.Size, QRCodeConstants.GetVersionBits(Version));
            return target[8];
        }
    }

    /// <summary>
    /// A pinned pattern applied to the unmasked matrix. The pattern is a captured variable, as the generator passes it: given as a
    /// literal, the JIT inlined the call into the row and folded the predicate per pattern, which timed the version 26 path at half
    /// what an encode pays for it.
    /// </summary>
    private static Func<int> ForcedMask(QRStages s, int pattern) => () =>
    {
        s.Placed.AsSpan().CopyTo(s.Work);
        ModulePlacer.ApplyMaskPattern(s.Work, s.Layout.Size, s.Layout.BlockedMask, pattern);
        return s.Work[s.Layout.Size + 1];
    };

    private static Shape[] QRStageShapes(string name, Func<string> text, QREccLevel ecc, int version)
    {
        // One symbol's products for all of its rows, built on first use: the selected rows build only what they time.
        var stages = new Lazy<QRStages>(() => new QRStages(text(), ecc, version));
        return
        [
            new($"stage/{name}/e2e", () => { var s = stages.Value; return Checked(() => QRCodeGenerator.Create(s.Text, s.Ecc, s.Destination), FramedLength(s.Layout.Size, 4)); }),
            new($"stage/{name}/e2e-qz0", () => { var s = stages.Value; var options = new QRCodeGeneratorOptions { QuietZoneSize = 0 }; return Checked(() => QRCodeGenerator.Create(s.Text, s.Ecc, s.Destination, options), s.Final.Length); }),
            new($"stage/{name}/e2e-qz0-mask0", () => { var s = stages.Value; var options = new QRCodeGeneratorOptions { QuietZoneSize = 0, MaskPattern = 0 }; return Checked(() => QRCodeGenerator.Create(s.Text, s.Ecc, s.Destination, options), s.Final.Length); }),
            new($"stage/{name}/e2e-qz0-mask3", () => { var s = stages.Value; var options = new QRCodeGeneratorOptions { QuietZoneSize = 0, MaskPattern = 3 }; return Checked(() => QRCodeGenerator.Create(s.Text, s.Ecc, s.Destination, options), s.Final.Length); }),
            new($"stage/{name}/e2e-class", () => { var s = stages.Value; return Checked(() => QRCodeGenerator.Create(s.Text, s.Ecc).Size, s.Layout.Size + 8); }),
            new($"stage/{name}/analyze", () => { var s = stages.Value; return Checked(() => TextAnalyzer.Analyze(s.Text, EciMode.Default, allowKanji: false).DataLength, s.Analysis.DataLength); }),
            new($"stage/{name}/version", () =>
            {
                var s = stages.Value;
                return Checked(() => QRCodeGenerator.TryGetVersion(s.Analysis.DataLength, s.Analysis.EncodingMode, s.Ecc, s.Analysis.EciMode, utf8BOM: false, out var version) ? version : -1, s.Version);
            }),
            new($"stage/{name}/data", () => { var s = stages.Value; return Checked(s.WriteData, s.DataLength); }),
            new($"stage/{name}/payload", () => { var s = stages.Value; var target = new byte[s.Data.Length]; return Checked(() => s.WritePayload(target), s.PayloadBits()); }),
            new($"stage/{name}/rs", () => stages.Value.ReedSolomon),
            new($"stage/{name}/interleave", () => stages.Value.Interleave),
            new($"stage/{name}/place", () => { var s = stages.Value; return () => s.Place(s.Work); }),
            new($"stage/{name}/mask-copy", () => { var s = stages.Value; return () => { s.Placed.AsSpan().CopyTo(s.Work); return s.Work[s.Layout.Size + 1]; }; }),
            new($"stage/{name}/mask", () =>
            {
                var s = stages.Value;
                return () =>
                {
                    s.Placed.AsSpan().CopyTo(s.Work);
                    return ModulePlacer.MaskCode(s.Work, s.Layout.Size, s.Version, s.Layout.BlockedMask, s.Ecc);
                };
            }),
            // Placement and selection together, the two routes the generator can take: the template copy, the placed bytes and the
            // selection on them, and on a build with the stream form, the stream placed straight into the selection's column words.
            new($"stage/{name}/place-mask", () =>
            {
                var s = stages.Value;
                return () =>
                {
                    s.Place(s.Work);
                    return ModulePlacer.MaskCode(s.Work, s.Layout.Size, s.Version, s.Layout.BlockedMask, s.Ecc);
                };
            }),
            new($"stage/{name}/mask-stream", () =>
            {
                var s = stages.Value;
                return () => ModulePlacer.TryMaskCodeFromStream(s.Work, s.Version, s.Interleaved, s.Ecc, out var pattern) ? pattern : -1;
            }),
            new($"stage/{name}/mask-forced0", () => ForcedMask(stages.Value, 0)),
            new($"stage/{name}/mask-forced3", () => ForcedMask(stages.Value, 3)),
            new($"stage/{name}/format", () => { var s = stages.Value; return () => s.FormatAndVersion(s.Work, 3); }),
            new($"stage/{name}/pack", () =>
            {
                var s = stages.Value;
                return () =>
                {
                    var data = new QRCodeData(s.Version, 4);
                    data.SetCoreData(s.Final);
                    return data.Size;
                };
            }),
        ];
    }

    private static int FramedLength(int coreSize, int quietZone) => (coreSize + 2 * quietZone) * (coreSize + 2 * quietZone);

    /// <summary>A payload's length in bits: 10 per three digits (7 or 4 for the rest), 11 per two alphanumerics (6 for the last), 8 per byte.</summary>
    private static int PayloadBitsOf(EncodingMode mode, int count) => mode switch
    {
        EncodingMode.Numeric => 10 * (count / 3) + (count % 3 == 2 ? 7 : count % 3 == 1 ? 4 : 0),
        EncodingMode.Alphanumeric => 11 * (count / 2) + 6 * (count % 2),
        EncodingMode.Byte => 8 * count,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "no payload row for this mode"),
    };

    // ---- Structured Append ----

    /// <summary>
    /// One Structured Append set's chunks, as the planner splits them under <see cref="QRSegmentation.Single"/>: every chunk is one run
    /// in the text's mode, so the payload row is the writer over the whole text in the set's chunks.
    /// </summary>
    private sealed class SetStages
    {
        public readonly string Text;
        public readonly QREccLevel Ecc;
        public readonly EncodingMode Mode;
        public readonly int Count;
        public readonly int[] ChunkEnds;
        public readonly byte[] Target;

        public SetStages(string text, QREccLevel ecc, int expectedSymbols)
        {
            Text = text;
            Ecc = ecc;
            Mode = TextAnalyzer.Analyze(text, EciMode.Default, allowKanji: false).EncodingMode;
            Span<int> ends = stackalloc int[StructuredAppendPlanner.MaxSymbols];
            if (!StructuredAppendPlanner.TryPlan(text, ecc, EciMode.Default, Mode, utf8Bom: false, QRSegmentation.Single, 1, 40, ends, out Count, out var version, out _)
                || Count != expectedSymbols)
                throw new InvalidOperationException($"{text.Length} characters at {ecc} are not a set of {expectedSymbols} symbols, the count the shape is named for");
            ChunkEnds = ends.Slice(0, Count).ToArray();
            Target = new byte[QRCodeConstants.GetEccInfo(version, ecc).TotalDataCodewords];

            // The generator's set must be the planner's: as many symbols, each of the planned version.
            var set = QRCodeGenerator.CreateStructuredAppend(text, ecc);
            if (set.Length != Count || Array.Exists(set, symbol => symbol.Version != version))
                throw new InvalidOperationException($"the planner's split no longer reproduces the set of {text.Length} characters at {ecc}");
        }

        public int WritePayloads()
        {
            var bits = 0;
            for (int i = 0, from = 0; i < Count; from = ChunkEnds[i], i++)
            {
                var encoder = new QRBinaryEncoder(Target);
                encoder.WriteData(Text.AsSpan(from, ChunkEnds[i] - from), Mode, EciMode.Default, utf8Bom: false);
                bits += encoder.BitPosition;
            }
            return bits;
        }

        public int PayloadBits()
        {
            var bits = 0;
            for (int i = 0, from = 0; i < Count; from = ChunkEnds[i], i++)
                bits += PayloadBitsOf(Mode, ChunkEnds[i] - from);
            return bits;
        }
    }

    private static Shape[] SetStageShapes(string name, Func<string> text, QREccLevel ecc, int expectedSymbols)
    {
        var stages = new Lazy<SetStages>(() => new SetStages(text(), ecc, expectedSymbols));
        return
        [
            new($"stage/{name}/e2e", () => { var s = stages.Value; return Checked(() => QRCodeGenerator.CreateStructuredAppend(s.Text, s.Ecc).Length, s.Count); }),
            new($"stage/{name}/payload", () => { var s = stages.Value; return Checked(s.WritePayloads, s.PayloadBits()); }),
        ];
    }

    // ---- Micro QR ----

    private sealed class MicroStages
    {
        public readonly string Text;
        public readonly MicroQREccLevel Ecc;
        public readonly TextAnalysisResult Analysis;
        public readonly MicroQRVersion Version;
        public readonly int Size;
        public readonly byte[] Data = new byte[16];
        public readonly int DataCount;
        public readonly byte[] Ecc2 = new byte[14];
        public readonly int EccCount;
        public readonly int DataBits;
        public readonly byte[] Core;
        public readonly byte[] Destination = new byte[1 << 10];

        public MicroStages(string text, MicroQREccLevel ecc, MicroQRVersion expectedVersion)
        {
            Text = text;
            Ecc = ecc;
            Analysis = TextAnalyzer.Analyze(text, EciMode.Default, allowKanji: false);
            if (!MicroQRCodeGenerator.TrySelectVersion(in Analysis, ecc, out Version) || Version != expectedVersion)
                throw new InvalidOperationException($"{text} at {ecc} is not {expectedVersion}, the version the shape is named for");
            Size = MicroQRConstants.SizeFromVersion(Version);
            EccCount = MicroQRConstants.GetEccCodewordCount(Version, ecc);
            DataBits = MicroQRConstants.GetDataBitCapacity(Version, ecc);
            DataCount = WriteData();
            ReedSolomon();
            Core = new byte[Size * Size];
            Place();

            var written = MicroQRCodeGenerator.Create(text, ecc, Destination, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });
            if (written != Core.Length || !Destination.AsSpan(0, written).SequenceEqual(Core))
                throw new InvalidOperationException($"the stage composition no longer reproduces the encode of {Version}-{ecc}");
        }

        public int WriteData() => MicroQRBinaryEncoder.EncodeDataCodewords(Text, Version, Ecc, Analysis.EncodingMode, Data);

        public int ReedSolomon()
        {
            EccBinaryEncoder.CalculateECC(Data.AsSpan(0, DataCount), Ecc2, EccCount);
            return Ecc2[0];
        }

        public int Place()
        {
            Core.AsSpan().Clear();
            return MicroQRModulePlacer.PlaceSymbol(Core, Size, Data.AsSpan(0, DataCount), Ecc2.AsSpan(0, EccCount), DataBits, Version, Ecc);
        }
    }

    private static Shape[] MicroStageShapes(string name, string text, MicroQREccLevel ecc, MicroQRVersion version)
    {
        var stages = new Lazy<MicroStages>(() => new MicroStages(text, ecc, version));
        return
        [
            new($"stage/{name}/e2e", () => { var s = stages.Value; return Checked(() => MicroQRCodeGenerator.Create(s.Text, s.Ecc, s.Destination), FramedLength(s.Size, 2)); }),
            new($"stage/{name}/e2e-qz0", () => { var s = stages.Value; var options = new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 }; return Checked(() => MicroQRCodeGenerator.Create(s.Text, s.Ecc, s.Destination, options), s.Core.Length); }),
            new($"stage/{name}/e2e-class", () => { var s = stages.Value; return Checked(() => MicroQRCodeGenerator.Create(s.Text, s.Ecc).Size, s.Size + 4); }),
            new($"stage/{name}/analyze", () => { var s = stages.Value; return Checked(() => TextAnalyzer.Analyze(s.Text, EciMode.Default, allowKanji: false).DataLength, s.Analysis.DataLength); }),
            new($"stage/{name}/version", () =>
            {
                var s = stages.Value;
                return Checked(() => MicroQRCodeGenerator.TrySelectVersion(in s.Analysis, s.Ecc, out var version) ? (int)version : -1, (int)s.Version);
            }),
            new($"stage/{name}/data", () => { var s = stages.Value; return Checked(s.WriteData, s.DataCount); }),
            new($"stage/{name}/rs", () => stages.Value.ReedSolomon),
            new($"stage/{name}/place", () => stages.Value.Place),
            new($"stage/{name}/pack", () =>
            {
                var s = stages.Value;
                return () =>
                {
                    var data = new MicroQRCodeData(s.Version, 2);
                    data.SetCoreData(s.Core);
                    return data.Size;
                };
            }),
        ];
    }

    // ---- rMQR ----

    private sealed class RmQRStages
    {
        public readonly string Text;
        public readonly RmQRVersion Version;
        public readonly TextAnalysisResult Analysis;
        public readonly byte[] Data = new byte[152];
        public readonly int DataCount;
        public readonly byte[] FinalMessage;
        public readonly int Width;
        public readonly byte[] Core;
        public readonly byte[] Destination = new byte[1 << 13];
        public readonly RmQRCodeGeneratorOptions Options;

        public RmQRStages(string text, RmQRVersion version)
        {
            Text = text;
            Version = version;
            Options = new RmQRCodeGeneratorOptions { Version = version };
            Analysis = TextAnalyzer.Analyze(text, EciMode.Default, allowKanji: false);
            if (Analysis.EciMode != EciMode.Default)
                throw new InvalidOperationException("the rMQR stage shapes time the writer without an ECI header");
            DataCount = WriteData();
            FinalMessage = new byte[RmQRCodewordEncoder.GetFinalMessageSize(version)];
            ReedSolomon();
            Width = RmQRConstants.GetWidth(version);
            Core = new byte[Width * RmQRConstants.GetHeight(version)];
            Place();

            var written = RmQRCodeGenerator.Create(text, RmQREccLevel.M, Destination, Options with { QuietZoneSize = 0 });
            if (written != Core.Length || !Destination.AsSpan(0, written).SequenceEqual(Core))
                throw new InvalidOperationException($"the stage composition no longer reproduces the encode of {version}-M");
        }

        public int WriteData() => RmQRBinaryEncoder.EncodeDataCodewordsWithoutEci(Text, Version, RmQREccLevel.M, in Analysis, Data);

        public int ReedSolomon()
        {
            RmQRCodewordEncoder.AssembleFinalMessage(Data.AsSpan(0, DataCount), Version, RmQREccLevel.M, FinalMessage);
            return FinalMessage[0];
        }

        public int Place()
        {
            RmQRModulePlacer.PlaceSymbol(Core, Width, Version, RmQREccLevel.M, FinalMessage);
            return Core[^1];
        }
    }

    private static Shape[] RmQRStageShapes(string name, string text, RmQRVersion version)
    {
        var stages = new Lazy<RmQRStages>(() => new RmQRStages(text, version));
        return
        [
            new($"stage/{name}/e2e", () => { var s = stages.Value; return Checked(() => RmQRCodeGenerator.Create(s.Text, RmQREccLevel.M, s.Destination, s.Options), (s.Width + 4) * (s.Core.Length / s.Width + 4)); }),
            new($"stage/{name}/e2e-qz0", () => { var s = stages.Value; var options = s.Options with { QuietZoneSize = 0 }; return Checked(() => RmQRCodeGenerator.Create(s.Text, RmQREccLevel.M, s.Destination, options), s.Core.Length); }),
            new($"stage/{name}/e2e-class", () => { var s = stages.Value; return Checked(() => RmQRCodeGenerator.Create(s.Text, RmQREccLevel.M, s.Options).Width, s.Width + 4); }),
            new($"stage/{name}/analyze", () => { var s = stages.Value; return Checked(() => TextAnalyzer.Analyze(s.Text, EciMode.Default, allowKanji: false).DataLength, s.Analysis.DataLength); }),
            new($"stage/{name}/version", () =>
            {
                var s = stages.Value;
                return Checked(() => (int)RmQRVersionSelector.Select(s.Analysis.EncodingMode, s.Analysis.DataLength, RmQREccLevel.M, s.Version, RmQRFitStrategy.MinimizeArea, null), (int)s.Version);
            }),
            new($"stage/{name}/data", () => { var s = stages.Value; return Checked(s.WriteData, s.DataCount); }),
            new($"stage/{name}/rs", () => stages.Value.ReedSolomon),
            new($"stage/{name}/place", () => stages.Value.Place),
            new($"stage/{name}/pack", () =>
            {
                var s = stages.Value;
                return () =>
                {
                    var data = new RmQRCodeData(s.Version, 2);
                    data.SetCoreData(s.Core);
                    return data.Width;
                };
            }),
        ];
    }
}
