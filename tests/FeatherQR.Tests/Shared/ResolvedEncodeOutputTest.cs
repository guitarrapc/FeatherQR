using FeatherQR.Internals;
using FeatherQR.Internals.MicroQR;
using System.Security.Cryptography;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The encodes whose version or level is settled before the pipeline runs, frozen as values: a narrowed
/// <see cref="QRCodeGeneratorOptions.Version"/>, <see cref="QRCodeGeneratorOptions.BoostEccLevel"/>, and the
/// <see cref="QRSegmentation.Optimal"/> encodes that end up writing the single-mode stream (no split pays, a
/// byte order mark rules one out, or the builder refuses the plan the scan chose), for Standard QR and Micro QR.
/// </summary>
/// <remarks>
/// <para>
/// These paths analyze the text to resolve the version and then hand the pipeline that resolution, so the
/// analysis they write from has to be the one the pipeline would have made itself.
/// The details that make two analyses differ are each a class here: a byte order mark overriding
/// <see cref="QRCodeGeneratorOptions.AllowKanji"/>, the mixed-mode analysis that marks a text
/// <c>KanjiPlannable</c> where the single-mode one does not, and the three bytes the mark adds to a UTF-8 Byte
/// count only.
/// </para>
/// <para>
/// The hashes, versions, levels and messages were captured from the encoder on 2026-10-04, before the
/// redundant analysis was removed. A difference is an output change, not a refactoring.
/// The default path (no range, no boost, single mode) is pinned alongside, since the same pipeline serves it.
/// </para>
/// </remarks>
public class ResolvedEncodeOutputTest
{
    private const string Ascii = "HELLO WORLD 123";
    private const string Digits = "012345678901234567890123456789";
    private const string Url = "https://example.com/user/repo?foo=value&bar=piyo";
    private const string Latin1 = "Café déjà vu";
    private const string KanjiText = "日本語のテキスト";      // every character has a JIS X 0208 cell
    private const string MixedKanji = "a日本";               // Kanji-eligible with ASCII: KanjiPlannable under a plan, UTF-8 alone
    private const string Unicode = "FooBar你好🎉";           // the emoji has no cell, so UTF-8 under any option
    private const string MicroMixedKanji = "a日";            // MixedKanji's class at a Micro QR length
    private const string Latin1Lookalike = "Ã©123456789012é"; // its Latin-1 run reads as UTF-8, so the Micro QR builder refuses the plan the scan chose

    private static readonly string Prose = Repeat("The quick brown fox jumps over the lazy dog. ", 600);
    private static readonly string Oversized = Repeat("The quick brown fox jumps over the lazy dog. ", 3_000);
    private static readonly string OversizedUnicode = Repeat(UnicodeRun, 3_000);   // 4,500 UTF-8 bytes
    private const string UnicodeRun = "FooBar你好";            // repeats without splitting a surrogate pair

    // ---- Standard QR -----------------------------------------------------------------

    public readonly record struct StandardCase(
        string Name, string Text, QREccLevel Ecc, QRCodeGeneratorOptions Options,
        int Version, QREccLevel Level, string RawSha, string BufferSha)
    {
        public override string ToString() => Name;
    }

    private static readonly QRCodeGeneratorOptions Optimal = new() { Segmentation = QRSegmentation.Optimal };

    public static IEnumerable<StandardCase> StandardCases()
    {
        // Single mode, version or level resolved before the pipeline
        yield return new("exact-alphanumeric", Ascii, QREccLevel.M, new() { Version = QRVersionRange.Exactly(5) }, 5, QREccLevel.M, "C97374703B53BACD7878E7093E40F1595BE2979D6495D9757493D9F879B45877", "5446C33980A179D82BBEA36919F06F938ADC28669BC2861D2C3B432B34F96DF1");
        yield return new("at-least-numeric", Digits, QREccLevel.L, new() { Version = QRVersionRange.AtLeast(3) }, 3, QREccLevel.L, "0796C2AD146232E53D63D36B1107DF8CBDA5C504A296ED2BE5A26942063B2244", "9361E26DA1CC0928EE4AB6CEC4EBAD25FD479126309D4B552A9046B1739CBF3C");
        yield return new("at-most-byte", Url, QREccLevel.M, new() { Version = QRVersionRange.AtMost(10) }, 4, QREccLevel.M, "F36A5931BBD9AE4A648FF30D6C3D9C3CFAB44CF7B00EE39FBD21F0CB37193BEB", "5306546D0EF7EA8DBDBAB1101671B876792ABB300EF5449B6EF06DD8C5D679A7");
        yield return new("boost-to-h", "HELLO", QREccLevel.L, new() { BoostEccLevel = true }, 1, QREccLevel.H, "8768A6A2BCF9BA03B1F60D0FF0772303D2420493432B02414C68880B96E2479C", "7CAC666A6FCFA9FAAA035BD6B8E9CEC011292906D0585D03011062DC240B0F57");
        yield return new("boost-partial", new string('A', 33), QREccLevel.L, new() { BoostEccLevel = true }, 2, QREccLevel.M, "A4C760E79C0916C43E99326E988121BB07506D9F5142B868A6CCBC23CB8110D7", "591D57C2623C33C3A3F118DF8308F088FC529D81B04A6177A6479B6994DBDD0C");
        yield return new("boost-exact", Ascii, QREccLevel.L, new() { BoostEccLevel = true, Version = QRVersionRange.Exactly(4) }, 4, QREccLevel.H, "1F0A7FF8003193B7F1504214009E20C38A1AA28AC6F1F070A5E23302B03815A0", "CDAF9DEECB4A3C11DF1EB339F40EEA0177922285F155BF55ADC955BBD168FE12");
        yield return new("boost-range-long", Prose, QREccLevel.L, new() { BoostEccLevel = true, Version = QRVersionRange.AtLeast(20) }, 20, QREccLevel.M, "B9D8060AD666344045B8323E07EFE5B726DB2E5B68DDEA4AE601D8A8502EB586", "14525E762B3414B300819F6F18E94D84BF92020081874D4CE64947D0B10587E5");
        yield return new("kanji-exact", KanjiText, QREccLevel.M, new() { AllowKanji = true, Version = QRVersionRange.Exactly(3) }, 3, QREccLevel.M, "7FB47D0401F2AE987222AA0F4725CCF561FC14DBDCE239ACBB11CFEDAD359417", "83A176E39EE21A58060094EB7B67331AA686C3CCAFFBCE09E5497B56393346A6");
        yield return new("kanji-bom-exact", KanjiText, QREccLevel.M, new() { AllowKanji = true, Utf8Bom = true, Version = QRVersionRange.Exactly(4) }, 4, QREccLevel.M, "94F8A1DD72F595DC553E37EA73F053C823836FFE4A30A087BCEDECE585C2C534", "A8985B5797D438E77B973AA054D04B0ECDE2C59EF945BA7BD205F59D4A014EFE");
        yield return new("kanji-bom-boost", KanjiText, QREccLevel.L, new() { AllowKanji = true, Utf8Bom = true, BoostEccLevel = true }, 2, QREccLevel.L, "16264B811F2E184077EED87A598E34B1850E606352974B628853033BC5372B39", "D3831027CE1FF4AB6C193FC35BB63D133BA24550CDFD8C5C2B2D02F087790F4F");
        yield return new("mixed-kanji-exact", MixedKanji, QREccLevel.M, new() { AllowKanji = true, Version = QRVersionRange.Exactly(2) }, 2, QREccLevel.M, "8AF246092853268E8CF5255B60AD5FA2C9ABDB51DC6B10512D41654CB20F8630", "ABD7B6BB15396F7F75072D53DECFE2C2CA7314E13DE2981BD9FCBBD50148F8FB");
        yield return new("bom-utf8-boost", Unicode, QREccLevel.L, new() { Utf8Bom = true, EciMode = EciMode.Utf8, BoostEccLevel = true }, 2, QREccLevel.Q, "F7DB6F24C4A7FA088C9284E64B55D8DB6160AD7728393745E1D3F8252CCC4388", "AC77BA0CE97BDEA4354FBCF173C905015C944717DF2686380F082D076E0E3801");
        yield return new("bom-numeric-exact", Digits, QREccLevel.M, new() { Utf8Bom = true, Version = QRVersionRange.Exactly(2) }, 2, QREccLevel.M, "BC988B991C31FC305A6B47D3AEE8E39D442AC08358C4B83C2A7504ABF93BEBB7", "413AF2CE2A720F4D75A3A3D9E5ECC2C6E39606111E8945DBF4A1D2F907139C5C");
        yield return new("iso-exact", Latin1, QREccLevel.Q, new() { EciMode = EciMode.Iso8859_1, Version = QRVersionRange.Exactly(3) }, 3, QREccLevel.Q, "ADA7D01106D8B895AA69C75C01FC8AA9AC2E9B6AD33DE4C439218E9F5D6657FD", "8DFEB5295F4682B581FF37268EF97096FC299B29857C2816CD85C3C156CA61A9");
        yield return new("latin1-range-boost", Latin1, QREccLevel.L, new() { BoostEccLevel = true, Version = QRVersionRange.AtLeast(2) }, 2, QREccLevel.H, "7D5378DB8B0A150CD63D9B66A29A3805E2931644AA8F562BEDBA4F38E68DAE04", "B3BA0D9FDEBB637381D7EF96CB5EF58424E56435A4212CF12F30823EC9B631FA");
        yield return new("empty-exact", "", QREccLevel.M, new() { Version = QRVersionRange.Exactly(2) }, 2, QREccLevel.M, "34924C31F975B1611EBEF9A737D35E8622A89B545E50C2F9544EBB5282FFD63C", "BFFDC43002DE2FD8A4C4C6CB61E117A672ED1C44CF39D86A678A07120E75005B");
        yield return new("mask-exact", Ascii, QREccLevel.M, new() { MaskPattern = 5, Version = QRVersionRange.Exactly(7) }, 7, QREccLevel.M, "5960525354A76A23EAE355735BF4C395C78BF8E9A341A0DD5EF1D38654B0B121", "08F75F6CBF87561146C953149939E34C717A591497859C116AB8FEB554B0D603");
        yield return new("quiet-zone-0-exact", Ascii, QREccLevel.Q, new() { QuietZoneSize = 0, Version = QRVersionRange.Exactly(1) }, 1, QREccLevel.Q, "4805298E52D3BC3001400129FE397B43FBF61411289699CBB2E29B1FD0D4A640", "CD3419AC942B664DD978C1083D8DF9844FFA8135443AC8D5280344C4BF08F12C");

        // Single mode, the default path the same pipeline serves
        yield return new("default-ascii", Ascii, QREccLevel.M, new(), 1, QREccLevel.M, "DF3513E09689BEFE49C38DF1BAA0707331B7A9CA92051E0881577D45399EB0C2", "4F9FD23E7FCA32AFFE0501DBF0F53501121AD58C155FBBE36635474C94FE6F01");
        yield return new("default-kanji", KanjiText, QREccLevel.M, new() { AllowKanji = true }, 1, QREccLevel.M, "91961CC911DCABEA184F2AF2D8CAF98B57D263BE39CC4B1E55FB9474C0D1155D", "623286B09EE5EFBD2F8B6EF89256C300A5A65DCB1737E80641A77DA4D7523C14");
        yield return new("default-kanji-bom", KanjiText, QREccLevel.M, new() { AllowKanji = true, Utf8Bom = true }, 3, QREccLevel.M, "4E440E9C311D57CA50BC684B8194C0F16E5A40B8434782E343B5A4C860F0F7FE", "5B62E6E9D40A7E5FAE77F316DAA5DF49503F43C2FCC00A74C0C56C4126FBB04F");
        yield return new("default-unicode-quiet-zone-0", Unicode, QREccLevel.L, new() { QuietZoneSize = 0 }, 1, QREccLevel.L, "0A6B962A5C8FE35CB080B9B9E7B5B37CCD38568936001162AA5D5312D4B5B30D", "5E70E89BC2A5BC063A70F5EF7D75D135A5F625D384A0C532C4CEDD8D6661000D");

        // Optimal, writing the single-mode stream
        yield return new("optimal-numeric", Digits, QREccLevel.M, Optimal, 1, QREccLevel.M, "06A1996CB4EDF1A6AB601AECADC4A5E4E23046F8DD5BB579EAE1F7F4AA594022", "05FEFB15C965C61813DB057F7A96ACD6E35B5C9C9EB64E2A7007ECAC39C26C88");
        yield return new("optimal-numeric-exact-boost", Digits, QREccLevel.L, Optimal with { Version = QRVersionRange.Exactly(2), BoostEccLevel = true }, 2, QREccLevel.H, "7207A93BEB4507C205C3E006398C525D2F13419DCE200387BB53538B834830E8", "57B3D6A365F52A22E31E8A19EEC72CEB0A34C9D61BC82699040F98B006840106");
        yield return new("optimal-mixed-kanji", MixedKanji, QREccLevel.M, Optimal with { AllowKanji = true }, 1, QREccLevel.M, "E57F77EB26BFEF52D90009CF1B5F65AA1F8EBA81E5FC6A057BF04C71538F0240", "BA662B9EF148E818A45CBF57EB7238B4516F215CDB54F656D07F8AC10F31EC00");
        yield return new("optimal-kanji", KanjiText, QREccLevel.M, Optimal with { AllowKanji = true }, 1, QREccLevel.M, "91961CC911DCABEA184F2AF2D8CAF98B57D263BE39CC4B1E55FB9474C0D1155D", "623286B09EE5EFBD2F8B6EF89256C300A5A65DCB1737E80641A77DA4D7523C14");
        yield return new("optimal-bom-utf8", Unicode, QREccLevel.M, Optimal with { Utf8Bom = true, EciMode = EciMode.Utf8 }, 2, QREccLevel.M, "B71D9AA527B58AE9F7FD2D161EF3B669E7082A01B8FE2A9E2938658B1A38059D", "CEDA72FCB27AE1B2142B0D70A8F5E929E0C720146FB740787981927861B6AF0F");
        yield return new("optimal-bom-default-eci", Unicode, QREccLevel.M, Optimal with { Utf8Bom = true }, 2, QREccLevel.M, "B71D9AA527B58AE9F7FD2D161EF3B669E7082A01B8FE2A9E2938658B1A38059D", "CEDA72FCB27AE1B2142B0D70A8F5E929E0C720146FB740787981927861B6AF0F");
        yield return new("optimal-bom-exact-boost", Unicode, QREccLevel.L, Optimal with { Utf8Bom = true, Version = QRVersionRange.Exactly(4), BoostEccLevel = true }, 4, QREccLevel.H, "8FAAAB55F5012680BCB5B2504C388D6B015269A089B06C434DD23AF4E576E9C8", "EEE94FCB9E5E30968680A9C8FFA00E0B10553541AD703418D1DA75FC35364A8F");
        yield return new("optimal-bom-kanji", KanjiText, QREccLevel.M, Optimal with { AllowKanji = true, Utf8Bom = true }, 3, QREccLevel.M, "4E440E9C311D57CA50BC684B8194C0F16E5A40B8434782E343B5A4C860F0F7FE", "5B62E6E9D40A7E5FAE77F316DAA5DF49503F43C2FCC00A74C0C56C4126FBB04F");
        yield return new("optimal-bom-numeric", Digits, QREccLevel.M, Optimal with { Utf8Bom = true }, 1, QREccLevel.M, "06A1996CB4EDF1A6AB601AECADC4A5E4E23046F8DD5BB579EAE1F7F4AA594022", "05FEFB15C965C61813DB057F7A96ACD6E35B5C9C9EB64E2A7007ECAC39C26C88");
        // A pinned mask on each Optimal single-stream site: no split pays, and the byte order mark branch
        yield return new("optimal-numeric-mask", Digits, QREccLevel.M, Optimal with { MaskPattern = 3 }, 1, QREccLevel.M, "7F7263D211AD7C8E3C63DF8FC442DBF1943838537E105CB17D6A0F6A2D07819D", "B557DDE1A054D32B109EF0541594D852B540F974B6B866635C1B682428FD86B6");
        yield return new("optimal-bom-mask", Unicode, QREccLevel.M, Optimal with { Utf8Bom = true, MaskPattern = 6 }, 2, QREccLevel.M, "DAEAA9B5F8A2E7B6119E8F7833061E5855FF2871429A1CA5D3542FAE1A8C58D0", "58B1ACE13B22212028178F5A9C56F1DEDFD91383DCAFFCBA46308E1EA967E92A");
    }

    public static IEnumerable<StandardCase> StandardOptimalCases() => StandardCases().Where(c => c.Options.Segmentation == QRSegmentation.Optimal);

    [Test]
    [MethodDataSource(nameof(StandardCases))]
    public async Task StandardQr_ResolvedEncode_ReproducesTheCapturedOutput(StandardCase c)
    {
        var data = QRCodeGenerator.Create(c.Text, c.Ecc, c.Options);

        await Assert.That(data.Version).IsEqualTo(c.Version);
        await Assert.That(data.Size).IsEqualTo(QRCodeData.SizeFromVersion(c.Version) + 2 * c.Options.QuietZoneSize);
        await Assert.That(Sha(data.GetRawData())).IsEqualTo(c.RawSha);

        // The level the symbol carries, which is where a boost shows, and the mask a pinned one shows.
        await Assert.That(QRCodeDecoder.TryDecode(data, out var decoded, out var info)).IsTrue();
        await Assert.That(decoded).IsEqualTo(c.Text);
        await Assert.That(info.EccLevel).IsEqualTo(c.Level);
        if (c.Options.MaskPattern is { } mask)
            await Assert.That(info.MaskPattern).IsEqualTo(mask);
    }

    [Test]
    [MethodDataSource(nameof(StandardCases))]
    public async Task StandardQr_ResolvedEncodeToDestination_ReproducesTheCapturedOutput(StandardCase c)
    {
        var size = Sizing.Required(c.Text.AsSpan(), c.Ecc, c.Options);
        // A dirty buffer one byte longer than needed: every byte up to the size is written, and the next is left alone.
        var buffer = Enumerable.Repeat((byte)0xA5, size.BufferSize + 1).ToArray();

        var written = QRCodeGenerator.Create(c.Text.AsSpan(), c.Ecc, buffer, c.Options);

        await Assert.That(size.Version).IsEqualTo(c.Version);
        await Assert.That(written).IsEqualTo(size.BufferSize);
        await Assert.That(Sha(buffer.AsSpan(0, written).ToArray())).IsEqualTo(c.BufferSha);
        await Assert.That(buffer[written]).IsEqualTo((byte)0xA5);

        // The allocating overload's symbol module for module, quiet zone included: the two overloads write one symbol, which the other test decodes.
        var data = QRCodeGenerator.Create(c.Text, c.Ecc, c.Options);
        await Assert.That(written).IsEqualTo(data.Size * data.Size);
        await Assert.That(CountMismatches(buffer, data.Size, (row, col) => data[row, col])).IsEqualTo(0);
    }

    /// <summary>
    /// Every Optimal case writes what the single mode writes at the version it chose, so each one reaches the single-mode pipeline from the mixed-mode entry point.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(StandardOptimalCases))]
    public async Task StandardQr_OptimalCases_WriteTheSingleModeStream(StandardCase c)
    {
        var optimal = QRCodeGenerator.Create(c.Text, c.Ecc, c.Options);
        var single = QRCodeGenerator.Create(c.Text, c.Ecc, c.Options with { Segmentation = QRSegmentation.Single, Version = QRVersionRange.Exactly(optimal.Version) });

        await Assert.That(optimal.GetRawData()).IsEquivalentTo(single.GetRawData(), CollectionOrdering.Matching);
    }

    /// <summary>
    /// The classes that make the two analyses differ are reached: a byte order mark writes what it writes without <see cref="QRCodeGeneratorOptions.AllowKanji"/>, while Kanji mode without one changes the symbol.
    /// </summary>
    [Test]
    [Arguments("kanji-exact", false)]
    [Arguments("default-kanji", false)]
    [Arguments("optimal-kanji", false)]
    [Arguments("kanji-bom-exact", true)]
    [Arguments("kanji-bom-boost", true)]
    [Arguments("default-kanji-bom", true)]
    [Arguments("optimal-bom-kanji", true)]
    [Arguments("mixed-kanji-exact", true)]
    [Arguments("optimal-mixed-kanji", true)]
    public async Task StandardQr_KanjiCases_ReachTheirClass(string name, bool sameWithoutKanji)
    {
        var c = StandardCases().Single(x => x.Name == name);
        var withKanji = QRCodeGenerator.Create(c.Text, c.Ecc, c.Options).GetRawData();
        var withoutKanji = QRCodeGenerator.Create(c.Text, c.Ecc, c.Options with { AllowKanji = false }).GetRawData();

        await Assert.That(withKanji.AsSpan().SequenceEqual(withoutKanji)).IsEqualTo(sameWithoutKanji);
    }

    public readonly record struct StandardErrorCase(
        string Name, string Text, QREccLevel Ecc, QRCodeGeneratorOptions Options,
        string Exception, string? ParamName, string Message)
    {
        public override string ToString() => Name;
    }

    public static IEnumerable<StandardErrorCase> StandardErrorCases()
    {
        yield return new("exact-too-long", new string('A', 50), QREccLevel.M, new() { Version = QRVersionRange.Exactly(1) }, nameof(ArgumentException), "options", "Content does not fit a version 1 QR code at ECC level M (mode: Alphanumeric, ECI: Default, 50 data units). Widen the version range, lower the ECC level, or leave it at QRVersionRange.Any for automatic selection. (Parameter 'options')");
        yield return new("range-too-long", new string('A', 100), QREccLevel.M, new() { Version = QRVersionRange.AtMost(2) }, nameof(ArgumentException), "options", "Content does not fit any version in v1-v2 QR code at ECC level M (mode: Alphanumeric, ECI: Default, 100 data units). Widen the version range, lower the ECC level, or leave it at QRVersionRange.Any for automatic selection. (Parameter 'options')");
        yield return new("boost-exact-too-long", new string('A', 50), QREccLevel.L, new() { BoostEccLevel = true, Version = QRVersionRange.Exactly(1) }, nameof(ArgumentException), "options", "Content does not fit a version 1 QR code at ECC level L (mode: Alphanumeric, ECI: Default, 50 data units). Widen the version range, lower the ECC level, or leave it at QRVersionRange.Any for automatic selection. (Parameter 'options')");
        yield return new("boost-overflow", Oversized, QREccLevel.L, new() { BoostEccLevel = true }, nameof(InvalidOperationException), null, "Data too large for QR code (exceeds Version 40 capacity). Required: 4 header bits + 3000 data units, Mode: Byte, ECC: L, ECI: Default");
        yield return new("default-overflow", Oversized, QREccLevel.L, new(), nameof(InvalidOperationException), null, "Data too large for QR code (exceeds Version 40 capacity). Required: 4 header bits + 3000 data units, Mode: Byte, ECC: L, ECI: Default");
        yield return new("kanji-exact-too-long", Repeat(KanjiText, 40), QREccLevel.M, new() { AllowKanji = true, Version = QRVersionRange.Exactly(1) }, nameof(ArgumentException), "options", "Content does not fit a version 1 QR code at ECC level M (mode: Kanji, ECI: Default, 40 data units). Widen the version range, lower the ECC level, or leave it at QRVersionRange.Any for automatic selection. (Parameter 'options')");
        yield return new("kanji-bom-exact-too-long", KanjiText, QREccLevel.M, new() { AllowKanji = true, Utf8Bom = true, Version = QRVersionRange.Exactly(1) }, nameof(ArgumentException), "options", "Content does not fit a version 1 QR code at ECC level M (mode: Byte, ECI: Utf8, 24 data units). Widen the version range, lower the ECC level, or leave it at QRVersionRange.Any for automatic selection. (Parameter 'options')");
        yield return new("bom-utf8-exact-too-long", Repeat(UnicodeRun, 60), QREccLevel.M, new() { Utf8Bom = true, EciMode = EciMode.Utf8, Version = QRVersionRange.Exactly(2) }, nameof(ArgumentException), "options", "Content does not fit a version 2 QR code at ECC level M (mode: Byte, ECI: Utf8, 88 data units). Widen the version range, lower the ECC level, or leave it at QRVersionRange.Any for automatic selection. (Parameter 'options')");
        yield return new("invalid-level-exact", Ascii, (QREccLevel)7, new() { Version = QRVersionRange.Exactly(2) }, nameof(ArgumentOutOfRangeException), "eccLevel", "Invalid QR ECC level: 7 (Parameter 'eccLevel')");
        yield return new("invalid-eci-boost", Ascii, QREccLevel.M, new() { EciMode = (EciMode)77, BoostEccLevel = true }, nameof(ArgumentOutOfRangeException), "eciMode", "Unsupported ECI mode for QR: 77 (Parameter 'eciMode')");
        yield return new("optimal-bom-overflow", OversizedUnicode, QREccLevel.L, Optimal with { Utf8Bom = true }, nameof(InvalidOperationException), null, "Data too large for QR code (exceeds Version 40 capacity). Required: 16 header bits + 4500 data units, Mode: Byte, ECC: L, ECI: Utf8");
        yield return new("optimal-bom-exact-too-long", Unicode, QREccLevel.M, Optimal with { Utf8Bom = true, Version = QRVersionRange.Exactly(1) }, nameof(ArgumentException), "options", "Content does not fit a version 1 QR code at ECC level M (mode: Byte, ECI: Utf8, 16 data units). Widen the version range, lower the ECC level, or leave it at QRVersionRange.Any for automatic selection. (Parameter 'options')");
        yield return new("optimal-numeric-exact-too-long", Repeat(Digits, 100), QREccLevel.M, Optimal with { Version = QRVersionRange.Exactly(1) }, nameof(ArgumentException), "options", "Content does not fit a version 1 QR code at ECC level M (mode: Numeric, ECI: Default, 100 data units). Widen the version range, lower the ECC level, or leave it at QRVersionRange.Any for automatic selection. (Parameter 'options')");
    }

    [Test]
    [MethodDataSource(nameof(StandardErrorCases))]
    public async Task StandardQr_ResolvedEncodeErrors_AreTheCapturedOnes(StandardErrorCase c)
    {
        var fromCreate = Catch(() => QRCodeGenerator.Create(c.Text.AsSpan(), c.Ecc, c.Options));
        var fromDestination = Catch(() => QRCodeGenerator.Create(c.Text.AsSpan(), c.Ecc, new byte[200_000], c.Options));

        await AssertError(fromCreate, c.Exception, c.ParamName, c.Message);
        await AssertError(fromDestination, c.Exception, c.ParamName, c.Message);
    }

    public static IEnumerable<StandardErrorCase> StandardSmallDestinationCases()
    {
        yield return new("exact", Ascii, QREccLevel.L, new() { Version = QRVersionRange.Exactly(5) }, nameof(ArgumentException), "destination", "Destination buffer too small: 2025 bytes required (version 5, 45x45 modules), got 10 bytes. Use TryGetRequiredBufferSize to calculate the required size. (Parameter 'destination')");
        yield return new("boost", Ascii, QREccLevel.L, new() { BoostEccLevel = true }, nameof(ArgumentException), "destination", "Destination buffer too small: 841 bytes required (version 1, 29x29 modules), got 10 bytes. Use TryGetRequiredBufferSize to calculate the required size. (Parameter 'destination')");
        yield return new("default", Ascii, QREccLevel.L, new(), nameof(ArgumentException), "destination", "Destination buffer too small: 841 bytes required (version 1, 29x29 modules), got 10 bytes. Use TryGetRequiredBufferSize to calculate the required size. (Parameter 'destination')");
        yield return new("optimal-bom-exact", Unicode, QREccLevel.L, Optimal with { Utf8Bom = true, Version = QRVersionRange.Exactly(4) }, nameof(ArgumentException), "destination", "Destination buffer too small: 1681 bytes required (version 4, 41x41 modules), got 10 bytes. Use TryGetRequiredBufferSize to calculate the required size. (Parameter 'destination')");
        yield return new("optimal-numeric", Digits, QREccLevel.L, Optimal, nameof(ArgumentException), "destination", "Destination buffer too small: 841 bytes required (version 1, 29x29 modules), got 10 bytes. Use TryGetRequiredBufferSize to calculate the required size. (Parameter 'destination')");
    }

    [Test]
    [MethodDataSource(nameof(StandardSmallDestinationCases))]
    public async Task StandardQr_ResolvedEncode_DestinationTooSmall_IsTheCapturedError(StandardErrorCase c)
    {
        var error = Catch(() => QRCodeGenerator.Create(c.Text.AsSpan(), c.Ecc, new byte[10], c.Options));

        await AssertError(error, c.Exception, c.ParamName, c.Message);
    }

    // ---- Micro QR --------------------------------------------------------------------

    public readonly record struct MicroCase(
        string Name, string Text, MicroQREccLevel Ecc, MicroQRCodeGeneratorOptions Options,
        MicroQRVersion Version, MicroQREccLevel Level, string RawSha, string BufferSha)
    {
        public override string ToString() => Name;
    }

    private static readonly MicroQRCodeGeneratorOptions MicroOptimal = new() { Segmentation = MicroQRSegmentation.Optimal };

    public static IEnumerable<MicroCase> MicroCases()
    {
        yield return new("exact-numeric-mask", "12345678", MicroQREccLevel.L, new() { MaskPattern = 2, Version = MicroQRVersionRange.Exactly(MicroQRVersion.M3) }, MicroQRVersion.M3, MicroQREccLevel.L, "9293048A027F517217B6DC4879051A278339F8DF0B6C83184A0D119837328E1D", "6E0B6BEA806D6B0A265BA3CE0BFDB08353EF4F20D8E8F0A7C660E7063343603E");
        yield return new("at-least-alphanumeric", "AC-42", MicroQREccLevel.L, new() { Version = MicroQRVersionRange.AtLeast(MicroQRVersion.M3) }, MicroQRVersion.M3, MicroQREccLevel.L, "8ED31E2517EDB2EDEC1BFAC68ECE17211366C0E66155FE53D44B36BC23BC6F17", "CCD45AFA844B2F2853BD80978738ADFE8B32839C98B51A848F57769764E4267E");
        yield return new("between-byte", "hello", MicroQREccLevel.M, new() { Version = MicroQRVersionRange.Between(MicroQRVersion.M3, MicroQRVersion.M4) }, MicroQRVersion.M3, MicroQREccLevel.M, "FC478DAC4E0DC466D0E73F57F5F6AE89AEA4340372E2F05D7D6CF081E66ECCE9", "CC53A582800C171E68094F8DB64F05A51DB9783B54230B2CDAA22AC1CC0412DF");
        yield return new("kanji-exact", "吾輩は猫", MicroQREccLevel.M, new() { AllowKanji = true, Version = MicroQRVersionRange.Exactly(MicroQRVersion.M4) }, MicroQRVersion.M4, MicroQREccLevel.M, "2FD2C27F0967D304E44CFF134B6D3578256D89C862C4802342AB0C528ED6BA16", "47EFB8AE645B3E39E218C04ECFC2A4BD9AC93DFF859B502138195E1178F97205");
        yield return new("mixed-kanji-exact", MicroMixedKanji, MicroQREccLevel.L, new() { AllowKanji = true, Version = MicroQRVersionRange.Exactly(MicroQRVersion.M4) }, MicroQRVersion.M4, MicroQREccLevel.L, "D3FAA5891E712043F27F13C5C6F85FFCBC31840F20A84C98A3ECF16C4F6F630F", "723238A2B9A8C67371D0B9311CE7F5D1E5D1F88218B9811795CD62C77E50F2A4");
        yield return new("quiet-zone-0-exact", "AC-42", MicroQREccLevel.M, new() { QuietZoneSize = 0, Version = MicroQRVersionRange.Exactly(MicroQRVersion.M3) }, MicroQRVersion.M3, MicroQREccLevel.M, "062B557EC4CEC0EC8419BEB4D67F78B9C13838A4D0B250E3B81DADF4E8A9B2C4", "02A355D7D6A6F21D47480039E9CE81CC7D5552AC83AD4B9AD47DC57D0BD26293");
        yield return new("default-kanji", "吾輩は猫", MicroQREccLevel.M, new() { AllowKanji = true }, MicroQRVersion.M3, MicroQREccLevel.M, "7ADA331AE154483975C72B3B4F18D231F16960776F7FC3AFF023755EE596CDD8", "4D6045A28EEFFBF42C3F989E4FEEE614ACF43EB9834E1CCFA07DFCC0A3582BAF");
        yield return new("default-numeric", "12345", MicroQREccLevel.ErrorDetectionOnly, new(), MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly, "3BED9754163F7050021953CF7F57A089379BA2EFBF8C446EC5CC92D802AF91A9", "0EAB90455BFCE8E3F1A5CF447534388D962F2831C6C045B769862B7473D869AB");
        yield return new("optimal-numeric-exact", "12345678", MicroQREccLevel.L, MicroOptimal with { Version = MicroQRVersionRange.Exactly(MicroQRVersion.M3) }, MicroQRVersion.M3, MicroQREccLevel.L, "BC842C8063DFC35BE4FC34491F78E73532C6D70A3898217BA7FB005B3E039E93", "D5167CC2EBE30E42924C8400F3CD00D47D3FE2692E9D93D9C84C0CB39CDF3475");
        yield return new("optimal-numeric", "12345", MicroQREccLevel.ErrorDetectionOnly, MicroOptimal, MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly, "3BED9754163F7050021953CF7F57A089379BA2EFBF8C446EC5CC92D802AF91A9", "0EAB90455BFCE8E3F1A5CF447534388D962F2831C6C045B769862B7473D869AB");
        yield return new("optimal-mixed-kanji", MicroMixedKanji, MicroQREccLevel.L, MicroOptimal with { AllowKanji = true }, MicroQRVersion.M3, MicroQREccLevel.L, "97E127BB6986316AFD0F3C110EAEEBB8F7DA769FC6B63281BAEBA6CA6B190779", "B0CD4E7CF2057C9E19889B2A1E1123F31D100EE5E4A47F26A4C9AB15BDE04713");
        yield return new("optimal-kanji", "吾輩は猫", MicroQREccLevel.M, MicroOptimal with { AllowKanji = true }, MicroQRVersion.M3, MicroQREccLevel.M, "7ADA331AE154483975C72B3B4F18D231F16960776F7FC3AFF023755EE596CDD8", "4D6045A28EEFFBF42C3F989E4FEEE614ACF43EB9834E1CCFA07DFCC0A3582BAF");
        // A pinned mask where no split pays, and the plan the builder refuses, which falls back to the single-mode fit
        yield return new("optimal-numeric-mask", "12345678", MicroQREccLevel.L, MicroOptimal with { MaskPattern = 1 }, MicroQRVersion.M2, MicroQREccLevel.L, "02E42387D35B2813AE7C0BA5410553A5DD6095EA96077B9053EAA12EA70A5341", "7298D9AA18AEA4004B8C52ACA642B9E8278A1EF0130AEB2EEC9A0654B287F39B");
        yield return new("optimal-plan-refused", Latin1Lookalike, MicroQREccLevel.L, MicroOptimal, MicroQRVersion.M4, MicroQREccLevel.L, "A6C901E2383A6E0288417E69AA52456571B9864896050B67F032C6E18DDF67C4", "0D567E11D949F98B9149BE802BB9A5B82E0D2FDBE948F694F4C108D53328FD71");
        yield return new("optimal-plan-refused-mask-quiet-zone-0", Latin1Lookalike, MicroQREccLevel.L, MicroOptimal with { MaskPattern = 2, QuietZoneSize = 0 }, MicroQRVersion.M4, MicroQREccLevel.L, "656767AD29A61F6504E37E0A2B144A44D9FA3D74B1A7C9008D06911BC134319A", "E988FD47A634F283AB501B9CD678C69B7150428CDF63C4719DBCAD6B5533CBC8");
    }

    public static IEnumerable<MicroCase> MicroOptimalCases() => MicroCases().Where(c => c.Options.Segmentation == MicroQRSegmentation.Optimal);

    [Test]
    [MethodDataSource(nameof(MicroCases))]
    public async Task MicroQr_ResolvedEncode_ReproducesTheCapturedOutput(MicroCase c)
    {
        var data = MicroQRCodeGenerator.Create(c.Text, c.Ecc, c.Options);

        await Assert.That(data.Version).IsEqualTo(c.Version);
        await Assert.That(data.Size).IsEqualTo(MicroQRConstants.SizeFromVersion(c.Version) + 2 * c.Options.QuietZoneSize);
        await Assert.That(Sha(data.GetRawData())).IsEqualTo(c.RawSha);

        await Assert.That(MicroQRCodeDecoder.TryDecode(data, out var decoded, out var info)).IsTrue();
        await Assert.That(decoded).IsEqualTo(c.Text);
        await Assert.That(info.EccLevel).IsEqualTo(c.Level);
        if (c.Options.MaskPattern is { } mask)
            await Assert.That(info.MaskPattern).IsEqualTo(mask);
    }

    [Test]
    [MethodDataSource(nameof(MicroCases))]
    public async Task MicroQr_ResolvedEncodeToDestination_ReproducesTheCapturedOutput(MicroCase c)
    {
        var size = Sizing.Required(c.Text.AsSpan(), c.Ecc, c.Options);
        var buffer = Enumerable.Repeat((byte)0xA5, size.BufferSize + 1).ToArray();

        var written = MicroQRCodeGenerator.Create(c.Text.AsSpan(), c.Ecc, buffer, c.Options);

        await Assert.That(size.Version).IsEqualTo(c.Version);
        await Assert.That(written).IsEqualTo(size.BufferSize);
        await Assert.That(Sha(buffer.AsSpan(0, written).ToArray())).IsEqualTo(c.BufferSha);
        await Assert.That(buffer[written]).IsEqualTo((byte)0xA5);

        var data = MicroQRCodeGenerator.Create(c.Text, c.Ecc, c.Options);
        await Assert.That(written).IsEqualTo(data.Size * data.Size);
        await Assert.That(CountMismatches(buffer, data.Size, (row, col) => data[row, col])).IsEqualTo(0);
    }

    [Test]
    [MethodDataSource(nameof(MicroOptimalCases))]
    public async Task MicroQr_OptimalCases_WriteTheSingleModeStream(MicroCase c)
    {
        var optimal = MicroQRCodeGenerator.Create(c.Text, c.Ecc, c.Options);
        var single = MicroQRCodeGenerator.Create(c.Text, c.Ecc, c.Options with { Segmentation = MicroQRSegmentation.Single, Version = MicroQRVersionRange.Exactly(optimal.Version) });

        await Assert.That(optimal.GetRawData()).IsEquivalentTo(single.GetRawData(), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("kanji-exact", false)]
    [Arguments("default-kanji", false)]
    [Arguments("optimal-kanji", false)]
    [Arguments("mixed-kanji-exact", true)]
    [Arguments("optimal-mixed-kanji", true)]
    public async Task MicroQr_KanjiCases_ReachTheirClass(string name, bool sameWithoutKanji)
    {
        var c = MicroCases().Single(x => x.Name == name);
        var withKanji = MicroQRCodeGenerator.Create(c.Text, c.Ecc, c.Options).GetRawData();
        var withoutKanji = MicroQRCodeGenerator.Create(c.Text, c.Ecc, c.Options with { AllowKanji = false }).GetRawData();

        await Assert.That(withKanji.AsSpan().SequenceEqual(withoutKanji)).IsEqualTo(sameWithoutKanji);
    }

    /// <summary>
    /// The mixed texts are the class where the mixed-mode and single-mode analyses differ: Kanji-plannable under a plan, plain UTF-8 Byte mode alone, and alike in every other field.
    /// </summary>
    [Test]
    [Arguments(MixedKanji)]
    [Arguments(MicroMixedKanji)]
    public async Task MixedKanjiTexts_AreKanjiPlannableOnlyUnderAPlan(string text)
    {
        var planned = TextAnalyzer.Analyze(text, EciMode.Default, allowKanji: true, planKanji: true);
        var single = TextAnalyzer.Analyze(text, EciMode.Default, allowKanji: true);

        await Assert.That(planned.KanjiPlannable).IsTrue();
        await Assert.That(single.KanjiPlannable).IsFalse();
        await Assert.That(single.EncodingMode).IsEqualTo(EncodingMode.Byte);
        await Assert.That(planned with { KanjiPlannable = false }).IsEqualTo(single);
    }

    /// <summary>A pinned mask shows only where the automatic choice would pick another, so each pinned case pins one it would not.</summary>
    [Test]
    public async Task PinnedMaskCases_PinAMaskTheAutomaticChoiceWouldNotPick()
    {
        var standard = StandardCases().Where(c => c.Options.MaskPattern is not null).ToArray();
        var micro = MicroCases().Where(c => c.Options.MaskPattern is not null).ToArray();
        await Assert.That(standard.Length).IsGreaterThan(0);
        await Assert.That(micro.Length).IsGreaterThan(0);

        foreach (var c in standard)
        {
            await Assert.That(QRCodeDecoder.TryDecode(QRCodeGenerator.Create(c.Text, c.Ecc, c.Options with { MaskPattern = null }), out _, out var automatic)).IsTrue().Because($"Standard QR {c.Name}");
            await Assert.That(automatic.MaskPattern).IsNotEqualTo(c.Options.MaskPattern!.Value).Because($"Standard QR {c.Name}");
        }
        foreach (var c in micro)
        {
            await Assert.That(MicroQRCodeDecoder.TryDecode(MicroQRCodeGenerator.Create(c.Text, c.Ecc, c.Options with { MaskPattern = null }), out _, out var automatic)).IsTrue().Because($"Micro QR {c.Name}");
            await Assert.That(automatic.MaskPattern).IsNotEqualTo(c.Options.MaskPattern!.Value).Because($"Micro QR {c.Name}");
        }
    }

    /// <summary>
    /// The plan-refused cases reach the fallback after the builder refuses, not the no-split exit: the scan chose a plan at a smaller version than the symbol written, and the builder refuses to build it.
    /// </summary>
    [Test]
    [Arguments("optimal-plan-refused")]
    [Arguments("optimal-plan-refused-mask-quiet-zone-0")]
    public async Task MicroQr_PlanRefusedCases_HadAPlanTheBuilderRefused(string name)
    {
        var c = MicroCases().Single(x => x.Name == name);
        var analysis = TextAnalyzer.Analyze(c.Text, EciMode.Default, allowKanji: c.Options.AllowKanji, planKanji: true);
        var plan = new ModeSegment[MicroQRSegmentPlanner.MaxPlannableChars];

        // Not Kanji-plannable, so the scan keeps no Kanji table and the overload without one chooses what the encode chose.
        await Assert.That(analysis.KanjiPlannable).IsFalse();
        await Assert.That(MicroQRSegmentPlanner.TrySelectVersion(c.Text, in analysis, c.Ecc, c.Options.Version, out var planned, out var useSegments, out _)).IsTrue();
        await Assert.That(useSegments).IsTrue();
        await Assert.That((int)planned).IsLessThan((int)c.Version);
        await Assert.That(MicroQRSegmentPlanner.TryBuildPlan(c.Text, analysis.EciMode, planned, c.Ecc, plan, out _)).IsFalse();
    }

    public readonly record struct MicroErrorCase(
        string Name, string Text, MicroQREccLevel Ecc, MicroQRCodeGeneratorOptions Options,
        string Exception, string? ParamName, string Message)
    {
        public override string ToString() => Name;
    }

    public static IEnumerable<MicroErrorCase> MicroErrorCases()
    {
        yield return new("exact-mode-unavailable", "AC-42", MicroQREccLevel.ErrorDetectionOnly, new() { Version = MicroQRVersionRange.Exactly(MicroQRVersion.M1) }, nameof(ArgumentException), "requestedVersion", "Encoding mode Alphanumeric is not available on Micro QR version M1 (M1: Numeric; M2: +Alphanumeric; M3/M4: +Byte). (Parameter 'requestedVersion')");
        yield return new("exact-too-long", "1234567890123", MicroQREccLevel.L, new() { Version = MicroQRVersionRange.Exactly(MicroQRVersion.M2) }, nameof(ArgumentException), "requestedVersion", "Content is too long for Micro QR M2 at ECC level L: 13 digits in Numeric mode, but the maximum is 10 digits. Shorten the content, lower the ECC level, or use Standard QR (QRCodeGenerator) for longer content. (Parameter 'requestedVersion')");
        yield return new("range-level-unavailable", "AC-42", MicroQREccLevel.Q, new() { Version = MicroQRVersionRange.Between(MicroQRVersion.M2, MicroQRVersion.M3) }, nameof(ArgumentException), "eccLevel", "ECC level Q is not available on any Micro QR version in M2-M3 (M1: ErrorDetectionOnly; M2/M3: L, M; M4: L, M, Q). (Parameter 'eccLevel')");
        yield return new("exact-level-unavailable", "12345", MicroQREccLevel.L, new() { Version = MicroQRVersionRange.Exactly(MicroQRVersion.M1) }, nameof(ArgumentException), "eccLevel", "ECC level L is not available on any Micro QR version in M1 (M1: ErrorDetectionOnly; M2/M3: L, M; M4: L, M, Q). (Parameter 'eccLevel')");
        yield return new("at-least-too-long", new string('A', 30), MicroQREccLevel.L, new() { Version = MicroQRVersionRange.AtLeast(MicroQRVersion.M2) }, nameof(ArgumentException), null, "Content is too long for Micro QR: 30 characters in Alphanumeric mode, but ECC level L fits at most 21 characters (M4). Shorten the content, lower the ECC level, or use Standard QR (QRCodeGenerator) for longer content.");
        yield return new("kanji-exact-too-long", "吾輩は猫である。名前は", MicroQREccLevel.M, new() { AllowKanji = true, Version = MicroQRVersionRange.Exactly(MicroQRVersion.M4) }, nameof(ArgumentException), "requestedVersion", "Content is too long for Micro QR M4 at ECC level M: 11 characters in Kanji mode, but the maximum is 8 characters. Shorten the content, lower the ECC level, or use Standard QR (QRCodeGenerator) for longer content. (Parameter 'requestedVersion')");
        yield return new("invalid-level-exact", "12345", (MicroQREccLevel)9, new() { Version = MicroQRVersionRange.Exactly(MicroQRVersion.M2) }, nameof(ArgumentOutOfRangeException), "eccLevel", "Invalid Micro QR ECC level: 9 (Parameter 'eccLevel')");
        yield return new("optimal-exact-too-long", "1234567890123", MicroQREccLevel.L, MicroOptimal with { Version = MicroQRVersionRange.Exactly(MicroQRVersion.M2) }, nameof(ArgumentException), "requestedVersion", "Content is too long for Micro QR M2 at ECC level L: 13 digits in Numeric mode, but the maximum is 10 digits. Shorten the content, lower the ECC level, or use Standard QR (QRCodeGenerator) for longer content. (Parameter 'requestedVersion')");
    }

    [Test]
    [MethodDataSource(nameof(MicroErrorCases))]
    public async Task MicroQr_ResolvedEncodeErrors_AreTheCapturedOnes(MicroErrorCase c)
    {
        var fromCreate = Catch(() => MicroQRCodeGenerator.Create(c.Text.AsSpan(), c.Ecc, c.Options));
        var fromDestination = Catch(() => MicroQRCodeGenerator.Create(c.Text.AsSpan(), c.Ecc, new byte[10_000], c.Options));

        await AssertError(fromCreate, c.Exception, c.ParamName, c.Message);
        await AssertError(fromDestination, c.Exception, c.ParamName, c.Message);
    }

    public static IEnumerable<MicroErrorCase> MicroSmallDestinationCases()
    {
        yield return new("exact", "AC-42", MicroQREccLevel.L, new() { Version = MicroQRVersionRange.Exactly(MicroQRVersion.M3) }, nameof(ArgumentException), "destination", "Destination buffer too small: 361 bytes required (version M3, 19x19 modules), got 10 bytes. Use TryGetRequiredBufferSize to calculate the required size. (Parameter 'destination')");
        yield return new("default", "AC-42", MicroQREccLevel.L, new(), nameof(ArgumentException), "destination", "Destination buffer too small: 289 bytes required (version M2, 17x17 modules), got 10 bytes. Use TryGetRequiredBufferSize to calculate the required size. (Parameter 'destination')");
        yield return new("optimal-numeric", "12345", MicroQREccLevel.ErrorDetectionOnly, MicroOptimal, nameof(ArgumentException), "destination", "Destination buffer too small: 225 bytes required (version M1, 15x15 modules), got 10 bytes. Use TryGetRequiredBufferSize to calculate the required size. (Parameter 'destination')");
    }

    [Test]
    [MethodDataSource(nameof(MicroSmallDestinationCases))]
    public async Task MicroQr_ResolvedEncode_DestinationTooSmall_IsTheCapturedError(MicroErrorCase c)
    {
        var error = Catch(() => MicroQRCodeGenerator.Create(c.Text.AsSpan(), c.Ecc, new byte[10], c.Options));

        await AssertError(error, c.Exception, c.ParamName, c.Message);
    }

    // ---- helpers ---------------------------------------------------------------------

    private static Exception? Catch(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    private static async Task AssertError(Exception? error, string exception, string? paramName, string message)
    {
        await Assert.That(error).IsNotNull();
        await Assert.That(error!.GetType().Name).IsEqualTo(exception);
        await Assert.That((error as ArgumentException)?.ParamName).IsEqualTo(paramName);
        await Assert.That(error.Message).IsEqualTo(message);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>Modules where a one-byte-per-module destination buffer of <paramref name="size"/> × <paramref name="size"/> disagrees with a matrix.</summary>
    private static int CountMismatches(byte[] buffer, int size, Func<int, int, bool> isDark)
    {
        var mismatches = 0;
        for (var row = 0; row < size; row++)
        {
            for (var col = 0; col < size; col++)
            {
                if ((buffer[row * size + col] != 0) != isDark(row, col))
                    mismatches++;
            }
        }
        return mismatches;
    }

    private static string Repeat(string text, int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = text[i % text.Length];
        return new string(chars);
    }
}
