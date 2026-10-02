using System.Text;
using FeatherQR;

/// <summary>
/// One case of the protocol made into a call. The input is read and converted here, before any timing, so the timed call does only QR work.
/// </summary>
/// <param name="call">The timed unit of work. Its value is folded into the checksum, so the work cannot be dropped.</param>
/// <param name="describe">Makes one call and returns its result as protocol JSON members: the status, then the decoded text or the encoded matrix.</param>
internal sealed class Operation(Func<ulong> call, Func<string> describe)
{
    public Func<ulong> Call { get; } = call;
    public Func<string> Describe { get; } = describe;
}

/// <summary>
/// The calls FeatherQR makes for each operation and symbology. Shared by the CLI and the BenchmarkDotNet reference project, so both time the same code.
/// </summary>
/// <remarks>
/// The calls are the ones the benchmark project's <c>Simple*</c> classes make: the data-object generators, and the string-returning decoders over a bare module matrix or 8-bit grey pixels.
/// </remarks>
internal static class Operations
{
    private const string Failed = "\"status\":\"failed\"";

    public static Operation Load(string op, string symbology, string input, string? ecc, string? version) => (op, symbology) switch
    {
        ("encode", "qr") => EncodeQR(ReadText(input), Enum.Parse<QREccLevel>(Required(ecc, "--ecc")), int.Parse(Required(version, "--version"))),
        ("encode", "microqr") => EncodeMicroQR(ReadText(input), Enum.Parse<MicroQREccLevel>(Required(ecc, "--ecc")), Enum.Parse<MicroQRVersion>(Required(version, "--version"))),
        ("encode", "rmqr") => EncodeRmQR(ReadText(input), Enum.Parse<RmQREccLevel>(Required(ecc, "--ecc")), Enum.Parse<RmQRVersion>(Required(version, "--version"))),
        ("decode-matrix", "qr") => DecodeMatrixQR(ReadMatrix(input, square: true)),
        ("decode-matrix", "microqr") => DecodeMatrixMicroQR(ReadMatrix(input, square: true)),
        ("decode-matrix", "rmqr") => DecodeMatrixRmQR(ReadMatrix(input, square: false)),
        ("decode-image", "qr") => DecodeImageQR(Pgm.Read(input)),
        ("decode-image", "microqr") => DecodeImageMicroQR(Pgm.Read(input)),
        ("decode-image", "rmqr") => DecodeImageRmQR(Pgm.Read(input)),
        _ => throw new NotSupportedException($"{op} {symbology}"),
    };

    private static Operation EncodeQR(string text, QREccLevel level, int version)
    {
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version) };
        return new(
            () => Fold(QRCodeGenerator.Create(text.AsSpan(), level, options)),
            () => Encoded(() =>
            {
                var data = QRCodeGenerator.Create(text.AsSpan(), level, options);
                return Matrix(data.Size, data.Size, (row, col) => data[row, col]);
            }));
    }

    private static Operation EncodeMicroQR(string text, MicroQREccLevel level, MicroQRVersion version)
    {
        var options = new MicroQRCodeGeneratorOptions { Version = version };
        return new(
            () => Fold(MicroQRCodeGenerator.Create(text.AsSpan(), level, options)),
            () => Encoded(() =>
            {
                var data = MicroQRCodeGenerator.Create(text.AsSpan(), level, options);
                return Matrix(data.Size, data.Size, (row, col) => data[row, col]);
            }));
    }

    private static Operation EncodeRmQR(string text, RmQREccLevel level, RmQRVersion version)
    {
        var options = new RmQRCodeGeneratorOptions { Version = version };
        return new(
            () => Fold(RmQRCodeGenerator.Create(text.AsSpan(), level, options)),
            () => Encoded(() =>
            {
                var data = RmQRCodeGenerator.Create(text.AsSpan(), level, options);
                return Matrix(data.Width, data.Height, (row, col) => data[row, col]);
            }));
    }

    private static Operation DecodeMatrixQR((byte[] Modules, int Width, int Height) m) => new(
        () =>
        {
            QRCodeDecoder.TryDecode(m.Modules, m.Width, out var text, out _);
            return Fold(text);
        },
        () => QRCodeDecoder.TryDecode(m.Modules, m.Width, out var text, out _) ? Decoded(text) : Failed);

    private static Operation DecodeMatrixMicroQR((byte[] Modules, int Width, int Height) m) => new(
        () =>
        {
            MicroQRCodeDecoder.TryDecode(m.Modules, m.Width, out var text, out _);
            return Fold(text);
        },
        () => MicroQRCodeDecoder.TryDecode(m.Modules, m.Width, out var text, out _) ? Decoded(text) : Failed);

    private static Operation DecodeMatrixRmQR((byte[] Modules, int Width, int Height) m) => new(
        () =>
        {
            RmQRCodeDecoder.TryDecode(m.Modules, m.Width, m.Height, out var text, out _);
            return Fold(text);
        },
        () => RmQRCodeDecoder.TryDecode(m.Modules, m.Width, m.Height, out var text, out _) ? Decoded(text) : Failed);

    private static Operation DecodeImageQR((byte[] Pixels, int Width, int Height) image) => new(
        () =>
        {
            QRCodeDecoder.TryDecodeImage(image.Pixels, image.Width, image.Height, out var text, out _);
            return Fold(text);
        },
        () => QRCodeDecoder.TryDecodeImage(image.Pixels, image.Width, image.Height, out var text, out _) ? Decoded(text) : Failed);

    private static Operation DecodeImageMicroQR((byte[] Pixels, int Width, int Height) image) => new(
        () =>
        {
            MicroQRCodeDecoder.TryDecodeImage(image.Pixels, image.Width, image.Height, out var text, out _);
            return Fold(text);
        },
        () => MicroQRCodeDecoder.TryDecodeImage(image.Pixels, image.Width, image.Height, out var text, out _) ? Decoded(text) : Failed);

    private static Operation DecodeImageRmQR((byte[] Pixels, int Width, int Height) image) => new(
        () =>
        {
            RmQRCodeDecoder.TryDecodeImage(image.Pixels, image.Width, image.Height, out var text, out _);
            return Fold(text);
        },
        () => RmQRCodeDecoder.TryDecodeImage(image.Pixels, image.Width, image.Height, out var text, out _) ? Decoded(text) : Failed);

    // The folds read the result's content, not only its presence.
    private static ulong Fold(string? text) => text is { Length: > 0 } ? (ulong)text.Length * 31 + text[^1] : 0;
    private static ulong Fold(QRCodeData data) => (ulong)data.Size * 2 + (data[data.Size / 2, data.Size / 2] ? 1UL : 0);
    private static ulong Fold(MicroQRCodeData data) => (ulong)data.Size * 2 + (data[data.Size / 2, data.Size / 2] ? 1UL : 0);
    private static ulong Fold(RmQRCodeData data) => (ulong)data.Width * 2 + (data[data.Height / 2, data.Width / 2] ? 1UL : 0);

    private static string Decoded(string text) => $"\"status\":\"ok\",\"text\":\"{Convert.ToHexString(Encoding.UTF8.GetBytes(text))}\"";

    private static string Encoded(Func<string> matrix)
    {
        try
        {
            return matrix();
        }
        catch (ArgumentException)
        {
            // A generator refuses content it cannot hold with an argument exception.
            return Failed;
        }
    }

    /// <summary>Rows top to bottom, 1 for a dark module, with whatever quiet zone the library returns.</summary>
    private static string Matrix(int width, int height, Func<int, int, bool> isDark)
    {
        var json = new StringBuilder($"\"status\":\"ok\",\"matrix\":{{\"width\":{width},\"height\":{height},\"rows\":[");
        for (var row = 0; row < height; row++)
        {
            json.Append(row == 0 ? "\"" : ",\"");
            for (var col = 0; col < width; col++)
                json.Append(isDark(row, col) ? '1' : '0');
            json.Append('"');
        }
        return json.Append("]}").ToString();
    }

    // The payload's exact bytes: a byte order mark would stay in the text rather than be dropped.
    private static string ReadText(string path) => Encoding.UTF8.GetString(File.ReadAllBytes(path));

    /// <summary>A bare symbol, one pixel per module: 0 (dark) becomes module 1.</summary>
    private static (byte[] Modules, int Width, int Height) ReadMatrix(string path, bool square)
    {
        var (pixels, width, height) = Pgm.Read(path);
        if (square && width != height)
            throw new InvalidDataException($"{path} is {width}x{height}; this symbology is square.");
        var modules = new byte[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
            modules[i] = pixels[i] < 128 ? (byte)1 : (byte)0;
        return (modules, width, height);
    }

    private static string Required(string? value, string option) => value ?? throw new ArgumentException($"encode needs {option}.");
}
