using System.Security.Cryptography;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="QRCodeGeneratorOptions"/> and <see cref="MicroQRCodeGeneratorOptions"/>,
/// the only way to configure a generator since 2.0.0 removed the released parameter
/// lists (specs/qrcode-symbologies.md, Public API direction).
/// </summary>
/// <remarks>
/// <para>
/// Two things are pinned here. First, <c>default(T)</c> has to carry the documented
/// defaults, which for quiet zone is 4 (Standard QR) and 2 (Micro QR) rather than the
/// zero value, while 0 stays expressible. Second, the output of every configuration the
/// removed overloads could express, captured from those overloads before they were
/// deleted: they were the shipped contract, and removing the spelling must not change
/// the symbol.
/// </para>
/// <para>
/// The one place the options surface does <em>more</em> than the released one did is
/// Standard QR sizing. The released <c>GetRequiredBufferSize</c> never had a version parameter,
/// so an ignored <c>Version</c> would have been a silent trap; sizing honours it and
/// reports a version that cannot hold the content the same way Micro QR and rMQR
/// already do.
/// </para>
/// </remarks>
public class GeneratorOptionsTest
{
    private const int StandardQrQuietZone = 4;
    private const int MicroQrQuietZone = 2;   // ISO/IEC 18004

    private const string Ascii = "HELLO WORLD 123";
    private const string Latin1 = "Café déjà vu";
    private const string Unicode = "日本語のテキスト";
    private const string Digits = "0123456789";

    // ---- default(T) --------------------------------------------------------------

    [Test]
    public async Task StandardQrOptions_Default_CarriesTheDocumentedDefaults()
    {
        var options = default(QRCodeGeneratorOptions);

        await Assert.That(options.EciMode).IsEqualTo(EciMode.Default);
        await Assert.That(options.Utf8BOM).IsFalse();
        await Assert.That(options.Version.IsAny).IsTrue();
        await Assert.That(options.QuietZoneSize).IsEqualTo(StandardQrQuietZone);
        await Assert.That(QRCodeGeneratorOptions.Default).IsEqualTo(options);
    }

    [Test]
    public async Task MicroQrOptions_Default_CarriesTheDocumentedDefaults()
    {
        var options = default(MicroQRCodeGeneratorOptions);

        await Assert.That(options.Version.IsAny).IsTrue();
        await Assert.That(options.QuietZoneSize).IsEqualTo(MicroQrQuietZone);
        await Assert.That(MicroQRCodeGeneratorOptions.Default).IsEqualTo(options);
    }

    [Test]
    public async Task QuietZoneSize_WrittenAsItsDefaultValue_IsIndistinguishableFromUnset()
    {
        // Offset encoding, as in RmQRCodeGeneratorOptions: an explicitly written default
        // must collapse onto the unset form or the generated equality lies.
        var standard = new QRCodeGeneratorOptions { QuietZoneSize = StandardQrQuietZone };
        await Assert.That(standard).IsEqualTo(default(QRCodeGeneratorOptions));
        await Assert.That(standard.GetHashCode()).IsEqualTo(default(QRCodeGeneratorOptions).GetHashCode());

        var micro = new MicroQRCodeGeneratorOptions { QuietZoneSize = MicroQrQuietZone };
        await Assert.That(micro).IsEqualTo(default(MicroQRCodeGeneratorOptions));
        await Assert.That(micro.GetHashCode()).IsEqualTo(default(MicroQRCodeGeneratorOptions).GetHashCode());
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(4)]
    [Arguments(9)]
    public async Task QuietZoneSize_RoundTripsOverItsRange(int quietZoneSize)
    {
        await Assert.That(new QRCodeGeneratorOptions { QuietZoneSize = quietZoneSize }.QuietZoneSize).IsEqualTo(quietZoneSize);
        await Assert.That(new MicroQRCodeGeneratorOptions { QuietZoneSize = quietZoneSize }.QuietZoneSize).IsEqualTo(quietZoneSize);
    }

    [Test]
    public async Task QuietZoneSize_Zero_IsExpressibleAndDiffersFromUnset()
    {
        await Assert.That(new QRCodeGeneratorOptions { QuietZoneSize = 0 }).IsNotEqualTo(default(QRCodeGeneratorOptions));
        await Assert.That(new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 }).IsNotEqualTo(default(MicroQRCodeGeneratorOptions));
    }

    // ---- Standard QR: the released output, frozen as values ---------------------------
    //
    // These are the configurations the 1.1.1 parameter list overloads could express, and
    // this test used to run each one through both spellings and compare. The overloads
    // were removed in 2.0.0, so what they produced is pinned here instead: version, matrix
    // size, and the SHA-256 of both the serialized matrix and the destination buffer, all
    // captured from the released overloads on 2026-09-06 immediately before the removal.
    // A difference now is an encoder change rather than a disagreement between two
    // spellings, which is the stronger of the two guarantees; the three entry points
    // (string, span, destination) are still cross-checked on every row.

    public readonly record struct StandardQrCase(
        string Text, ECCLevel Ecc, bool Utf8BOM, EciMode Eci, int RequestedVersion, int QuietZone,
        int Version, int QrSize, string RawSha, string BufferSha)
    {
        public QRCodeGeneratorOptions Options => new()
        {
            Utf8BOM = Utf8BOM,
            EciMode = Eci,
            Version = RequestedVersion == -1 ? QRCodeVersionRange.Any : QRCodeVersionRange.Exactly(RequestedVersion),
            QuietZoneSize = QuietZone,
        };
    }

    public static IEnumerable<StandardQrCase> StandardQrConfigurations()
    {
        yield return new(Ascii, ECCLevel.M, false, EciMode.Default, -1, 4, 1, 29, "DF3513E09689BEFE49C38DF1BAA0707331B7A9CA92051E0881577D45399EB0C2", "4F9FD23E7FCA32AFFE0501DBF0F53501121AD58C155FBBE36635474C94FE6F01");
        yield return new(Ascii, ECCLevel.L, false, EciMode.Default, -1, 0, 1, 21, "FF5525B60FC0B9AB33AE35DD2E4D8EE07DE9C073646354B2B06737994FBEEBF7", "A3B3FADF5892440C88E68365D6917E46544BF61CA018C4EA57BC3577D8E180D6");
        yield return new(Ascii, ECCLevel.Q, false, EciMode.Default, -1, 7, 1, 35, "4805298E52D3BC3001400129FE397B43FBF61411289699CBB2E29B1FD0D4A640", "E19CA367EEF7ADF638BE03D04AC99DB5B4140F4BDD90B3193A7AC9DFCA5CD1D9");
        yield return new(Ascii, ECCLevel.H, false, EciMode.Default, 10, 4, 10, 65, "8582F27F9C01FE3B087624129D8B526E8E196103BD0C4423F69ECF59B89326D8", "5FF120D0E32BEFA10C8498C1A6A01EFCC392A57DA93C94958B0C166F267806E2");
        yield return new(Digits, ECCLevel.M, false, EciMode.Default, 1, 4, 1, 29, "F69134B693DBC5CB51FF8B4129F922BF528E16284E8FF3FC05FB7FC8D42811F0", "EA5AEE3F4FB5BF3A33E69D5E858664CDBAD3770EBAC3C5CBE3F0F81370C427F9");
        yield return new(Digits, ECCLevel.M, false, EciMode.Default, 40, 2, 40, 181, "B1B7DB1BE8E1AAA9121493ACB207614E8C2DB349677353B72122E291A05D6332", "57395DBD1E96294EEBC3B0C76C5A6D968BD79C9B784AE914D3BC0D1522B71DF9");
        yield return new(Latin1, ECCLevel.M, false, EciMode.Default, -1, 4, 1, 29, "32CB9B35BE3ED7A0E0FE1B406D40C878BF7A0639DA911C42FFCF1938026232CD", "7E16CA26465E45B4261404FCB5A205E7048D930169164AC75BF828CBDA56C45C");
        yield return new(Latin1, ECCLevel.M, false, EciMode.Iso8859_1, -1, 4, 1, 29, "32CB9B35BE3ED7A0E0FE1B406D40C878BF7A0639DA911C42FFCF1938026232CD", "7E16CA26465E45B4261404FCB5A205E7048D930169164AC75BF828CBDA56C45C");
        yield return new(Latin1, ECCLevel.M, false, EciMode.Utf8, -1, 4, 2, 33, "F462B9E5DD8FD73F38B5CC9EA6D98234E2C8E8F19023432EE87B82519E3DA538", "AF4BEAD356645CFD2DEDE5170115090271AA6C90F691C0D872154583A6F2F1C6");
        yield return new(Unicode, ECCLevel.M, false, EciMode.Default, -1, 4, 2, 33, "5A532AA6FC5B854162FC73366E67F054D9B7BC001E1C1ED43E0E52900AD72CE3", "52FC0E4364CA2885A62746136B6B78682280CCDA28C2F3B4FC246793C5776A3A");
        yield return new(Unicode, ECCLevel.M, false, EciMode.Utf8, -1, 1, 2, 27, "5A532AA6FC5B854162FC73366E67F054D9B7BC001E1C1ED43E0E52900AD72CE3", "B2519103EF39CED7E0E018C232FEC391B381FC656CEB1181FA6A3327964CBA46");
        yield return new(Unicode, ECCLevel.H, true, EciMode.Utf8, -1, 4, 4, 41, "7AD2CE76A220D86716F4B5D89410A8521BB556DE778C85476EBE4A53E5B568A5", "29047B9550A8C963E58BD2BCA046751FB8C7DC2A693100B247EBDFAEF33B1E5E");
        yield return new(Ascii, ECCLevel.M, true, EciMode.Utf8, 12, 3, 12, 71, "A6355BAA48669761D513937555235945CF020E26A6F8475847DC4AF61F7C385C", "31A6A4B8C0A96D7620CA7C00DC2A3B357A06A502D7A18F2A9FC24FB0F3FA91BC");
        yield return new("", ECCLevel.M, false, EciMode.Default, -1, 4, 1, 29, "B1E6AB2001B4E374C1AFD9CBA7C57FA0C0ACAEB22E871DF1C13DB686A8892EF9", "2409B6A5CC02DE7B49C526B19337725D15A816BD6CFA421128943E06260FC128");
    }

    [Test]
    [MethodDataSource(nameof(StandardQrConfigurations))]
    public async Task StandardQr_OptionsOverload_ReproducesTheReleasedOutput(StandardQrCase c)
    {
        var viaOptions = QRCodeGenerator.CreateQrCode(c.Text, c.Ecc, c.Options);
        var viaOptionsSpan = QRCodeGenerator.CreateQrCode(c.Text.AsSpan(), c.Ecc, c.Options);

        await Assert.That(viaOptions.Version).IsEqualTo(c.Version);
        await Assert.That(viaOptions.Size).IsEqualTo(c.QrSize);
        await Assert.That(Sha(viaOptions.GetRawData())).IsEqualTo(c.RawSha);
        await Assert.That(viaOptionsSpan.GetRawData()).IsEquivalentTo(viaOptions.GetRawData());
    }

    [Test]
    [MethodDataSource(nameof(StandardQrConfigurations))]
    public async Task StandardQr_OptionsDestinationOverload_ReproducesTheReleasedOutput(StandardQrCase c)
    {
        var size = Sizing.Required(c.Text.AsSpan(), c.Ecc, c.Options);
        await Assert.That(size.Version).IsEqualTo(c.Version);
        await Assert.That(size.QrSize).IsEqualTo(c.QrSize);
        await Assert.That(size.BufferSize).IsEqualTo(c.QrSize * c.QrSize);

        var fromOptions = new byte[size.BufferSize];
        var fromOptionsString = new byte[size.BufferSize];
        var written = QRCodeGenerator.CreateQrCode(c.Text.AsSpan(), c.Ecc, fromOptions, c.Options);
        var writtenString = QRCodeGenerator.CreateQrCode(c.Text, c.Ecc, fromOptionsString, c.Options);

        await Assert.That(written).IsEqualTo(size.BufferSize);
        await Assert.That(writtenString).IsEqualTo(written);
        await Assert.That(Sha(fromOptions)).IsEqualTo(c.BufferSha);
        await Assert.That(fromOptionsString).IsEquivalentTo(fromOptions);
    }

    // ---- Micro QR: the released output, frozen as values ------------------------------

    public readonly record struct MicroQrCase(
        string Text, MicroQREccLevel Ecc, MicroQRVersion? RequestedVersion, int QuietZone,
        MicroQRVersion Version, int QrSize, string RawSha, string BufferSha)
    {
        public MicroQRCodeGeneratorOptions Options => new()
        {
            Version = RequestedVersion is null ? MicroQRVersionRange.Any : MicroQRVersionRange.Exactly(RequestedVersion.Value),
            QuietZoneSize = QuietZone,
        };
    }

    public static IEnumerable<MicroQrCase> MicroQrConfigurations()
    {
        yield return new("12345", MicroQREccLevel.ErrorDetectionOnly, null, 2, MicroQRVersion.M1, 15, "3BED9754163F7050021953CF7F57A089379BA2EFBF8C446EC5CC92D802AF91A9", "0EAB90455BFCE8E3F1A5CF447534388D962F2831C6C045B769862B7473D869AB");
        yield return new("12345", MicroQREccLevel.ErrorDetectionOnly, MicroQRVersion.M1, 0, MicroQRVersion.M1, 11, "3BED9754163F7050021953CF7F57A089379BA2EFBF8C446EC5CC92D802AF91A9", "F8EFA9C1B0150A6952B22AF65F27DD871EB648B63616F39F5FE7B52E22A3FFB5");
        yield return new("1234567890", MicroQREccLevel.L, null, 2, MicroQRVersion.M2, 17, "A68021E67420A7FE94CA31990B84D97DA5B6B20A30DFDC930143B3F8E505B160", "877A60F1F02F7BCA97A3BED5746D13A7DC7218A9587EF08755193DEB15F5D00B");
        yield return new("1234567890", MicroQREccLevel.L, MicroQRVersion.M3, 5, MicroQRVersion.M3, 25, "F908D5183FABDF4ED0EBD9D001BD95B34718B055C8012BB8146793A5B12DFD12", "C47ED21D1D97A2EED648F131F2CF609F602C656682B3FF8E670DF5CDD18BB88D");
        yield return new("AC-42", MicroQREccLevel.L, null, 2, MicroQRVersion.M2, 17, "ECA67C8559F2D6AD4FF9CAE9BB35A9CCA58CDD8E5937461B0083BEE38FE18B45", "B7743C78CEE3B8FFFBA9DC8CB3B4314F19F5049DB46C21A5C7E65A06C6323077");
        yield return new("AC-42", MicroQREccLevel.M, MicroQRVersion.M3, 2, MicroQRVersion.M3, 19, "062B557EC4CEC0EC8419BEB4D67F78B9C13838A4D0B250E3B81DADF4E8A9B2C4", "AA54743193A53C45B6083FFE4E00F82EF822A7EBB738B35B977DB4E4D6BD1996");
        yield return new("hello", MicroQREccLevel.L, MicroQRVersion.M4, 1, MicroQRVersion.M4, 19, "083021B65DB60B42CEC6DDEDAFE2573464D4D4C7BCB06D2DD5709F2F4E4CAE7B", "57C96A57683BAA91ABBE4AA231AD322FD4EB3FEBCB7A96F9CBB513F64C533AF7");
        yield return new("hello", MicroQREccLevel.Q, MicroQRVersion.M4, 2, MicroQRVersion.M4, 21, "35E45C0A2C0A55D081D9451030F6DD53E252C72787404FE322DE5FEE430B469C", "83F08D69241D9E420CCF6B91CCA4E87CA082BDCB2A5B3D1C31139D86195A02E2");
    }

    [Test]
    [MethodDataSource(nameof(MicroQrConfigurations))]
    public async Task MicroQr_OptionsOverload_ReproducesTheReleasedOutput(MicroQrCase c)
    {
        var viaOptions = MicroQRCodeGenerator.CreateMicroQRCode(c.Text, c.Ecc, c.Options);
        var viaOptionsSpan = MicroQRCodeGenerator.CreateMicroQRCode(c.Text.AsSpan(), c.Ecc, c.Options);

        await Assert.That(viaOptions.Version).IsEqualTo(c.Version);
        await Assert.That(viaOptions.Size).IsEqualTo(c.QrSize);
        await Assert.That(Sha(viaOptions.GetRawData())).IsEqualTo(c.RawSha);
        await Assert.That(viaOptionsSpan.GetRawData()).IsEquivalentTo(viaOptions.GetRawData());
    }

    [Test]
    [MethodDataSource(nameof(MicroQrConfigurations))]
    public async Task MicroQr_OptionsSizingAndDestination_ReproduceTheReleasedOutput(MicroQrCase c)
    {
        var size = Sizing.Required(c.Text.AsSpan(), c.Ecc, c.Options);
        await Assert.That(size.Version).IsEqualTo(c.Version);
        await Assert.That(size.QrSize).IsEqualTo(c.QrSize);
        await Assert.That(size.BufferSize).IsEqualTo(c.QrSize * c.QrSize);

        var fromOptions = new byte[size.BufferSize];
        var written = MicroQRCodeGenerator.CreateMicroQRCode(c.Text.AsSpan(), c.Ecc, fromOptions, c.Options);

        await Assert.That(written).IsEqualTo(size.BufferSize);
        await Assert.That(Sha(fromOptions)).IsEqualTo(c.BufferSha);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    // ---- Standard QR sizing honours Version (the one added capability) ---------------

    [Test]
    public async Task StandardQrSizing_ExplicitVersion_ReportsThatVersion()
    {
        var automatic = Sizing.Required(Digits.AsSpan(), ECCLevel.M, QRCodeGeneratorOptions.Default);
        var pinned = Sizing.Required(Digits.AsSpan(), ECCLevel.M, new QRCodeGeneratorOptions { Version = QRCodeVersionRange.Exactly(15) });

        await Assert.That(automatic.Version).IsEqualTo(1);
        await Assert.That(pinned.Version).IsEqualTo(15);
        await Assert.That(pinned.QrSize).IsEqualTo(QRCodeData.SizeFromVersion(15) + 8);

        // and the size it reports is the size the encode actually writes
        var buffer = new byte[pinned.BufferSize];
        var written = QRCodeGenerator.CreateQrCode(Digits.AsSpan(), ECCLevel.M, buffer, new QRCodeGeneratorOptions { Version = QRCodeVersionRange.Exactly(15) });
        await Assert.That(written).IsEqualTo(pinned.BufferSize);
    }

    [Test]
    public async Task StandardQrSizing_ExplicitVersionTooSmallForTheContent_IsNotAFit()
    {
        var content = new string('A', 100);   // needs well above version 1

        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(content.AsSpan(), ECCLevel.M, out var size, new QRCodeGeneratorOptions { Version = QRCodeVersionRange.Exactly(1) })).IsFalse();
        await Assert.That(size).IsEqualTo(default(QRCodeCalculatedSize));

        // the same content at a version that does hold it is a fit
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(content.AsSpan(), ECCLevel.M, out var ok, new QRCodeGeneratorOptions { Version = QRCodeVersionRange.Exactly(10) })).IsTrue();
        await Assert.That(ok.Version).IsEqualTo(10);
    }

    [Test]
    [Arguments(0)]
    [Arguments(41)]
    [Arguments(-2)]
    public async Task StandardQrOptions_VersionOutOfRange_ThrowsBeforeAGeneratorIsCalled(int version)
    {
        // An invalid version is an argument error, not a "does not fit". Since Phase 3 it
        // is rejected when the range is constructed rather than when a generator reads it,
        // so an option set carrying an impossible version cannot be built at all. The
        // per-factory coverage lives in VersionRangeTest.
        await Assert.That(() => new QRCodeGeneratorOptions { Version = QRCodeVersionRange.Exactly(version) }).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task DefaultLiteralAsTheThirdArgument_StillMeansAllDefaults()
    {
        // Adding the options overloads moved this call: `default` now binds to the options
        // parameter rather than to `utf8BOM` / `requestedVersion`, because a candidate that
        // needs no optional-parameter substitution wins. Both readings mean "all defaults",
        // so the symbol must be unchanged. (Three sibling shapes did not compile at all
        // before, being ambiguous with the Span<byte> destination overload.)
        var standardDefault = QRCodeGenerator.CreateQrCode("hello world", ECCLevel.M, default);
        var standardOmitted = QRCodeGenerator.CreateQrCode("hello world", ECCLevel.M);
        await Assert.That(standardDefault.Version).IsEqualTo(standardOmitted.Version);
        await Assert.That(standardDefault.GetRawData().AsSpan().SequenceEqual(standardOmitted.GetRawData())).IsTrue();

        var microDefault = MicroQRCodeGenerator.CreateMicroQRCode("12345", MicroQREccLevel.L, default);
        var microOmitted = MicroQRCodeGenerator.CreateMicroQRCode("12345", MicroQREccLevel.L);
        await Assert.That(microDefault.Version).IsEqualTo(microOmitted.Version);
        await Assert.That(microDefault.GetRawData().AsSpan().SequenceEqual(microOmitted.GetRawData())).IsTrue();

        // the span spellings, which were ambiguous before the options overloads existed
        await Assert.That(QRCodeGenerator.CreateQrCode("hello world".AsSpan(), ECCLevel.M, default).Version).IsEqualTo(standardOmitted.Version);
        await Assert.That(MicroQRCodeGenerator.CreateMicroQRCode("12345".AsSpan(), MicroQREccLevel.L, default).Version).IsEqualTo(microOmitted.Version);
    }

    [Test]
    public async Task Options_NegativeQuietZone_ThrowsFromEveryEntryPoint()
    {
        var standard = new QRCodeGeneratorOptions { QuietZoneSize = -1 };
        await Assert.That(() => QRCodeGenerator.CreateQrCode(Digits, ECCLevel.M, standard)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => QRCodeGenerator.TryGetRequiredBufferSize(Digits.AsSpan(), ECCLevel.M, out _, standard)).Throws<ArgumentOutOfRangeException>();

        var micro = new MicroQRCodeGeneratorOptions { QuietZoneSize = -1 };
        await Assert.That(() => MicroQRCodeGenerator.CreateMicroQRCode(Digits, MicroQREccLevel.L, micro)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => MicroQRCodeGenerator.TryGetRequiredBufferSize(Digits.AsSpan(), MicroQREccLevel.L, out _, micro)).Throws<ArgumentOutOfRangeException>();
    }
}
