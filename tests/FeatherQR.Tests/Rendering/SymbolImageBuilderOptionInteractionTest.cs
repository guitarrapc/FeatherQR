using SkiaSharp;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// A setter sets the option it names and leaves every other option alone.
/// The setters that change no pixels themselves, the layout and encoding ones, are where a
/// cross-assignment hides: the option that was set earlier simply stops reaching the output and
/// nothing about the call says so.
/// </summary>
public class SymbolImageBuilderOptionInteractionTest
{
    private const string Content = "option-interaction-test";

    // Large enough on both axes that the symbol is padded rather than filling the canvas, and not
    // square, so the clear color has somewhere to show.
    private const int CanvasWidth = 260;
    private const int CanvasHeight = 220;

    /// <summary>
    /// The options under test, by the field each one writes. Every one of them is visible in the
    /// rendered image on its own, which is what makes "it stopped being visible" an assertion.
    /// </summary>
    public static IEnumerable<string> Options()
    {
        yield return "CodeColor";
        yield return "BackgroundColor";
        yield return "ClearColor";
        yield return "ModuleShape";
        yield return "ModuleSizePercent";
        yield return "FinderPatternShape";
        yield return "Gradient";
        yield return "QuietZone";
        yield return "ModulePixelSize";
        yield return "Size";
    }

    /// <summary>
    /// The setters that are called after an option and have no styling of their own. These are the
    /// four the cross-assignment sweep found the survivors concentrated in.
    /// </summary>
    public static IEnumerable<string> LaterSetters()
    {
        yield return "QuietZone";
        yield return "ModulePixelSize";
        yield return "Size";
        yield return "Format";
    }

    public static IEnumerable<(string Option, string Later)> OptionAndLaterSetter()
    {
        foreach (var option in Options())
        {
            foreach (var later in LaterSetters())
            {
                // The same field twice is the one case where overwriting is the contract.
                if (option == later) continue;
                yield return (option, later);
            }
        }
    }

    /// <summary>
    /// An option set before a layout or encoding setter still reaches the image.
    /// Assigning the option's field from the later setter makes the two renders identical, which
    /// is the mutation this kills.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(OptionAndLaterSetter))]
    public async Task AnOptionSurvivesTheSetterCalledAfterIt(string option, string later)
    {
        using var withoutOption = Render(builder => Apply(builder, later));
        using var withOption = Render(builder => Apply(Apply(builder, option), later));

        await Assert.That(SamePixels(withoutOption, withOption)).IsFalse();
    }

    /// <summary>
    /// And the order the two were called in does not change the image, which is the reading the
    /// fluent chain invites.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(OptionAndLaterSetter))]
    public async Task TheTwoSettersCommute(string option, string later)
    {
        using var optionFirst = Render(builder => Apply(Apply(builder, option), later));
        using var laterFirst = Render(builder => Apply(Apply(builder, later), option));

        await Assert.That(SamePixels(optionFirst, laterFirst)).IsTrue();
    }

    /// <summary>
    /// The other direction: the format and the quality survive every styling setter called after
    /// them. <c>WithFormat</c> is the one setter whose two arguments reach nothing but the encoder,
    /// so a pixel comparison cannot see it at all.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Options))]
    public async Task TheFormatAndQualitySurviveAStylingSetterCalledAfterThem(string option)
    {
        var lowQuality = Encode(builder => Apply(builder.WithFormat(SKEncodedImageFormat.Jpeg, 5), option));
        var highQuality = Encode(builder => Apply(builder.WithFormat(SKEncodedImageFormat.Jpeg, 95), option));

        await Assert.That(IsJpeg(lowQuality)).IsTrue();
        await Assert.That(IsJpeg(highQuality)).IsTrue();
        await Assert.That(lowQuality.Length).IsLessThan(highQuality.Length);
    }

    /// <summary>
    /// <c>WithFormat</c> itself: both arguments reach the encoded bytes. Without this the only
    /// assertions on it are that the builder comes back and the output is not empty, which an
    /// implementation that ignored both arguments would satisfy.
    /// </summary>
    [Test]
    [Arguments(SKEncodedImageFormat.Png, (byte)0x89, (byte)0x50)]
    [Arguments(SKEncodedImageFormat.Jpeg, (byte)0xFF, (byte)0xD8)]
    [Arguments(SKEncodedImageFormat.Webp, (byte)0x52, (byte)0x49)]
    public async Task WithFormat_EncodesTheFormatItNames(SKEncodedImageFormat format, byte first, byte second)
    {
        var bytes = Encode(builder => builder.WithFormat(format));

        await Assert.That(bytes.Length).IsGreaterThan(2);
        await Assert.That(bytes[0]).IsEqualTo(first);
        await Assert.That(bytes[1]).IsEqualTo(second);
    }

    [Test]
    public async Task WithFormat_QualityChangesTheEncodedSize()
    {
        var low = Encode(builder => builder.WithFormat(SKEncodedImageFormat.Jpeg, 5));
        var high = Encode(builder => builder.WithFormat(SKEncodedImageFormat.Jpeg, 95));

        await Assert.That(IsJpeg(low)).IsTrue();
        await Assert.That(IsJpeg(high)).IsTrue();
        await Assert.That(low.Length).IsLessThan(high.Length);
    }

    /// <summary>
    /// The premise of the sweep above: every option in the table is visible on its own. A case that
    /// passed <see cref="AnOptionSurvivesTheSetterCalledAfterIt"/> because the option draws nothing
    /// would be worthless, and this is what says it does not.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Options))]
    public async Task EveryOptionInTheTableChangesTheImageOnItsOwn(string option)
    {
        using var plain = Render(builder => builder);
        using var styled = Render(builder => Apply(builder, option));

        await Assert.That(SamePixels(plain, styled)).IsFalse();
    }

    /// <summary>
    /// Applies one named option. The quiet zone only reaches a builder constructed from content,
    /// since a ready-made symbol carries its own, so every builder here is content-constructed.
    /// </summary>
    private static QRCodeImageBuilder Apply(QRCodeImageBuilder builder, string option) => option switch
    {
        "CodeColor" => builder.WithCodeColor(SKColors.Teal),
        "BackgroundColor" => builder.WithBackgroundColor(SKColors.Gold),
        "ClearColor" => builder.WithClearColor(SKColors.Lime),
        "ModuleShape" => builder.WithModuleShape(CircleModuleShape.Default),
        "ModuleSizePercent" => builder.WithModuleShape(RectangleModuleShape.Default, 0.7f),
        "FinderPatternShape" => builder.WithFinderPatternShape(CircleFinderPatternShape.Default),
        "Gradient" => builder.WithGradient(new GradientOptions([SKColors.Blue, SKColors.Purple], GradientDirection.TopLeftToBottomRight)),
        "QuietZone" => builder.WithQuietZone(1),
        "ModulePixelSize" => builder.WithModulePixelSize(4),
        "Size" => builder.WithSize(CanvasWidth - 80, CanvasHeight),
        "Format" => builder.WithFormat(SKEncodedImageFormat.Jpeg, 60),
        _ => throw new ArgumentOutOfRangeException(nameof(option), option, "Unknown option"),
    };

    /// <summary>
    /// The chain every case starts from. The clear color is part of it because the quiet zone is
    /// drawn in the background color and so is the padding: on a canvas larger than the content the
    /// two are the same white, and a symbol with a one-module quiet zone is then pixel-identical to
    /// the same symbol with four. Coloring the padding is what makes the quiet zone visible at all.
    /// </summary>
    private static QRCodeImageBuilder NewBuilder()
        => new QRCodeImageBuilder(Content).WithSize(CanvasWidth, CanvasHeight).WithClearColor(SKColors.Red);

    private static SKBitmap Render(Func<QRCodeImageBuilder, QRCodeImageBuilder> configure)
        => configure(NewBuilder()).ToBitmap();

    private static byte[] Encode(Func<QRCodeImageBuilder, QRCodeImageBuilder> configure)
    {
        using var stream = new MemoryStream();
        configure(NewBuilder()).SaveTo(stream);
        return stream.ToArray();
    }

    private static bool IsJpeg(byte[] bytes) => bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8;

    /// <summary>
    /// Ordered pixel comparison: two images made of the same pixels in a different arrangement are
    /// not the same image.
    /// </summary>
    private static bool SamePixels(SKBitmap left, SKBitmap right)
        => left.Width == right.Width
        && left.Height == right.Height
        && left.Bytes.AsSpan().SequenceEqual(right.Bytes.AsSpan());
}
