using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using FeatherQR.Internals;
using FeatherQR.Internals.ImageWriters;
using SkiaSharp;
using ZXing.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// PNG from the core: the chunks, the 1-bit pixels, the colour modes, and the deflate stream the writer compresses them with.
/// </summary>
/// <remarks>
/// Each image is read three ways that share no code with the writer: the chunks are walked with a CRC of the test's own,
/// the image data is inflated by the runtime and compared with scanlines the test builds, and SkiaSharp decodes the file.
/// </remarks>
public class SymbolImageWriterPngTest
{
    private static readonly SymbolColor Black = new(0, 0, 0);
    private static readonly SymbolColor White = new(255, 255, 255);

    // ---- every symbol decodes to its matrix ---------------------------------------------

    [Test]
    [MethodDataSource(typeof(SymbolImageWriterSvgTest), nameof(SymbolImageWriterSvgTest.StandardQrVersionsAndLevels))]
    public async Task StandardQr_DecodesToTheMatrix_EveryVersionAndLevel(int version, QREccLevel level)
    {
        var data = QRCodeGenerator.Create("1", level, new QRCodeGeneratorOptions { Version = version });
        await AssertPng(SymbolImageWriter.GetPngBytes(data), Grid.Of(data), 8, Black, White);
    }

    [Test]
    [MethodDataSource(typeof(SymbolImageWriterSvgTest), nameof(SymbolImageWriterSvgTest.MicroQrVersionsAndLevels))]
    public async Task MicroQr_DecodesToTheMatrix_EveryVersionAndLevel(MicroQRVersion version, MicroQREccLevel level)
    {
        var data = MicroQRCodeGenerator.Create("1", level, new MicroQRCodeGeneratorOptions { Version = version });
        await AssertPng(SymbolImageWriter.GetPngBytes(data), Grid.Of(data), 8, Black, White);
    }

    [Test]
    [MethodDataSource(typeof(SymbolImageWriterSvgTest), nameof(SymbolImageWriterSvgTest.RmQrVersionsAndLevels))]
    public async Task RmQr_DecodesToTheMatrix_EveryVersionAndLevel(RmQRVersion version, RmQREccLevel level)
    {
        var data = RmQRCodeGenerator.Create("1", level, new RmQRCodeGeneratorOptions { Version = version });
        await AssertPng(SymbolImageWriter.GetPngBytes(data), Grid.Of(data), 8, Black, White);
    }

    /// <summary>
    /// Sizes that fill a byte, split one, and straddle bytes, with quiet zones of 0, 1 and 4 so the symbol's edge lands inside a byte too.
    /// A scanline is packed eight modules at a time up to 8 pixels, a module at a time up to 32, and run by run past that, so 8 and 9,
    /// 32 and 33 are each side of a change. A quiet zone goes in 56 bits at a time on top of the bits still held, and at 15 and 31 pixels
    /// a quiet zone of 4 modules takes more than one step.
    /// </summary>
    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(5)]
    [Arguments(7)]
    [Arguments(8)]
    [Arguments(9)]
    [Arguments(13)]
    [Arguments(15)]
    [Arguments(31)]
    [Arguments(32)]
    [Arguments(33)]
    public async Task EveryModuleSize_DecodesToTheMatrix(int pixels)
    {
        var options = new SymbolImageOptions { ModulePixelSize = pixels };
        foreach (var quietZone in new[] { 0, 1, 4 })
        {
            var qr = QRCodeGenerator.Create("module size", QREccLevel.Q, new QRCodeGeneratorOptions { QuietZoneSize = quietZone });
            await AssertPng(SymbolImageWriter.GetPngBytes(qr, options), Grid.Of(qr), pixels, Black, White);
            var micro = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { QuietZoneSize = quietZone });
            await AssertPng(SymbolImageWriter.GetPngBytes(micro, options), Grid.Of(micro), pixels, Black, White);
            var rm = RmQRCodeGenerator.Create("module size", RmQREccLevel.M, new RmQRCodeGeneratorOptions { QuietZoneSize = quietZone });
            await AssertPng(SymbolImageWriter.GetPngBytes(rm, options), Grid.Of(rm), pixels, Black, White);
        }
    }

    // ---- colour modes -------------------------------------------------------------------

    [Test]
    public async Task BlackOnWhite_IsOneBitGreyscale_WithNoPalette()
    {
        var png = SymbolImageWriter.GetPngBytes(QRCodeGenerator.Create("grey", QREccLevel.M));
        var chunks = Chunks(png);

        await Assert.That(chunks.Select(c => c.Type)).IsEquivalentTo(new[] { "IHDR", "IDAT", "IEND" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(chunks[0].Data[8]).IsEqualTo((byte)1).Because("bit depth");
        await Assert.That(chunks[0].Data[9]).IsEqualTo((byte)0).Because("colour type greyscale");
    }

    [Test]
    public async Task OpaqueColors_AreOneBitIndexed_DarkFirst()
    {
        var navy = new SymbolColor(0x1E, 0x3A, 0x8A);
        var ivory = new SymbolColor(0xFF, 0xFF, 0xF0);
        var qr = QRCodeGenerator.Create("palette", QREccLevel.M);
        var png = SymbolImageWriter.GetPngBytes(qr, new SymbolImageOptions { DarkColor = navy, LightColor = ivory });
        var chunks = Chunks(png);

        await Assert.That(chunks.Select(c => c.Type)).IsEquivalentTo(new[] { "IHDR", "PLTE", "IDAT", "IEND" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(chunks[0].Data[9]).IsEqualTo((byte)3).Because("colour type indexed");
        await Assert.That(chunks[1].Data).IsEquivalentTo(new byte[] { 0x1E, 0x3A, 0x8A, 0xFF, 0xFF, 0xF0 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await AssertPng(png, Grid.Of(qr), 8, navy, ivory);
    }

    [Test]
    public async Task TransparentLight_CarriesBothAlphas()
    {
        var clear = new SymbolColor(255, 255, 255, 0);
        var qr = QRCodeGenerator.Create("transparent", QREccLevel.M);
        var png = SymbolImageWriter.GetPngBytes(qr, new SymbolImageOptions { LightColor = clear });
        var chunks = Chunks(png);

        await Assert.That(chunks.Select(c => c.Type)).IsEquivalentTo(new[] { "IHDR", "PLTE", "tRNS", "IDAT", "IEND" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(chunks[2].Data).IsEquivalentTo(new byte[] { 255, 0 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await AssertPng(png, Grid.Of(qr), 8, Black, clear);
    }

    /// <summary>
    /// The dark colour is painted over the light one, as the SVG paints its path over its background.
    /// Half-transparent black over opaque white is opaque grey 127, so the file needs no transparency.
    /// </summary>
    [Test]
    public async Task PartlyTransparentDark_IsPaintedOverTheLightColor()
    {
        var shade = new SymbolColor(0, 0, 0, 128);
        var micro = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L);
        var png = SymbolImageWriter.GetPngBytes(micro, new SymbolImageOptions { DarkColor = shade });
        var chunks = Chunks(png);

        await Assert.That(chunks.Select(c => c.Type)).IsEquivalentTo(new[] { "IHDR", "PLTE", "IDAT", "IEND" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(chunks[1].Data).IsEquivalentTo(new byte[] { 127, 127, 127, 255, 255, 255 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await AssertPng(png, Grid.Of(micro), 8, new SymbolColor(127, 127, 127), White);
    }

    /// <summary>A fully transparent dark colour paints nothing over the light one, so the image is the light colour alone, as the SVG is.</summary>
    [Test]
    public async Task TransparentDark_LeavesTheLightColorAlone()
    {
        var navy = new SymbolColor(0x1E, 0x3A, 0x8A);
        var qr = QRCodeGenerator.Create("knockout", QREccLevel.M);
        var png = SymbolImageWriter.GetPngBytes(qr, new SymbolImageOptions { DarkColor = new SymbolColor(0, 0, 0, 0), LightColor = navy });
        var chunks = Chunks(png);

        await Assert.That(chunks.Select(c => c.Type)).IsEquivalentTo(new[] { "IHDR", "PLTE", "IDAT", "IEND" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await AssertPng(png, Grid.Of(qr), 8, navy, navy);
    }

    /// <summary>Over a translucent light colour the painted dark colour keeps an alpha, so both entries carry one.</summary>
    [Test]
    public async Task TranslucentDarkOverTranslucentLight_CarriesBothAlphas()
    {
        var dark = new SymbolColor(0x1E, 0x3A, 0x8A, 200);
        var light = new SymbolColor(0xFF, 0xF8, 0xE7, 51);
        var painted = Over(dark, light);
        var qr = QRCodeGenerator.Create("translucent", QREccLevel.M);
        var png = SymbolImageWriter.GetPngBytes(qr, new SymbolImageOptions { DarkColor = dark, LightColor = light });
        var chunks = Chunks(png);

        await Assert.That(chunks[2].Type).IsEqualTo("tRNS");
        await Assert.That(chunks[2].Data).IsEquivalentTo(new byte[] { painted.A, 51 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await AssertPng(png, Grid.Of(qr), 8, painted, light);
    }

    /// <summary>
    /// A transparent dark colour over a translucent light one leaves the light colour, its alpha included.
    /// Over a transparent light colour nothing is painted at all, the one case where the blend has no weight to divide by.
    /// </summary>
    [Test]
    public async Task TransparentDark_OverATranslucentOrTransparentLight()
    {
        var clear = new SymbolColor(0, 0, 0, 0);
        var qr = QRCodeGenerator.Create("translucent", QREccLevel.M);

        var light = new SymbolColor(0xFF, 0xF8, 0xE7, 51);
        var chunks = Chunks(SymbolImageWriter.GetPngBytes(qr, new SymbolImageOptions { DarkColor = clear, LightColor = light }));
        await Assert.That(chunks[1].Data).IsEquivalentTo(new byte[] { 0xFF, 0xF8, 0xE7, 0xFF, 0xF8, 0xE7 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(chunks[2].Data).IsEquivalentTo(new byte[] { 51, 51 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);

        var none = new SymbolColor(255, 255, 255, 0);
        var png = SymbolImageWriter.GetPngBytes(qr, new SymbolImageOptions { DarkColor = clear, LightColor = none });
        await Assert.That(Chunks(png)[2].Data).IsEquivalentTo(new byte[] { 0, 0 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await AssertPng(png, Grid.Of(qr), 8, clear, none);
    }

    /// <summary>
    /// Straight-alpha source over, written here from its definition: alpha a = at + ab (1 - at), and each channel (ct at + cb ab (1 - at)) / a,
    /// each rounded half up. It is worked in integers scaled by 255², since floating point can land a value that is exactly halfway just below it.
    /// </summary>
    internal static SymbolColor Over(SymbolColor top, SymbolColor bottom)
    {
        long weightTop = top.A * 255L, weightBottom = bottom.A * (255L - top.A), alpha = weightTop + weightBottom;
        if (alpha == 0)
            return top;
        byte Channel(byte t, byte b) => (byte)((2 * (t * weightTop + b * weightBottom) + alpha) / (2 * alpha));
        return new SymbolColor(Channel(top.R, bottom.R), Channel(top.G, bottom.G), Channel(top.B, bottom.B), (byte)((2 * alpha + 255) / 510));
    }

    // ---- the deflate stream -------------------------------------------------------------

    /// <summary>
    /// A copy reaches at most 32,768 bytes back, so a scanline longer than that cannot be copied from the one above.
    /// A core 32,767 modules wide at 8 px makes a scanline of exactly 32,768 bytes with its filter byte, the last that can be copied.
    /// Past that a module row's scanline is searched once for all its repeats, which must give the symbols a search of each would, so the
    /// files are pinned as well.
    /// </summary>
    [Test]
    [Arguments(32_767, "5BE409DEBDE58F65BE920F63676C2D9D3D0467A7113940AC217E27EA2A1A8653")]
    [Arguments(32_768, "6DECF859A08DE621558E89A73158008A6E8AB1F6AD01111929C453AEF5DBFC69")]
    [Arguments(33_000, "A1B2CF6A60AF9623A08D1272868FB89AF8B5F208986C9E1C5B795733E45CA09D")]
    public async Task ScanlinesAroundTheCopyWindow_DecodeToTheMatrix(int coreWidth, string pinned)
    {
        var grid = Grid.Random(coreWidth, 3, seed: coreWidth);
        var png = PngWriter.GetBytes(new SyntheticView(grid), SymbolImageOptions.Default);
        await AssertPng(png, grid, 8, Black, White);
        await Assert.That(Sha256(png)).IsEqualTo(pinned);
    }

    /// <summary>
    /// At 1,900 pixels a module, an rMQR R7x139 with no quiet zone has scanlines of 33,014 bytes, past a copy's reach, each repeated 1,900
    /// times. Each pass searches a module row once and takes its symbols once for each repeat. The file is too large to read back here, so
    /// it is pinned.
    /// </summary>
    [Test]
    public async Task ManyRepeatsPastACopysReach_ArePinned()
    {
        var rm = RmQRCodeGenerator.Create("FeatherQR", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x139, QuietZoneSize = 0 });
        var png = SymbolImageWriter.GetPngBytes(rm, new SymbolImageOptions { ModulePixelSize = 1_900 });

        await Assert.That(Sha256(png)).IsEqualTo("4E96AFD05E1372F38836D8D6F07EEDC50352C31F921E6E99D6A203A4AD62B6EB");
    }

    /// <summary>
    /// The search marks where a run or a copy may start in 64-position words, and always searches the last seven positions in full.
    /// With no quiet zone, a scanline at 8 pixels is a byte a module and the filter byte, so these widths end it at each byte around the
    /// first and second word's ends. At 1 and 3 pixels the same widths give scanlines of 8 to 53 bytes, all inside the first word.
    /// Rows that repeat the row above and rows of long runs make copies and runs start near the end too.
    /// A position the marks miss changes which match is taken and the file still decodes, so the files are pinned as well.
    /// </summary>
    [Test]
    [Arguments(1, "D87FD26E21C47ABC1BE1046A43F0BA4E48850C11F0FF6917C26ADFB781207A5E")]
    [Arguments(3, "903D75520959B883BCE700866FC6BCD1293130607DEDE0C69909DBB1B56BA11F")]
    [Arguments(8, "642AE911C572D9E2B88C9119B1544CA41911A1D060704283BEEC44BB09D96FD5")]
    public async Task ScanlinesEndingAroundAWordOfStarts_DecodeToTheMatrix(int pixels, string pinned)
    {
        using var all = new MemoryStream();
        foreach (var width in Enumerable.Range(52, 22).Concat(Enumerable.Range(116, 22)))
        {
            var grid = Grid.Mixed(width, 12, seed: width);
            var png = PngWriter.GetBytes(new SyntheticView(grid), new SymbolImageOptions { ModulePixelSize = pixels });
            await AssertPng(png, grid, pixels, Black, White);
            all.Write(png);
        }
        await Assert.That(Sha256(all.ToArray())).IsEqualTo(pinned);
    }

    /// <summary>
    /// The writing pass replays the symbols the counting pass recorded, up to 2^20 entries, and matches the image again past that.
    /// Random modules at one pixel make almost every byte a literal, so 4,096 × 2,100 modules (1,077,300 bytes) pass the record.
    /// Both passes write the same bytes, so the encoder this thread keeps is read to tell which one ran, with a small image as the control.
    /// </summary>
    [Test]
    public async Task MoreSymbolsThanTheRecordHolds_AreMatchedAgain()
    {
        var small = Grid.Random(64, 64, seed: 20);
        PngWriter.GetBytes(new SyntheticView(small), new SymbolImageOptions { ModulePixelSize = 1 });
        var smallReplayed = LastRecordWasComplete();

        var grid = Grid.Random(4096, 2100, seed: 20);
        var png = PngWriter.GetBytes(new SyntheticView(grid), new SymbolImageOptions { ModulePixelSize = 1 });
        var replayed = LastRecordWasComplete();

        await Assert.That(smallReplayed).IsTrue();
        await Assert.That(replayed).IsFalse();
        await AssertPng(png, grid, 1, Black, White);
    }

    /// <summary>
    /// The lookup keeps the first scanline of each module row a copy can reach, in slots a later row takes over once the row is out of
    /// reach. Rows of 87,000 modules at 3 pixels, 3,000 at 10 and 2,000 at 16 reach only the row above, and the narrower rows at 3 and 5
    /// pixels reach 96 and 11 rows back, so every image here reuses its slots. The images 87,000 and 1,000 modules wide also take matches
    /// from less than a scanline short of 32,768 bytes back. A lookup that read a slot as the row it held before would not decode, and a
    /// copy from past 32,768 bytes would fail to write. A valid but different choice would decode, so the files are pinned as well.
    /// </summary>
    [Test]
    [Arguments(3, 87_000, 6, "5D4B1BB5FA2B34D4D7600332DBCB04D75E47EBE2A4EE66FECE204A15E4664614")]
    [Arguments(10, 3_000, 10, "7AD100B8AC163EA299866FD4E8694B72621B218533283ACC05DD44DC6DD0E2A3")]
    [Arguments(16, 2_000, 8, "2A655B7BB826B347D2967230757DADC4D42848BCD119C4E01389A9576ECE6357")]
    [Arguments(3, 300, 120, "8D341F3FEB5473E460C624BB4FE25110369300C8616A1034A1856337DCA1C210")]
    [Arguments(5, 1_000, 60, "31025D621AACF73AD102F5075C9E16753A005EEAD57CBB5B1598600914A83995")]
    public async Task RowsAcrossTheLookupWindow_DecodeToTheMatrix(int pixels, int width, int height, string pinned)
    {
        var grid = Grid.Mixed(width, height, seed: width + pixels);
        var png = PngWriter.GetBytes(new SyntheticView(grid), new SymbolImageOptions { ModulePixelSize = pixels });

        await AssertPng(png, grid, pixels, Black, White);
        await Assert.That(Sha256(png)).IsEqualTo(pinned);
    }

    /// <summary>
    /// The lookup's tables stay with the encoder this thread keeps. A file written after another must be the one a new encoder writes,
    /// so nothing of the image before may be found. The new encoder is the one a thread of its own takes.
    /// </summary>
    [Test]
    public async Task AFileWrittenAfterAnother_IsTheOneANewEncoderWrites()
    {
        var options = new SymbolImageOptions { ModulePixelSize = 10 };
        var before = QRCodeGenerator.Create(new string('7', 3000), QREccLevel.L, new QRCodeGeneratorOptions { Version = 40 });
        var after = QRCodeGenerator.Create("https://github.com/guitarrapc/FeatherQR", QREccLevel.M, new QRCodeGeneratorOptions { Version = 10 });
        byte[] fresh = [];
        var thread = new Thread(() => fresh = SymbolImageWriter.GetPngBytes(after, options));
        thread.Start();
        thread.Join();

        SymbolImageWriter.GetPngBytes(before, options);
        var written = SymbolImageWriter.GetPngBytes(after, options);

        await Assert.That(written).IsEquivalentTo(fresh, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    private static bool LastRecordWasComplete()
        => (bool)typeof(PngEncoder).GetField("_recordComplete", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(CachedEncoder())!;

    // Read before any await, since the encoder is kept per thread.
    private static object? CachedEncoder()
        => typeof(PngEncoder).GetField("cached", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);

    /// <summary>
    /// The bytes of files the decode tests above read back, pinned so that a change to the encoder's output is a decision.
    /// Version 40 at 8 and 3 pixels and an rMQR at 10 pixels in a palette with alpha also take the long matches and each way of packing
    /// a scanline up to 32 pixels. No test above writes that rMQR, so it is read back here. Change the hashes only with the encoder, after
    /// those tests pass.
    /// </summary>
    [Test]
    public async Task Output_IsPinned()
    {
        var qr = SymbolImageWriter.GetPngBytes(QRCodeGenerator.Create("1", QREccLevel.M, new QRCodeGeneratorOptions { Version = 1 }));
        var rm = SymbolImageWriter.GetPngBytes(RmQRCodeGenerator.Create("1", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43 }));
        var digits = string.Concat(Enumerable.Range(0, 7000).Select(i => (char)('0' + i * 7 % 10)));
        var large = QRCodeGenerator.Create(digits, QREccLevel.L, new QRCodeGeneratorOptions { Version = 40 });
        var wide = RmQRCodeGenerator.Create("FeatherQR", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 });
        var tinted = new SymbolImageOptions { ModulePixelSize = 10, DarkColor = new SymbolColor(0x1E, 0x3A, 0x8A, 200), LightColor = new SymbolColor(255, 255, 255, 51) };

        await Assert.That(Sha256(qr)).IsEqualTo("BCFE5FE5D20EFA5BA1261C9A975F899548E74269EDAA04986A485F884E0D5D2B");
        await Assert.That(Sha256(rm)).IsEqualTo("CF07EB71A69F9C864CCBF9DFAF7600E8D70FA3B7DD1074A69AF518F232E451A8");
        await Assert.That(Sha256(SymbolImageWriter.GetPngBytes(large))).IsEqualTo("4020F5A576A91B87DB3C011D4910F5BF756191F8A1B299767ABCD3CF06D976DA");
        await Assert.That(Sha256(SymbolImageWriter.GetPngBytes(large, new SymbolImageOptions { ModulePixelSize = 3 }))).IsEqualTo("E1B74019556A55D758D95456A92440D18D11AAFFC54A126F5A549D04DFE01F43");
        var tintedPng = SymbolImageWriter.GetPngBytes(wide, tinted);
        await AssertPng(tintedPng, Grid.Of(wide), 10, Over(tinted.DarkColor, tinted.LightColor), tinted.LightColor);
        await Assert.That(Sha256(tintedPng)).IsEqualTo("E81B85986E7BD403EE2C78BE1C1B01D9BD3C8C1566308F1A9618779B3F20D09C");
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    [Test]
    public async Task Version40_AtTheDefaultSize_IsCompressed()
    {
        var qr = QRCodeGenerator.Create(new string('A', 1800), QREccLevel.L, new QRCodeGeneratorOptions { Version = 40 });
        var png = SymbolImageWriter.GetPngBytes(qr);

        // Stored, the 1,480 scanlines of 186 bytes would take 275,280 bytes.
        await Assert.That(png.Length).IsLessThan(12_000);
    }

    /// <summary>
    /// At 1, 2, 4 and 8 pixels a literal byte is one module or a whole number of them, and runs and copies of the scanline above are all
    /// the search looks for. At any other size it also looks each position up among the module rows in reach, where the same few modules
    /// at the same bit phase nearly always are. Runs and copies alone stay over each bound, so a lookup that found nothing fails here.
    /// </summary>
    [Test]
    [Arguments(3, 7_000)]
    [Arguments(5, 8_500)]
    [Arguments(10, 11_000)]
    [Arguments(16, 12_800)]
    public async Task SizesWithTheLookup_FindModulesAcrossTheWindow(int pixels, int bound)
    {
        var digits = string.Concat(Enumerable.Range(0, 7000).Select(i => (char)('0' + i * 7 % 10)));
        var qr = QRCodeGenerator.Create(digits, QREccLevel.L, new QRCodeGeneratorOptions { Version = 40 });
        var png = SymbolImageWriter.GetPngBytes(qr, new SymbolImageOptions { ModulePixelSize = pixels });

        await AssertPng(png, Grid.Of(qr), pixels, Black, White);
        await Assert.That(png.Length).IsLessThan(bound);
    }

    // ---- readers ------------------------------------------------------------------------

    [Test]
    public async Task TheCoresDecoder_ReadsEachSymbology()
    {
        const string text = "https://github.com/guitarrapc/FeatherQR";
        await AssertCoreReads(SymbolImageWriter.GetPngBytes(QRCodeGenerator.Create(text, QREccLevel.M)), text);
        await AssertCoreReads(SymbolImageWriter.GetPngBytes(MicroQRCodeGenerator.Create("FeatherQR", MicroQREccLevel.L), new SymbolImageOptions { ModulePixelSize = 3 }), "FeatherQR");
        await AssertCoreReads(SymbolImageWriter.GetPngBytes(RmQRCodeGenerator.Create(text, RmQREccLevel.M), new SymbolImageOptions { ModulePixelSize = 5 }), text);
    }

    [Test]
    [Arguments(1)]
    [Arguments(3)]
    [Arguments(8)]
    public async Task ZXing_ReadsStandardQr(int pixels)
    {
        const string text = "https://github.com/guitarrapc/FeatherQR";
        var png = SymbolImageWriter.GetPngBytes(QRCodeGenerator.Create(text, QREccLevel.M), new SymbolImageOptions { ModulePixelSize = pixels, DarkColor = new SymbolColor(0x1E, 0x3A, 0x8A) });
        using var bitmap = SKBitmap.Decode(png);
        var reader = new BarcodeReader { Options = new ZXing.Common.DecodingOptions { TryHarder = true, PureBarcode = pixels == 1, PossibleFormats = new[] { ZXing.BarcodeFormat.QR_CODE } } };

        var result = reader.Decode(bitmap);
        await Assert.That(result?.Text).IsEqualTo(text);
    }

    // ---- the bytes route and arguments --------------------------------------------------

    [Test]
    public async Task WritePng_AppendsTheBytes_AndReturnsTheirLength()
    {
        var options = new SymbolImageOptions { ModulePixelSize = 3, LightColor = new SymbolColor(255, 255, 255, 0) };
        var qr = QRCodeGenerator.Create("bytes route", QREccLevel.Q);
        var micro = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L);
        var rm = RmQRCodeGenerator.Create("bytes route", RmQREccLevel.M);

        await AssertAppends(w => SymbolImageWriter.WritePng(qr, w, options), SymbolImageWriter.GetPngBytes(qr, options));
        await AssertAppends(w => SymbolImageWriter.WritePng(micro, w, options), SymbolImageWriter.GetPngBytes(micro, options));
        await AssertAppends(w => SymbolImageWriter.WritePng(rm, w, options), SymbolImageWriter.GetPngBytes(rm, options));
    }

    private static async Task AssertAppends(Func<IBufferWriter<byte>, int> write, byte[] expected)
    {
        var writer = new ArrayBufferWriter<byte>();
        writer.Write("prefix"u8);
        var count = write(writer);

        await Assert.That(count).IsEqualTo(expected.Length);
        await Assert.That(writer.WrittenSpan.ToArray()).IsEquivalentTo("prefix"u8.ToArray().Concat(expected).ToArray(), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    /// <summary>
    /// A writer must hand out at least the span asked for. One that hands out less is refused with <see cref="InvalidOperationException"/>,
    /// which tells it from an image too large, and nothing is advanced.
    /// </summary>
    [Test]
    public async Task AWriterWithTooShortASpan_IsRefused()
    {
        var writer = new ShortSpanWriter();
        await Assert.That(() => SymbolImageWriter.WritePng(QRCodeGenerator.Create("short", QREccLevel.M), writer)).Throws<InvalidOperationException>();
        await Assert.That(() => SymbolImageWriter.WritePng(MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L), writer)).Throws<InvalidOperationException>();
        await Assert.That(() => SymbolImageWriter.WritePng(RmQRCodeGenerator.Create("short", RmQREccLevel.M), writer)).Throws<InvalidOperationException>();
        await Assert.That(writer.Advanced).IsEqualTo(0);
    }

    /// <summary>
    /// The encoder is kept per thread, and a writer may write another image from inside <c>GetSpan</c>. The inner call must take an encoder
    /// of its own, since it hands its record back to the pool as it ends. The expected files are written first, which leaves this thread an
    /// encoder to take, so both calls would find that one if the inner call did not take another.
    /// </summary>
    [Test]
    public async Task AWriterThatWritesAnotherImage_GetsBothRight()
    {
        var outer = QRCodeGenerator.Create("outer", QREccLevel.M);
        var inner = QRCodeGenerator.Create(new string('7', 600), QREccLevel.L);
        var expectedOuter = SymbolImageWriter.GetPngBytes(outer);
        var expectedInner = SymbolImageWriter.GetPngBytes(inner);
        byte[] innerPng = [];
        var writer = new CallbackWriter(() => innerPng = SymbolImageWriter.GetPngBytes(inner));

        var length = SymbolImageWriter.WritePng(outer, writer);

        await Assert.That(writer.WrittenSpan.ToArray()).IsEquivalentTo(expectedOuter, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(length).IsEqualTo(writer.WrittenCount);
        await Assert.That(innerPng).IsEquivalentTo(expectedInner, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    /// <summary>
    /// A writer that throws from <c>GetSpan</c> ends the call, and the encoder goes back to the thread and writes the next file as before.
    /// Nothing is awaited until all three calls are done, so they run on this thread and find its one encoder.
    /// </summary>
    [Test]
    public async Task AWriterThatThrows_LeavesTheNextFileRight()
    {
        var qr = QRCodeGenerator.Create("throws", QREccLevel.M);
        var expected = SymbolImageWriter.GetPngBytes(qr);
        var kept = CachedEncoder();
        Exception? thrown = null;
        try
        {
            SymbolImageWriter.WritePng(qr, new CallbackWriter(() => throw new IOException("full")));
        }
        catch (IOException e)
        {
            thrown = e;
        }
        var returned = CachedEncoder();
        var next = SymbolImageWriter.GetPngBytes(qr);

        await Assert.That(thrown).IsNotNull();
        await Assert.That(kept).IsNotNull();
        await Assert.That(returned).IsSameReferenceAs(kept);
        await Assert.That(next).IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    /// <summary>An array writer that runs a callback the first time it is asked for a span.</summary>
    private sealed class CallbackWriter(Action callback) : IBufferWriter<byte>
    {
        private readonly ArrayBufferWriter<byte> _inner = new();
        private bool _called;

        public ReadOnlySpan<byte> WrittenSpan => _inner.WrittenSpan;
        public int WrittenCount => _inner.WrittenCount;
        public void Advance(int count) => _inner.Advance(count);
        public Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            if (!_called)
            {
                _called = true;
                callback();
            }
            return _inner.GetSpan(sizeHint);
        }
    }

    [Test]
    public async Task NullArguments_Throw()
    {
        var writer = new ArrayBufferWriter<byte>();
        await Assert.That(() => SymbolImageWriter.GetPngBytes((QRCodeData)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.GetPngBytes((MicroQRCodeData)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.GetPngBytes((RmQRCodeData)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.WritePng((QRCodeData)null!, writer)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.WritePng((MicroQRCodeData)null!, writer)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.WritePng((RmQRCodeData)null!, writer)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.WritePng(QRCodeGenerator.Create("1", QREccLevel.L), null!)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.WritePng(MicroQRCodeGenerator.Create("1", MicroQREccLevel.ErrorDetectionOnly), null!)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.WritePng(RmQRCodeGenerator.Create("1", RmQREccLevel.M), null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task PixelSide_PastAnInt_Throws()
    {
        var qr = QRCodeGenerator.Create("1", QREccLevel.L, new QRCodeGeneratorOptions { Version = 40, QuietZoneSize = 10_000 });
        var options = new SymbolImageOptions { ModulePixelSize = int.MaxValue / qr.Size + 1 };

        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.GetPngBytes(qr, options));
        await Assert.That(refused.ParamName).IsEqualTo("options");
        var written = Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.WritePng(qr, new ArrayBufferWriter<byte>(), options));
        await Assert.That(written.ParamName).IsEqualTo("options");
    }

    /// <summary>
    /// A match costs at least two bits for at most 258 bytes. Version 1 at 150,000 pixels a module has 2.37e12 bytes of scanlines, longer
    /// than an array even at that rate, so it is refused before its symbols are counted. An rMQR R7x139 with no quiet zone at 134,000 pixels
    /// has 2.18e12 bytes, which passes that bound, and is refused only once its count gives its length. Its scanlines are past a copy's
    /// reach, so the count takes seconds only if each module row is searched once for all its repeats. <c>Measure</c> is called for it so
    /// that a wrong count could not make the test write a file of 2 GB.
    /// </summary>
    [Test]
    public async Task FilesLongerThanAnArray_AreRefusedWithinSeconds()
    {
        var qr = QRCodeGenerator.Create("1", QREccLevel.L, new QRCodeGeneratorOptions { Version = 1 });
        var options = new SymbolImageOptions { ModulePixelSize = 150_000 };
        var rm = RmQRCodeGenerator.Create("1", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x139, QuietZoneSize = 0 });
        var counted = new SymbolImageOptions { ModulePixelSize = 134_000 };

        var clock = Stopwatch.StartNew();
        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.GetPngBytes(qr, options));
        var written = Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.WritePng(qr, new ArrayBufferWriter<byte>(), options));
        var encoder = PngEncoder.Take();
        ArgumentOutOfRangeException measured;
        try
        {
            measured = Assert.Throws<ArgumentOutOfRangeException>(() => encoder.Measure(new RmQRMatrixView(rm), counted));
        }
        finally
        {
            PngEncoder.Return(encoder);
        }
        clock.Stop();

        await Assert.That(refused.ParamName).IsEqualTo("options");
        await Assert.That(written.ParamName).IsEqualTo("options");
        await Assert.That(measured.ParamName).IsEqualTo("options");
        await Assert.That(clock.Elapsed).IsLessThan(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// A side within 7 pixels of <see cref="int.MaxValue"/> passes the side check, and rounding it up to whole bytes adds 7.
    /// Version 2 with the default quiet zone is 33 modules, and 33 × 65,075,262 is 2,147,483,646.
    /// </summary>
    [Test]
    public async Task PixelSide_JustUnderAnInt_IsRefusedOnTheOptions()
    {
        var qr = QRCodeGenerator.Create("1", QREccLevel.L, new QRCodeGeneratorOptions { Version = 2 });
        await Assert.That(qr.Size).IsEqualTo(33);
        var options = new SymbolImageOptions { ModulePixelSize = 65_075_262 };

        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.GetPngBytes(qr, options));
        await Assert.That(refused.ParamName).IsEqualTo("options");
        var written = Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.WritePng(qr, new ArrayBufferWriter<byte>(), options));
        await Assert.That(written.ParamName).IsEqualTo("options");
    }

    // ---- the reader ---------------------------------------------------------------------

    private sealed record Chunk(string Type, byte[] Data);

    /// <summary>Walks the chunks after the signature and checks each CRC with a bitwise CRC-32 of the test's own.</summary>
    private static List<Chunk> Chunks(byte[] png)
    {
        if (!png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            throw new FormatException("no PNG signature");
        var chunks = new List<Chunk>();
        var pos = 8;
        while (pos < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            var typeAndData = png.AsSpan(pos + 4, length + 4);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 8 + length));
            if (crc != BitwiseCrc32(typeAndData))
                throw new FormatException($"bad CRC on {Encoding.ASCII.GetString(typeAndData[..4])}");
            chunks.Add(new Chunk(Encoding.ASCII.GetString(typeAndData[..4]), typeAndData[4..].ToArray()));
            pos += 12 + length;
        }
        if (pos != png.Length || chunks[^1].Type != "IEND")
            throw new FormatException("the file does not end with IEND");
        return chunks;
    }

    private static uint BitwiseCrc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    private static async Task AssertPng(byte[] png, Grid grid, int pixels, SymbolColor dark, SymbolColor light)
    {
        var chunks = Chunks(png);
        var header = chunks[0];
        var width = grid.Width * pixels;
        var height = grid.Height * pixels;
        await Assert.That(header.Type).IsEqualTo("IHDR");
        await Assert.That(BinaryPrimitives.ReadInt32BigEndian(header.Data)).IsEqualTo(width);
        await Assert.That(BinaryPrimitives.ReadInt32BigEndian(header.Data.AsSpan(4))).IsEqualTo(height);
        await Assert.That(header.Data.AsSpan(8, 5).ToArray()).IsEquivalentTo(new byte[] { 1, header.Data[9], 0, 0, 0 }, TUnit.Assertions.Enums.CollectionOrdering.Matching)
            .Because("bit depth 1, deflate, filter method 0, no interlace");

        // The zlib header: deflate with a window of up to 32 KB, a check that divides by 31, no preset dictionary.
        var stream = chunks.Where(c => c.Type == "IDAT").SelectMany(c => c.Data).ToArray();
        await Assert.That(stream[0] & 0x0F).IsEqualTo(8);
        await Assert.That((stream[0] << 8 | stream[1]) % 31).IsEqualTo(0);
        await Assert.That(stream[1] & 0x20).IsEqualTo(0);

        // The runtime's inflater also checks the Adler-32 at the end.
        using var inflated = new MemoryStream();
        using (var z = new ZLibStream(new MemoryStream(stream), CompressionMode.Decompress))
            z.CopyTo(inflated);
        var expected = grid.Scanlines(pixels);
        await Assert.That(inflated.Length).IsEqualTo(expected.LongLength);
        await Assert.That(inflated.ToArray().AsSpan().SequenceEqual(expected)).IsTrue().Because("the inflated scanlines are the module rows, filter 0, light padding");

        // SkiaSharp reads the whole file. One pixel a module is compared, at its centre.
        using var bitmap = SKBitmap.Decode(png, new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        await Assert.That(bitmap).IsNotNull();
        var rgba = bitmap!.GetPixelSpan();
        var wrong = 0;
        for (var row = 0; row < grid.Height; row++)
        {
            for (var col = 0; col < grid.Width; col++)
            {
                var color = grid[row, col] ? dark : light;
                var at = ((row * pixels + pixels / 2) * bitmap.RowBytes) + (col * pixels + pixels / 2) * 4;
                if (rgba[at] != color.R || rgba[at + 1] != color.G || rgba[at + 2] != color.B || rgba[at + 3] != color.A)
                    wrong++;
            }
        }
        await Assert.That(wrong).IsEqualTo(0);
    }

    private static async Task AssertCoreReads(byte[] png, string text)
    {
        var bounds = SKBitmap.DecodeBounds(png);
        using var bitmap = SKBitmap.Decode(png, new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        var luminance = new byte[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
            bitmap.GetPixelSpan().Slice(y * bitmap.RowBytes, bitmap.Width).CopyTo(luminance.AsSpan(y * bitmap.Width));
        var results = SymbolDecoder.DecodeImageSymbols(luminance, bitmap.Width, bitmap.Height);
        await Assert.That(results.Where(r => r.Success).Select(r => r.Text)).IsEquivalentTo(new[] { text }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    /// <summary>A module grid with the quiet zone, read through the public indexers or made at random.</summary>
    internal sealed class Grid(int width, int height, bool[] dark)
    {
        public int Width => width;
        public int Height => height;
        public bool this[int row, int col] => dark[row * width + col];

        public static Grid Of(QRCodeData d) => From(d.Size, d.Size, (r, c) => d[r, c]);
        public static Grid Of(MicroQRCodeData d) => From(d.Size, d.Size, (r, c) => d[r, c]);
        public static Grid Of(RmQRCodeData d) => From(d.Width, d.Height, (r, c) => d[r, c]);

        /// <summary>Rows that repeat the row above, rows of runs from one to a dozen modules, and random rows, a third each.</summary>
        public static Grid Mixed(int width, int height, int seed)
        {
            var rng = new Random(seed);
            var cells = new bool[width * height];
            for (var r = 0; r < height; r++)
            {
                var kind = r == 0 ? 2 : rng.Next(3);
                var run = 1 + rng.Next(12);
                for (var c = 0; c < width; c++)
                {
                    cells[r * width + c] = kind switch
                    {
                        0 => cells[(r - 1) * width + c],
                        1 => c / run % 2 == 0,
                        _ => rng.Next(2) == 1,
                    };
                }
            }
            return new Grid(width, height, cells);
        }

        public static Grid Random(int width, int height, int seed)
        {
            var rng = new Random(seed);
            var cells = new bool[width * height];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = rng.Next(2) == 1;
            return new Grid(width, height, cells);
        }

        private static Grid From(int width, int height, Func<int, int, bool> dark)
        {
            var cells = new bool[width * height];
            for (var r = 0; r < height; r++)
            {
                for (var c = 0; c < width; c++)
                    cells[r * width + c] = dark(r, c);
            }
            return new Grid(width, height, cells);
        }

        /// <summary>The packed core bits, row-major and MSB first, as the data types hold them, for a grid with no quiet zone.</summary>
        public byte[] PackedBits()
        {
            var bits = new byte[(width * height + 7) / 8];
            for (var i = 0; i < width * height; i++)
            {
                if (dark[i])
                    bits[i >> 3] |= (byte)(0x80 >> (i & 7));
            }
            return bits;
        }

        /// <summary>Filter byte 0, then the pixels a bit each, light 1 and dark 0, MSB first, the last byte padded with light.</summary>
        public byte[] Scanlines(int pixels)
        {
            var rowBytes = (width * pixels + 7) / 8;
            var stride = rowBytes + 1;
            var image = new byte[(long)stride * height * pixels];
            for (var y = 0; y < height * pixels; y++)
            {
                var line = image.AsSpan(y * stride, stride);
                line[1..].Fill(0xFF);
                for (var x = 0; x < width * pixels; x++)
                {
                    if (dark[(y / pixels) * width + x / pixels])
                        line[1 + (x >> 3)] &= (byte)~(0x80 >> (x & 7));
                }
            }
            return image;
        }
    }

    /// <summary>A matrix with no quiet zone over a grid, for sizes no symbol has.</summary>
    private readonly struct SyntheticView(Grid grid) : IModuleMatrixView
    {
        private readonly byte[] _bits = grid.PackedBits();
        public int Width => grid.Width;
        public int Height => grid.Height;
        public int CoreWidth => grid.Width;
        public int CoreHeight => grid.Height;
        public ReadOnlySpan<byte> CoreBits => _bits;
        public bool GetCoreModule(int coreRow, int coreCol) => grid[coreRow, coreCol];
        public bool IsFinderPattern(int coreRow, int coreCol) => false;
    }
}
