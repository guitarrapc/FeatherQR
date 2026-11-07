namespace FeatherQR.Tests;

/// <summary>
/// What the image options and the colour read when nothing is set.
/// <see cref="GeneratorOptionsAssignmentTest"/> covers the routes that set them.
/// </summary>
public class SymbolImageOptionsTest
{
    [Test]
    public async Task Default_IsEightPixelsAModule_OpaqueBlackOnOpaqueWhite()
    {
        var options = default(SymbolImageOptions);

        await Assert.That(options.ModulePixelSize).IsEqualTo(8);
        await Assert.That(options.DarkColor).IsEqualTo(new SymbolColor(0, 0, 0, 255));
        await Assert.That(options.LightColor).IsEqualTo(new SymbolColor(255, 255, 255, 255));
        await Assert.That(SymbolImageOptions.Default).IsEqualTo(options);
        await Assert.That(new SymbolImageOptions()).IsEqualTo(options);
    }

    [Test]
    public async Task Color_IsOpaqueUnlessGivenAnAlpha_AndItsDefaultIsTransparentBlack()
    {
        await Assert.That(new SymbolColor(1, 2, 3).A).IsEqualTo((byte)255);
        await Assert.That(new SymbolColor(1, 2, 3, 4).A).IsEqualTo((byte)4);
        await Assert.That(default(SymbolColor)).IsEqualTo(new SymbolColor(0, 0, 0, 0));
        await Assert.That(new SymbolColor(1, 2, 3, 4).ToString()).IsEqualTo("SymbolColor { R = 1, G = 2, B = 3, A = 4 }");
    }

    /// <summary>
    /// A transparent black is a colour of its own, not the unset default, so it reads back as given.
    /// </summary>
    [Test]
    public async Task TransparentBlack_ReadsBackAsGiven()
    {
        var options = new SymbolImageOptions { DarkColor = default, LightColor = default };

        await Assert.That(options.DarkColor).IsEqualTo(default(SymbolColor));
        await Assert.That(options.LightColor).IsEqualTo(default(SymbolColor));
        await Assert.That(options).IsNotEqualTo(default(SymbolImageOptions));
    }
}
