namespace FeatherQR.Tests;

/// <summary>
/// An undefined <see cref="QREccLevel"/> fails the same way on every Standard QR entry point: exactly <see cref="ArgumentException"/>
/// (the type Standard QR froze, where Micro QR and rMQR report <see cref="ArgumentOutOfRangeException"/>), naming the
/// <c>eccLevel</c> parameter and the level, whatever path the options send the call down.
/// </summary>
/// <remarks>
/// Each option set below takes a different route to its first use of the level: the single-mode version scan, the mixed-mode
/// planner, the ECC boost, a pinned version, Kanji analysis, and the Structured Append planners.
/// </remarks>
public class EccLevelValidationTest
{
    private static readonly (string Name, string Text, QRCodeGeneratorOptions Options)[] Routes =
    [
        ("default", "hello world", default),
        ("numeric", "0123456789", default),
        ("optimal", "ORDER 20260915 item 42", new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Optimal }),
        ("boost", "hello world", new QRCodeGeneratorOptions { BoostEccLevel = true }),
        ("pinned", "hello world", new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(5) }),
        ("range", "hello world", new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(10) }),
        ("kanji", "日本語の漢字", new QRCodeGeneratorOptions { AllowKanji = true }),
        ("kanji-optimal", "日本語 2026 漢字", new QRCodeGeneratorOptions { AllowKanji = true, Segmentation = QRSegmentation.Optimal }),
    ];

    [Test]
    [Arguments(4)]
    [Arguments(-1)]
    [Arguments(99)]
    public async Task UndefinedLevel_ThrowsArgumentException_NamingTheParameter(int value)
    {
        var level = (QREccLevel)value;
        var destination = new byte[200 * 200];
        foreach (var (name, text, options) in Routes)
        {
            var calls = new (string Entry, Action Call)[]
            {
                ("TryGetRequiredBufferSize", () => QRCodeGenerator.TryGetRequiredBufferSize(text, level, out _, options)),
                ("Create", () => QRCodeGenerator.Create(text, level, options)),
                ("Create span", () => QRCodeGenerator.Create(text, level, destination, options)),
                ("CreateStructuredAppend", () => QRCodeGenerator.CreateStructuredAppend(text, level, options)),
            };
            foreach (var (entry, call) in calls)
            {
                var exception = await Assert.That(call).ThrowsExactly<ArgumentException>().Because($"{entry} over the {name} route");
                await Assert.That(exception!.ParamName).IsEqualTo("eccLevel").Because($"{entry} over the {name} route");
                await Assert.That(exception.Message).Contains($"ECC level {value}").Because($"{entry} over the {name} route");
            }
        }
    }

    [Test]
    public async Task UndefinedLevel_IsReportedAfterAnInvalidQuietZone()
    {
        // The quiet zone has always been checked first. The level's check keeps that order rather than overtaking it.
        var options = new QRCodeGeneratorOptions { QuietZoneSize = -1 };
        await Assert.That(() => QRCodeGenerator.TryGetRequiredBufferSize("1", (QREccLevel)9, out _, options)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => QRCodeGenerator.Create("1", (QREccLevel)9, options)).Throws<ArgumentOutOfRangeException>();
    }
}
