using SkiaSharp;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

public class IconDataModuleSizingTest
{
    private const string TestContent = "HELLO-ICON";

    [Test]
    public async Task FromImageByModules_ValidArgs_SetsModuleProperties()
    {
        using var logo = CreateLogo(32);
        var icon = IconData.FromImageByModules(logo, iconSizeModules: 5, iconBorderModules: 1, maxCoreOccupancyPercent: 40);

        await Assert.That(icon.IconSizeModules).IsEqualTo(5);
        await Assert.That(icon.IconBorderModules).IsEqualTo(1);
        await Assert.That(icon.MaxCoreOccupancyPercent).IsEqualTo(40);
    }

    [Test]
    [Arguments(0, 1, 30)]
    [Arguments(-1, 1, 30)]
    [Arguments(5, -1, 30)]
    [Arguments(5, 1, 0)]
    [Arguments(5, 1, 101)]
    public void FromImageByModules_InvalidArgs_ThrowsArgumentOutOfRangeException(
        int iconSizeModules,
        int iconBorderModules,
        int maxCoreOccupancyPercent)
    {
        using var logo = CreateLogo(16);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            IconData.FromImageByModules(logo, iconSizeModules, iconBorderModules, maxCoreOccupancyPercent));
    }

    [Test]
    public async Task GetIconRects_ModuleSizing_WithIntegerModulePixels_AlignsToModules()
    {
        const int modulePixelSize = 10;
        const int iconSizeModules = 5;
        const int iconBorderModules = 1;

        var qrData = QRCodeGenerator.CreateQrCode(TestContent, ECCLevel.H, new QRCodeGeneratorOptions { Version = 1, QuietZoneSize = 4 });
        var imageSide = qrData.Size * modulePixelSize;
        var area = SKRect.Create(0, 0, imageSide, imageSide);

        using var logo = CreateLogo(32);
        var icon = IconData.FromImageByModules(logo, iconSizeModules, iconBorderModules, maxCoreOccupancyPercent: 40);

        var (iconRect, borderRect) = QRCodeRenderer.GetIconRects(qrData, area, icon);

        await Assert.That(iconRect.Width).IsEqualTo(iconSizeModules * modulePixelSize);
        await Assert.That(iconRect.Height).IsEqualTo(iconSizeModules * modulePixelSize);
        await Assert.That(borderRect.Width).IsEqualTo((iconSizeModules + iconBorderModules * 2) * modulePixelSize);
        await Assert.That(borderRect.Height).IsEqualTo((iconSizeModules + iconBorderModules * 2) * modulePixelSize);
        await Assert.That(iconRect.MidX).IsEqualTo(area.MidX).Within(0.001f);
        await Assert.That(iconRect.MidY).IsEqualTo(area.MidY).Within(0.001f);
        await Assert.That(iconRect.Left % modulePixelSize).IsEqualTo(0).Within(0.001f);
        await Assert.That(iconRect.Top % modulePixelSize).IsEqualTo(0).Within(0.001f);
        await Assert.That(borderRect.Left % modulePixelSize).IsEqualTo(0).Within(0.001f);
        await Assert.That(borderRect.Top % modulePixelSize).IsEqualTo(0).Within(0.001f);
    }

    [Test]
    public async Task GetIconRects_ModuleSizing_EvenIconSize_SnapsToModuleGrid()
    {
        const int modulePixelSize = 10;
        const int iconSizeModules = 6; // even: cannot be geometrically centered on odd matrix
        const int iconBorderModules = 1;

        var qrData = QRCodeGenerator.CreateQrCode(TestContent, ECCLevel.H, new QRCodeGeneratorOptions { Version = 5, QuietZoneSize = 4 });
        var imageSide = qrData.Size * modulePixelSize;
        var area = SKRect.Create(0, 0, imageSide, imageSide);

        using var logo = CreateLogo(32);
        var icon = IconData.FromImageByModules(logo, iconSizeModules, iconBorderModules, maxCoreOccupancyPercent: 40);

        var (iconRect, borderRect) = QRCodeRenderer.GetIconRects(qrData, area, icon);

        var expectedOrigin = ((qrData.Size - iconSizeModules) / 2) * modulePixelSize;
        await Assert.That(iconRect.Left).IsEqualTo(expectedOrigin);
        await Assert.That(iconRect.Top).IsEqualTo(expectedOrigin);
        await Assert.That(iconRect.Width).IsEqualTo(iconSizeModules * modulePixelSize);
        await Assert.That(borderRect.Width).IsEqualTo((iconSizeModules + iconBorderModules * 2) * modulePixelSize);
        await Assert.That(iconRect.Left % modulePixelSize).IsEqualTo(0).Within(0.001f);
        await Assert.That(borderRect.Left % modulePixelSize).IsEqualTo(0).Within(0.001f);
    }

    [Test]
    [Arguments(0)]
    [Arguments(101)]
    public void GetIconRects_PercentSizing_InvalidPercent_ThrowsArgumentOutOfRangeException(int iconSizePercent)
    {
        var qrData = QRCodeGenerator.CreateQrCode(TestContent, ECCLevel.M, new QRCodeGeneratorOptions { Version = 1, QuietZoneSize = 4 });
        var area = SKRect.Create(0, 0, 500, 500);
        using var logo = CreateLogo(16);
        var icon = IconData.FromImage(logo, iconSizePercent: 10, iconBorderWidth: 2);
        icon.IconSizePercent = iconSizePercent;

        Assert.Throws<ArgumentOutOfRangeException>(() => QRCodeRenderer.GetIconRects(qrData, area, icon));
    }

    [Test]
    public void GetIconRects_PercentSizing_NegativeBorder_ThrowsArgumentOutOfRangeException()
    {
        var qrData = QRCodeGenerator.CreateQrCode(TestContent, ECCLevel.M, new QRCodeGeneratorOptions { Version = 1, QuietZoneSize = 4 });
        var area = SKRect.Create(0, 0, 500, 500);
        using var logo = CreateLogo(16);
        var icon = IconData.FromImage(logo, iconSizePercent: 10, iconBorderWidth: 2);
        icon.IconBorderWidth = -1;

        Assert.Throws<ArgumentOutOfRangeException>(() => QRCodeRenderer.GetIconRects(qrData, area, icon));
    }

    [Test]
    public async Task GetIconRects_ModuleSizing_IgnoresPercentAndPixelBorder()
    {
        const int modulePixelSize = 8;
        var qrData = QRCodeGenerator.CreateQrCode(TestContent, ECCLevel.H, new QRCodeGeneratorOptions { Version = 5, QuietZoneSize = 4 });
        var imageSide = qrData.Size * modulePixelSize;
        var area = SKRect.Create(0, 0, imageSide, imageSide);

        using var logo = CreateLogo(32);
        var icon = IconData.FromImageByModules(logo, iconSizeModules: 7, iconBorderModules: 1);
        icon.IconSizePercent = 50;
        icon.IconBorderWidth = 99;

        var (iconRect, borderRect) = QRCodeRenderer.GetIconRects(qrData, area, icon);

        await Assert.That(iconRect.Width).IsEqualTo(7 * modulePixelSize);
        await Assert.That(borderRect.Width).IsEqualTo(9 * modulePixelSize);
    }

    [Test]
    public async Task GetIconRects_ModuleSizing_NullBorderModules_DefaultsToOne()
    {
        const int modulePixelSize = 10;
        var qrData = QRCodeGenerator.CreateQrCode(TestContent, ECCLevel.H, new QRCodeGeneratorOptions { Version = 5, QuietZoneSize = 4 });
        var imageSide = qrData.Size * modulePixelSize;
        var area = SKRect.Create(0, 0, imageSide, imageSide);

        using var logo = CreateLogo(16);
        var icon = new IconData
        {
            Icon = new ImageIconShape(logo),
            IconSizeModules = 5,
            IconBorderModules = null,
        };

        var (iconRect, borderRect) = QRCodeRenderer.GetIconRects(qrData, area, icon);

        await Assert.That(iconRect.Width).IsEqualTo(50);
        await Assert.That(borderRect.Width).IsEqualTo(70);
    }

    [Test]
    public async Task GetIconRects_ModuleSizing_ExceedsMatrixSize_ThrowsInvalidOperationException()
    {
        var qrData = QRCodeGenerator.CreateQrCode(TestContent, ECCLevel.H, new QRCodeGeneratorOptions { Version = 1, QuietZoneSize = 0 });
        // Version 1 core/matrix size = 21
        var area = SKRect.Create(0, 0, 210, 210);

        using var logo = CreateLogo(16);
        var icon = IconData.FromImageByModules(logo, iconSizeModules: 21, iconBorderModules: 1, maxCoreOccupancyPercent: 100);

        var ex = Assert.Throws<InvalidOperationException>(() => QRCodeRenderer.GetIconRects(qrData, area, icon));
        await Assert.That(ex.Message).Contains("exceeds QR matrix size");
    }

    [Test]
    public async Task GetIconRects_ModuleSizing_ExceedsCoreOccupancy_ThrowsInvalidOperationException()
    {
        var qrData = QRCodeGenerator.CreateQrCode(TestContent, ECCLevel.H, new QRCodeGeneratorOptions { Version = 1, QuietZoneSize = 4 });
        // coreSize=21, 30% => max 6 modules
        var area = SKRect.Create(0, 0, qrData.Size * 10, qrData.Size * 10);

        using var logo = CreateLogo(16);
        var icon = IconData.FromImageByModules(logo, iconSizeModules: 5, iconBorderModules: 1); // total 7 > 6

        var ex = Assert.Throws<InvalidOperationException>(() => QRCodeRenderer.GetIconRects(qrData, area, icon));
        await Assert.That(ex.Message).Contains("max allowed is");
        await Assert.That(ex.Message).Contains("maxCoreOccupancyPercent=30");
    }

    [Test]
    public async Task Builder_WithModulePixelSize_AndModuleIcon_RendersSuccessfully()
    {
        using var logo = CreateLogo(64);
        var icon = IconData.FromImageByModules(logo, iconSizeModules: 5, iconBorderModules: 0);

        using var bitmap = new QRCodeImageBuilder(TestContent)
            .WithErrorCorrection(ECCLevel.H)
            .WithVersion(1)
            .WithQuietZone(4)
            .WithModulePixelSize(10)
            .WithIcon(icon)
            .ToBitmap();

        await Assert.That(bitmap.Width).IsEqualTo(290); // 29 modules * 10px
        await Assert.That(bitmap.Height).IsEqualTo(290);
    }

    [Test]
    public async Task GetIconRects_PercentSizing_StillWorks()
    {
        var qrData = QRCodeGenerator.CreateQrCode(TestContent, ECCLevel.M, new QRCodeGeneratorOptions { Version = 1, QuietZoneSize = 4 });
        var area = SKRect.Create(0, 0, 500, 500);

        using var logo = CreateLogo(16);
        var icon = IconData.FromImage(logo, iconSizePercent: 20, iconBorderWidth: 10);

        var (iconRect, borderRect) = QRCodeRenderer.GetIconRects(qrData, area, icon);

        await Assert.That(iconRect.Width).IsEqualTo(100);
        await Assert.That(iconRect.Height).IsEqualTo(100);
        await Assert.That(borderRect.Width).IsEqualTo(120);
        await Assert.That(borderRect.Height).IsEqualTo(120);
    }

    private static SKBitmap CreateLogo(int size)
    {
        var bitmap = new SKBitmap(size, size);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Red);
        return bitmap;
    }
}
