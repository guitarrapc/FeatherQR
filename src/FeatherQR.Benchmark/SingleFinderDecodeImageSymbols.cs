/// <summary>
/// The every-symbol calls of Micro QR and rMQR, against their one-symbol call on the same image.
///
/// The every-symbol call does not stop at the first symbol, so its cost depends on what else the image holds.
/// The shapes are 1,280 x 960 pages, the symbols drawn crisp at 4 px a module.
///
///   one-symbol : one symbol on a smooth texture
///   sheet-12   : twelve symbols of the symbology in a 4 x 3 grid on a light page
///   no-symbol  : the same texture with no symbol
///   qr-page-12 : twelve Standard QR codes in a 4 x 3 grid, whose 36 finders the decoder takes for its own
///
///   IntoArray     : DecodeImageSymbols into a new array, whose room never fills
///   IntoRoomOfOne : DecodeImageSymbols into a span of one, which stops once a symbol fills it
///   OneSymbol     : TryDecodeImage into a char buffer, the baseline
///
/// The span forms allocate only the texts read.
/// </summary>
public class SingleFinderDecodeImageSymbols
{
    internal const int Width = 1280;
    internal const int Height = 960;
    private const int PixelsPerModule = 4;

    private static readonly string[] shapeKeys = ["one-symbol", "sheet-12", "no-symbol", "qr-page-12"];

    private byte[] _luminance = default!;
    private char[] _chars = default!;
    private readonly MicroQRCodeDecodeResult[] _microRoom = new MicroQRCodeDecodeResult[1];
    private readonly RmQRCodeDecodeResult[] _rmqrRoom = new RmQRCodeDecodeResult[1];
    private bool _micro;

    [Params("MicroQR", "RmQR")]
    public string Symbology { get; set; } = default!;

    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = default!;

    public static IEnumerable<string> Shapes() => shapeKeys;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _micro = Symbology == "MicroQR";
        _chars = new char[Math.Max(MicroQRCodeDecoder.GetMaxDecodedLength(MicroQRVersion.M4), RmQRCodeDecoder.GetMaxDecodedLength(RmQRVersion.R17x139))];
        _luminance = Shape switch
        {
            "one-symbol" => Page(texture: true, Enumerable.Repeat(SymbolOfSymbology(0), 1), grid: false),
            "sheet-12" => Page(texture: false, Enumerable.Range(0, 12).Select(SymbolOfSymbology), grid: true),
            "no-symbol" => Page(texture: true, [], grid: false),
            "qr-page-12" => Page(texture: false, Enumerable.Range(0, 12).Select(i => Matrix(QRCodeGenerator.Create($"https://example.com/{i:00}", QREccLevel.M))), grid: true),
            _ => throw new InvalidOperationException(Shape),
        };

        // A shape that read another number of symbols would go on being measured as another case
        var expected = Shape switch { "one-symbol" => 1, "sheet-12" => 12, _ => 0 };
        var read = IntoArray();
        if (read != expected)
            throw new InvalidOperationException($"{Symbology} {Shape} read {read} symbols, expected {expected}");
        if (OneSymbol() != expected > 0 || IntoRoomOfOne() != Math.Min(expected, 1))
            throw new InvalidOperationException($"{Symbology} {Shape}: the one-symbol call or the room of one disagrees with the array form");
    }

    [Benchmark]
    public int IntoArray() => _micro
        ? MicroQRCodeDecoder.DecodeImageSymbols(_luminance, Width, Height).Length
        : RmQRCodeDecoder.DecodeImageSymbols(_luminance, Width, Height).Length;

    [Benchmark]
    public int IntoRoomOfOne() => _micro
        ? MicroQRCodeDecoder.DecodeImageSymbols(_luminance, Width, Height, _microRoom)
        : RmQRCodeDecoder.DecodeImageSymbols(_luminance, Width, Height, _rmqrRoom);

    [Benchmark(Baseline = true)]
    public bool OneSymbol() => _micro
        ? MicroQRCodeDecoder.TryDecodeImage(_luminance, Width, Height, _chars, out _, out _)
        : RmQRCodeDecoder.TryDecodeImage(_luminance, Width, Height, _chars, out _, out _);

    private bool[,] SymbolOfSymbology(int index) => _micro
        ? Matrix(MicroQRCodeGenerator.Create($"MICRO {index:00}", MicroQREccLevel.L))
        : Matrix(RmQRCodeGenerator.Create($"RMQR SHEET {index:00}", RmQREccLevel.M));

    internal static bool[,] Matrix(MicroQRCodeData data) => Matrix(data.Size, data.Size, (r, c) => data[r, c]);

    internal static bool[,] Matrix(RmQRCodeData data) => Matrix(data.Height, data.Width, (r, c) => data[r, c]);

    internal static bool[,] Matrix(QRCodeData data) => Matrix(data.Size, data.Size, (r, c) => data[r, c]);

    private static bool[,] Matrix(int rows, int columns, Func<int, int, bool> dark)
    {
        var matrix = new bool[rows, columns];
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < columns; c++)
                matrix[r, c] = dark(r, c);
        }
        return matrix;
    }

    /// <summary>
    /// A page with each symbol drawn at <see cref="PixelsPerModule"/>, quiet zone included: one placed off centre, or each in its own cell of a 4 x 3 grid.
    /// </summary>
    internal static byte[] Page(bool texture, IEnumerable<bool[,]> symbols, bool grid)
    {
        var state = 20261029u;
        uint Next() => state = state * 1664525u + 1013904223u;
        var page = new byte[Width * Height];
        if (texture)
        {
            // Bilinear random cells of 24 px with a little noise: ridges and blobs, no symbol
            const int Cell = 24;
            const int Columns = Width / Cell + 2;
            var cells = new float[Columns * (Height / Cell + 2)];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = (Next() >> 8) / 16777216f;
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    int cx = x / Cell, cy = y / Cell;
                    float tx = x % Cell / (float)Cell, ty = y % Cell / (float)Cell;
                    var v = cells[cy * Columns + cx] * (1 - tx) * (1 - ty) + cells[cy * Columns + cx + 1] * tx * (1 - ty)
                        + cells[(cy + 1) * Columns + cx] * (1 - tx) * ty + cells[(cy + 1) * Columns + cx + 1] * tx * ty;
                    page[y * Width + x] = (byte)Math.Clamp(40 + v * 180 + (int)(Next() >> 28) - 8, 0, 255);
                }
            }
        }
        else
        {
            for (var i = 0; i < page.Length; i++)
                page[i] = (byte)(225 + (Next() >> 28));
        }

        var index = 0;
        foreach (var symbol in symbols)
        {
            int rows = symbol.GetLength(0), columns = symbol.GetLength(1);
            int left, top;
            if (grid)
            {
                left = index % 4 * (Width / 4) + (Width / 4 - columns * PixelsPerModule) / 2;
                top = index / 4 * (Height / 3) + (Height / 3 - rows * PixelsPerModule) / 2;
            }
            else
            {
                left = Width * 3 / 10;
                top = Height * 2 / 5;
            }
            if (left < 0 || top < 0 || left + columns * PixelsPerModule > Width || top + rows * PixelsPerModule > Height)
                throw new InvalidOperationException("a symbol does not fit its place on the page");
            for (var r = 0; r < rows * PixelsPerModule; r++)
            {
                for (var c = 0; c < columns * PixelsPerModule; c++)
                    page[(top + r) * Width + left + c] = symbol[r / PixelsPerModule, c / PixelsPerModule] ? (byte)25 : (byte)235;
            }
            index++;
        }
        return page;
    }
}
