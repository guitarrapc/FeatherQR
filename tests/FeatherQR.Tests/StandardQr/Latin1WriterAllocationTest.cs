namespace FeatherQR.Tests;

/// <summary>
/// The Latin-1 writer allocates nothing once warm on a run past its stack budget of 256 chars, where it narrows into a pooled buffer.
/// ASCII Byte runs and auto-detected ISO-8859-1 take the same writer as a forced ISO-8859-1.
/// </summary>
/// <remarks>Release only, and run alone, as <see cref="DecodeAllocationTest"/>'s checks are.</remarks>
[NotInParallel]
public class Latin1WriterAllocationTest
{
    public static IEnumerable<Func<(string Name, string Text, QRCodeGeneratorOptions Options)>> Cases() =>
    [
        () => ("forced ISO-8859-1", new string('é', 300), new() { EciMode = EciMode.Iso8859_1 }),
        () => ("ISO-8859-1 chosen", new string('é', 300), default),
        () => ("ASCII", new string('a', 500), new() { Segmentation = QRSegmentation.Single }),
    ];

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task LongRun_AllocatesNothing(string name, string text, QRCodeGeneratorOptions options)
    {
        var size = Sizing.Required(text, QREccLevel.L, options);
        var matrix = new byte[size.BufferSize];
        QRCodeGenerator.Create(text, QREccLevel.L, matrix, options);
        await Assert.That(QRCodeDecoder.TryDecode(matrix, size.Size, out string decoded, out _) ? decoded : null).IsEqualTo(text).Because(name);

#if !DEBUG
        await Assert.That(DecodeAllocationTest.SteadyStateBytes(() => QRCodeGenerator.Create(text, QREccLevel.L, matrix, in options))).IsEqualTo(0)
            .Because($"{name}: bytes the encode allocates once warm");
#endif
    }
}
