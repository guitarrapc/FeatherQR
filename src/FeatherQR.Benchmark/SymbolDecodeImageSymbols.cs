/// <summary>
/// Compares mixed-symbology scanning with Standard QR scanning and three separate symbology scans.
/// </summary>
/// <remarks>
/// The same pages cover a single symbol, mixed and Standard QR sheets, and no symbols.
/// Span destinations have room for every symbol and allocate only the decoded texts. The array row also allocates the result container.
/// </remarks>
public class SymbolDecodeImageSymbols
{
    private const int Width = SingleFinderDecodeImageSymbols.Width;
    private const int Height = SingleFinderDecodeImageSymbols.Height;
    private const int Room = 16;

    private static readonly string[] shapeKeys = ["qr-one", "mixed-12", "qr-page-12", "no-symbol"];

    private byte[] _luminance = default!;
    private readonly SymbolDecodeResult[] _room = new SymbolDecodeResult[Room];
    private readonly QRCodeDecodeResult[] _qrRoom = new QRCodeDecodeResult[Room];
    private readonly MicroQRCodeDecodeResult[] _microRoom = new MicroQRCodeDecodeResult[Room];
    private readonly RmQRCodeDecodeResult[] _rmqrRoom = new RmQRCodeDecodeResult[Room];

    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = default!;

    public static IEnumerable<string> Shapes() => shapeKeys;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _luminance = Shape switch
        {
            "qr-one" => SingleFinderDecodeImageSymbols.Page(texture: true, [StandardQRSymbol(0)], grid: false),
            "mixed-12" => SingleFinderDecodeImageSymbols.Page(texture: false, Enumerable.Range(0, 12).Select(MixedSymbol), grid: true),
            "qr-page-12" => SingleFinderDecodeImageSymbols.Page(texture: false, Enumerable.Range(0, 12).Select(StandardQRSymbol), grid: true),
            "no-symbol" => SingleFinderDecodeImageSymbols.Page(texture: true, [], grid: false),
            _ => throw new InvalidOperationException(Shape),
        };

        // A shape that read another number of symbols would go on being measured as another case
        var (every, standard) = Shape switch { "qr-one" => (1, 1), "mixed-12" => (12, 4), "qr-page-12" => (12, 12), _ => (0, 0) };
        if (EveryIntoRoom() != every || EveryIntoArray() != every || ThreeCalls() != every || StandardQR() != standard)
            throw new InvalidOperationException($"{Shape}: every symbology {EveryIntoRoom()}, three calls {ThreeCalls()}, Standard QR {StandardQR()}; expected {every}, {every}, {standard}");
    }

    [Benchmark]
    public int EveryIntoRoom() => SymbolDecoder.DecodeImageSymbols(_luminance, Width, Height, _room);

    [Benchmark]
    public int EveryIntoArray() => SymbolDecoder.DecodeImageSymbols(_luminance, Width, Height).Length;

    [Benchmark]
    public int ThreeCalls() =>
        QRCodeDecoder.DecodeImageSymbols(_luminance, Width, Height, _qrRoom)
        + MicroQRCodeDecoder.DecodeImageSymbols(_luminance, Width, Height, _microRoom)
        + RmQRCodeDecoder.DecodeImageSymbols(_luminance, Width, Height, _rmqrRoom);

    [Benchmark(Baseline = true)]
    public int StandardQR() => QRCodeDecoder.DecodeImageSymbols(_luminance, Width, Height, _qrRoom);

    private static bool[,] StandardQRSymbol(int index) => SingleFinderDecodeImageSymbols.Matrix(QRCodeGenerator.Create($"https://example.com/{index:00}", QREccLevel.M));

    private static bool[,] MixedSymbol(int index) => (index % 3) switch
    {
        0 => StandardQRSymbol(index),
        1 => SingleFinderDecodeImageSymbols.Matrix(MicroQRCodeGenerator.Create($"MICRO {index:00}", MicroQREccLevel.L)),
        _ => SingleFinderDecodeImageSymbols.Matrix(RmQRCodeGenerator.Create($"RMQR SHEET {index:00}", RmQREccLevel.M)),
    };
}
