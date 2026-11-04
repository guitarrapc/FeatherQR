using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FeatherQR.Gs1;
using FeatherQR.SkiaSharp;
using SkiaSharp;

namespace FeatherQR.Playground;

/// <summary>
/// Browser-callable QR generation API.
/// Invoked by the host script after <c>runMain()</c> completes.
/// <para>
/// CRITICAL: Every <c>[JSExport]</c> method MUST catch all exceptions internally.
/// An unhandled exception propagating through the interop boundary causes the Mono WASM runtime to abort (exit code 1).
/// Once aborted, the runtime cannot be restarted without a full page reload, and all subsequent calls fail with "Assert failed: .NET runtime already exited with 1".
/// </para>
/// </summary>
public static partial class QrInterop
{
    private static SKBitmap? s_defaultLogo;
    private static byte[]? s_customLogoBytes;
    private static SKBitmap? s_customLogoBitmap;
    private static string s_lastMeta = "{}";

    /// <summary>User-facing build version. Exposed to the page script after WASM starts.</summary>
    [JSExport]
    public static string GetProductVersion()
    {
        try
        {
            var assembly = typeof(QrInterop).Assembly;
            var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(info))
                return assembly.GetName().Version?.ToString(3) ?? "unknown";

            // Strip SourceLink metadata (e.g. "1.2.3+abcdef0")
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Stats of the most recent successful <see cref="Generate"/> call as a JSON string (see <see cref="GenerateMeta"/>): <c>{"symbology":"qr","qrVersion":N,"matrixSize":N,"totalMs":N,"bytes":N}</c>, with <c>"encoded"</c> holding the element string or Digital Link URI written for GS1 data.
    /// </summary>
    [JSExport]
    public static string GetLastMeta() => s_lastMeta;

    /// <summary>
    /// Generates a QR code PNG from JSON options (see <see cref="QrRequest"/>).
    /// Returns PNG bytes on success (first byte 0x89), or UTF-8 JSON <c>{"error":"..."}</c> on failure (first byte 0x7B), the JS side distinguishes the two by the first byte.
    /// </summary>
    /// <param name="optionsJson">Serialized <see cref="QrRequest"/> (camelCase).</param>
    /// <param name="customLogo">Uploaded logo image bytes, empty unless logo mode is "custom".</param>
    [JSExport]
    public static byte[] Generate(string optionsJson, byte[] customLogo)
    {
        try
        {
            var request = JsonSerializer.Deserialize(optionsJson, PlaygroundJsonContext.Default.QrRequest)
                ?? throw new InvalidOperationException("Options JSON deserialized to null.");
            return GenerateCore(request, customLogo);
        }
        catch (Exception ex)
        {
            return SerializeError(ex);
        }
    }

    /// <summary>
    /// Generates a QR code SVG document from JSON options (see <see cref="QrRequest"/>).
    /// Returns UTF-8 SVG bytes on success (first byte '&lt;' 0x3C), or UTF-8 JSON <c>{"error":"..."}</c> on failure (first byte 0x7B), the JS side distinguishes the two by the first byte.
    /// </summary>
    /// <param name="optionsJson">Serialized <see cref="QrRequest"/> (camelCase).</param>
    /// <param name="customLogo">Uploaded logo image bytes, empty unless logo mode is "custom".</param>
    [JSExport]
    public static byte[] GenerateSvg(string optionsJson, byte[] customLogo)
    {
        try
        {
            var request = JsonSerializer.Deserialize(optionsJson, PlaygroundJsonContext.Default.QrRequest)
                ?? throw new InvalidOperationException("Options JSON deserialized to null.");
            if (string.IsNullOrWhiteSpace(request.Content))
                throw new ArgumentException("Content is empty.");

            using var stream = new MemoryStream();
            if (IsRmQR(request))
            {
                var rmData = CreateRmData(request);
                CreateRmBuilder(request, rmData).SaveToSvg(stream);
            }
            else if (IsMicroQR(request))
            {
                var microData = CreateMicroData(request);
                CreateMicroBuilder(request, microData).SaveToSvg(stream);
            }
            else
            {
                var data = CreateStandardData(request);
                CreateBuilder(request, data, customLogo).SaveToSvg(stream);
            }
            return stream.ToArray();
        }
        catch (Exception ex)
        {
            return SerializeError(ex);
        }
    }

    /// <summary>
    /// Decodes an encoded image and returns the result as JSON.
    /// </summary>
    /// <remarks>
    /// Success contains text, symbology, decode diagnostics, timing and image geometry. No match returns <c>ok:false</c> and status; unexpected failure returns <c>error</c>.
    /// Corners are ordered top-left, top-right, bottom-right, bottom-left, as x/y pairs in stored-image pixels.
    /// The page must apply the reported EXIF orientation before drawing them over the browser-oriented image.
    /// Standard QR, Micro QR and rMQR are tried in that order. rMQR reports maskPattern as -1.
    /// FNC1 results include their mode and second-position application indicator. Separators remain U+001D in text.
    /// GS1 interpretation adds HRI and elements, plus validation errors when applicable. See the payload types for JSON fields.
    /// Each symbol also reports its bytes and ECI assignment. Content that is not text the library can convert, such as binary data under ECI 899, succeeds with bytes and no text.
    /// </remarks>
    /// <param name="imageBytes">Encoded image file bytes.</param>
    [JSExport]
    public static string Decode(byte[] imageBytes)
    {
        try
        {
            if (imageBytes.Length == 0)
                throw new ArgumentException("Image is empty.");

            var stopwatch = Stopwatch.StartNew();
            using var bitmap = DecodeBitmap(imageBytes);
            var image = new DecodedImage(bitmap.Width, bitmap.Height, ReadOrigin(imageBytes));

            var payload = ReadStandard(bitmap, stopwatch, image, out var info)
                ?? ReadMicro(bitmap, stopwatch, image, out _)
                ?? ReadRmqr(bitmap, stopwatch, image, out _)
                ?? new DecodePayload(
                    Ok: false,
                    Text: null,
                    Status: info.Status.ToString(),
                    Symbology: null,
                    QrVersion: info.Version,
                    Ecc: null,
                    MaskPattern: info.MaskPattern,
                    ErrorsCorrected: info.ErrorsCorrected,
                    TotalMs: ElapsedMs(stopwatch),
                    ImageWidth: image.Width,
                    ImageHeight: image.Height,
                    Orientation: image.Orientation);
            return JsonSerializer.Serialize(payload, PlaygroundJsonContext.Default.DecodePayload);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new ErrorPayload(ToUserFacingMessage(ex)), PlaygroundJsonContext.Default.ErrorPayload);
        }
    }

    /// <summary>
    /// Decodes symbols from an encoded image and returns <see cref="DecodeSymbolsPayload"/> as JSON.
    /// </summary>
    /// <remarks>
    /// With <paramref name="everySymbology"/>, the shared scan returns all symbologies, Standard QR first, without duplicates.
    /// Otherwise it scans Standard QR, then Micro QR if none decoded, then rMQR if neither decoded, in discovery order.
    /// Each symbol has the diagnostics of <see cref="Decode"/>, with its ECI assignment. Bytes come with a symbol only when it is the one symbol read. No match returns <c>ok:false</c> and an empty symbols array. An unexpected failure returns <c>error</c>.
    /// </remarks>
    /// <param name="imageBytes">Encoded image file bytes.</param>
    /// <param name="everySymbology">Whether to read the symbols of every symbology together.</param>
    [JSExport]
    public static string DecodeAll(byte[] imageBytes, bool everySymbology)
    {
        try
        {
            if (imageBytes.Length == 0)
                throw new ArgumentException("Image is empty.");

            var stopwatch = Stopwatch.StartNew();
            using var bitmap = DecodeBitmap(imageBytes);
            var image = new DecodedImage(bitmap.Width, bitmap.Height, ReadOrigin(imageBytes));

            DecodePayload[] symbols;
            if (everySymbology)
            {
                var all = SymbolDecoder.DecodeSymbols(bitmap);
                var ms = ElapsedMs(stopwatch);
                symbols = Array.ConvertAll(all, r => r.Symbology switch
                {
                    Symbology.StandardQR => StandardPayload(r.StandardQR.Text, r.StandardQR.Info, ms, image),
                    Symbology.MicroQR => MicroPayload(r.MicroQR.Text, r.MicroQR.Info, ms, image),
                    _ => RmqrPayload(r.RmQR.Text, r.RmQR.Info, ms, image),
                });
            }
            else if (QRCodeDecoder.DecodeSymbols(bitmap) is { Length: > 0 } results)
            {
                var ms = ElapsedMs(stopwatch);
                symbols = Array.ConvertAll(results, r => StandardPayload(r.Text, r.Info, ms, image));
            }
            else if (MicroQRCodeDecoder.DecodeSymbols(bitmap) is { Length: > 0 } micro)
            {
                var ms = ElapsedMs(stopwatch);
                symbols = Array.ConvertAll(micro, r => MicroPayload(r.Text, r.Info, ms, image));
            }
            else
            {
                var rmqr = RmQRCodeDecoder.DecodeSymbols(bitmap);
                var ms = ElapsedMs(stopwatch);
                symbols = Array.ConvertAll(rmqr, r => RmqrPayload(r.Text, r.Info, ms, image));
            }

            // The several-symbol calls give text alone, and no bytes form of them exists. The single-symbol bytes call
            // picks a symbol of its own, which can be another one, since it also reads symbols the text calls refuse.
            // So bytes are added only to the one symbol read, and only when the bytes call finds that same symbol.
            // The time is taken first, since the bytes read is for the view and not part of the read timed.
            var totalMs = ElapsedMs(stopwatch);
            if (symbols.Length == 1)
                symbols[0] = WithBytesOfSameSymbol(symbols[0], bitmap);

            var payload = new DecodeSymbolsPayload(
                Ok: symbols.Length > 0,
                Symbols: symbols,
                TotalMs: totalMs,
                ImageWidth: image.Width,
                ImageHeight: image.Height,
                Orientation: image.Orientation);
            return JsonSerializer.Serialize(payload, PlaygroundJsonContext.Default.DecodeSymbolsPayload);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new ErrorPayload(ToUserFacingMessage(ex)), PlaygroundJsonContext.Default.ErrorPayload);
        }
    }

    /// <summary>
    /// The image as the decoders saw it, and its EXIF orientation tag. <see cref="SKBitmap.Decode(byte[])"/> ignores the tag,
    /// so the corners are in the stored frame. A browser applies it, so the page needs it to place the overlay.
    /// </summary>
    private readonly record struct DecodedImage(int Width, int Height, int Orientation);

    private static SKBitmap DecodeBitmap(byte[] imageBytes)
        => SKBitmap.Decode(imageBytes) ?? throw new ArgumentException("The file is not a decodable image (PNG/JPEG/WebP).");

    private static double ElapsedMs(Stopwatch stopwatch) => Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1);

    /// <param name="text">The text, or null for a symbol read as bytes alone.</param>
    private static DecodePayload StandardPayload(string? text, QRCodeDecodeInfo info, double totalMs, DecodedImage image)
    {
        var gs1 = text is null ? default : ReadGs1(text, info.Fnc1);
        return new DecodePayload(
            Ok: true,
            Text: text,
            Status: info.Status.ToString(),
            Symbology: "qr",
            QrVersion: info.Version,
            Ecc: info.EccLevel.ToString(),
            MaskPattern: info.MaskPattern,
            ErrorsCorrected: info.ErrorsCorrected,
            TotalMs: totalMs,
            Corners: Flatten(info.Corners),
            ImageWidth: image.Width,
            ImageHeight: image.Height,
            Orientation: image.Orientation,
            Fnc1: Fnc1Label(info.Fnc1),
            ApplicationIndicator: info.Fnc1.ApplicationIndicator,
            Gs1Hri: gs1.Hri,
            Gs1Elements: gs1.Elements,
            Gs1Error: gs1.Error,
            Gs1Check: gs1.Check,
            EciAssignment: info.EciAssignment);
    }

    // One symbol of each symbology, read as text and then as bytes. Each read searches the bitmap on its own,
    // so the bytes are kept only when they come from the symbol the text came from (see WithBytesOfSameSymbol).
    // A symbol whose content the text call cannot convert is read as bytes alone, with no text.
    // A symbol read as text is timed by that read alone, since the bytes read after it only fills the view.

    /// <summary>One Standard QR symbol, or null when none reads. <paramref name="info"/> is the text read's, for the failure the page shows.</summary>
    private static DecodePayload? ReadStandard(SKBitmap bitmap, Stopwatch stopwatch, DecodedImage image, out QRCodeDecodeInfo info)
    {
        if (QRCodeDecoder.TryDecode(bitmap, out var text, out info))
            return WithBytesOfSameSymbol(StandardPayload(text, info, ElapsedMs(stopwatch), image), bitmap);
        if (IsNotText(info.Status) && QRCodeDecoder.TryDecodeBytes(bitmap, out var bytes, out var bytesInfo))
            return WithBytes(StandardPayload(null, bytesInfo, ElapsedMs(stopwatch), image), bytes, bytesInfo.EciAssignment);
        return null;
    }

    /// <summary>One Micro QR symbol, or null when none reads.</summary>
    private static DecodePayload? ReadMicro(SKBitmap bitmap, Stopwatch stopwatch, DecodedImage image, out MicroQRCodeDecodeInfo info)
    {
        if (MicroQRCodeDecoder.TryDecode(bitmap, out var text, out info))
            return WithBytesOfSameSymbol(MicroPayload(text, info, ElapsedMs(stopwatch), image), bitmap);
        if (IsNotText(info.Status) && MicroQRCodeDecoder.TryDecodeBytes(bitmap, out var bytes, out var bytesInfo))
            return WithBytes(MicroPayload(null, bytesInfo, ElapsedMs(stopwatch), image), bytes, null);
        return null;
    }

    /// <summary>One rMQR symbol, or null when none reads.</summary>
    private static DecodePayload? ReadRmqr(SKBitmap bitmap, Stopwatch stopwatch, DecodedImage image, out RmQRCodeDecodeInfo info)
    {
        if (RmQRCodeDecoder.TryDecode(bitmap, out var text, out info))
            return WithBytesOfSameSymbol(RmqrPayload(text, info, ElapsedMs(stopwatch), image), bitmap);
        if (IsNotText(info.Status) && RmQRCodeDecoder.TryDecodeBytes(bitmap, out var bytes, out var bytesInfo))
            return WithBytes(RmqrPayload(null, bytesInfo, ElapsedMs(stopwatch), image), bytes, bytesInfo.EciAssignment);
        return null;
    }

    /// <summary>A text read that failed on what the symbol holds, not on finding it: a charset the library does not know, such as binary data under ECI 899, or a character it cannot map.</summary>
    private static bool IsNotText(DecodeStatus status) => status is DecodeStatus.UnsupportedContent or DecodeStatus.UnmappedCharacter;

    /// <summary>
    /// The symbol with its bytes read by the single-symbol bytes call of its symbology, or as it was when that call finds another symbol or none.
    /// The assignment becomes the one the bytes call reports, the one the bytes are under. A symbol that declares several can hold it apart from the first one, which the text read reports.
    /// </summary>
    private static DecodePayload WithBytesOfSameSymbol(DecodePayload symbol, SKBitmap bitmap)
    {
        switch (symbol.Symbology)
        {
            case "qr":
                if (QRCodeDecoder.TryDecodeBytes(bitmap, out var bytes, out var info) && SameSymbol(symbol.Corners, Flatten(info.Corners)))
                    return WithBytes(symbol, bytes, info.EciAssignment);
                break;
            case "microqr":
                if (MicroQRCodeDecoder.TryDecodeBytes(bitmap, out var microBytes, out var microInfo) && SameSymbol(symbol.Corners, Flatten(microInfo.Corners)))
                    return WithBytes(symbol, microBytes, null);
                break;
            case "rmqr":
                if (RmQRCodeDecoder.TryDecodeBytes(bitmap, out var rmBytes, out var rmInfo) && SameSymbol(symbol.Corners, Flatten(rmInfo.Corners)))
                    return WithBytes(symbol, rmBytes, rmInfo.EciAssignment);
                break;
        }
        return symbol;
    }

    private static DecodePayload WithBytes(DecodePayload symbol, byte[] bytes, int? eciAssignment)
        => symbol with { BytesHex = Convert.ToHexString(bytes), EciAssignment = eciAssignment };

    /// <summary>
    /// Whether two reads found the same symbol, by the corners each reports. Symbols do not overlap, so another symbol's centre
    /// lies at least half of this one's shorter side away. Centres closer than a quarter of it are taken as one symbol.
    /// </summary>
    private static bool SameSymbol(float[]? a, float[]? b)
    {
        if (a is null || b is null)
            return false;
        var shortestSide = float.MaxValue;
        for (var i = 0; i < 8; i += 2)
        {
            var next = (i + 2) % 8;
            shortestSide = Math.Min(shortestSide, MathF.Sqrt(Square(a[next] - a[i]) + Square(a[next + 1] - a[i + 1])));
        }
        var dx = (a[0] + a[2] + a[4] + a[6] - b[0] - b[2] - b[4] - b[6]) / 4;
        var dy = (a[1] + a[3] + a[5] + a[7] - b[1] - b[3] - b[5] - b[7]) / 4;
        return MathF.Sqrt(Square(dx) + Square(dy)) < shortestSide / 4;
    }

    private static float Square(float value) => value * value;

    /// <param name="text">The text, or null for a symbol read as bytes alone.</param>
    private static DecodePayload MicroPayload(string? text, MicroQRCodeDecodeInfo info, double totalMs, DecodedImage image)
        => new(
            Ok: true,
            Text: text,
            Status: info.Status.ToString(),
            Symbology: "microqr",
            QrVersion: (int)info.Version,
            Ecc: info.EccLevel.ToString(),
            MaskPattern: info.MaskPattern,
            ErrorsCorrected: info.ErrorsCorrected,
            TotalMs: totalMs,
            Corners: Flatten(info.Corners),
            ImageWidth: image.Width,
            ImageHeight: image.Height,
            Orientation: image.Orientation);

    /// <param name="text">The text, or null for a symbol read as bytes alone.</param>
    private static DecodePayload RmqrPayload(string? text, RmQRCodeDecodeInfo info, double totalMs, DecodedImage image)
    {
        // rMQR has a single fixed mask, so no mask pattern is reported (-1).
        var gs1 = text is null ? default : ReadGs1(text, info.Fnc1);
        return new DecodePayload(
            Ok: true,
            Text: text,
            Status: info.Status.ToString(),
            Symbology: "rmqr",
            QrVersion: (int)info.Version,
            Ecc: info.EccLevel.ToString(),
            MaskPattern: -1,
            ErrorsCorrected: info.ErrorsCorrected,
            TotalMs: totalMs,
            Corners: Flatten(info.Corners),
            ImageWidth: image.Width,
            ImageHeight: image.Height,
            Orientation: image.Orientation,
            Fnc1: Fnc1Label(info.Fnc1),
            ApplicationIndicator: info.Fnc1.ApplicationIndicator,
            Gs1Hri: gs1.Hri,
            Gs1Elements: gs1.Elements,
            Gs1Error: gs1.Error,
            Gs1Check: gs1.Check,
            EciAssignment: info.EciAssignment);
    }

    /// <summary>The FNC1 mode for the page: "first" (GS1), "second" (an AIM industry format), or null without FNC1.</summary>
    private static string? Fnc1Label(Fnc1Mode fnc1) => fnc1.IsFirstPosition ? "first" : fnc1.IsSecondPosition ? "second" : null;

    /// <summary>
    /// GS1 data in a decoded symbol, as (AI) text and its elements: the element string under FNC1 in first position, or a Digital Link URI without FNC1.
    /// A symbol in first position claims GS1 data, so text that does not read as GS1 data reports why. Plain text that is not a Digital Link URI is not GS1 data and reports nothing, as does second position.
    /// The read takes data the checks would refuse, a wrong check digit for one, so the element string is checked as well.
    /// </summary>
    private static Gs1Read ReadGs1(string text, Fnc1Mode fnc1)
    {
        if (fnc1.IsSecondPosition)
            return default;
        if (!Gs1ElementString.TryRead(text, fnc1, out var hri, out var error))
            return fnc1.IsFirstPosition ? new Gs1Read(null, null, error.ToString(), null) : default;
        var elementString = text;
        if (!fnc1.IsFirstPosition && !Gs1ElementString.TryFromDigitalLink(text, out elementString, out _))
            return new Gs1Read(hri, null, null, null);
        var elements = new List<Gs1ElementDto>();
        foreach (var element in Gs1ElementString.EnumerateElements(elementString))
            elements.Add(new Gs1ElementDto(element.Ai.ToString(), element.Value.ToString()));
        var check = Gs1ElementString.TryValidate(elementString, out var checkError) ? null : checkError.ToString();
        return new Gs1Read(hri, [.. elements], null, check);
    }

    private readonly record struct Gs1Read(string? Hri, Gs1ElementDto[]? Elements, string? Error, string? Check);

    /// <summary>
    /// The symbol's corners as eight numbers for the page to stroke, in the order the contract
    /// names them (top-left, top-right, bottom-right, bottom-left), or null when the decode
    /// reported none. Empty corners cannot happen on the success paths that call this, but the
    /// check keeps the payload honest if a decoder ever stops attaching them.
    /// </summary>
    private static float[]? Flatten(SymbolCorners corners) => corners.IsEmpty
        ? null
        :
        [
            corners.TopLeft.X, corners.TopLeft.Y,
            corners.TopRight.X, corners.TopRight.Y,
            corners.BottomRight.X, corners.BottomRight.Y,
            corners.BottomLeft.X, corners.BottomLeft.Y,
        ];

    /// <summary>The EXIF orientation tag (1-8), or 1 when absent. <see cref="SKCodec"/> parses the tag that <see cref="SKBitmap.Decode"/> ignores.</summary>
    private static int ReadOrigin(byte[] imageBytes)
    {
        try
        {
            using var codec = SKCodec.Create(new SKMemoryStream(imageBytes));
            return codec is null ? 1 : (int)codec.EncodedOrigin;
        }
        catch
        {
            // A format SKCodec cannot open is not worth failing the decode over.
            return 1;
        }
    }

    private static byte[] GenerateCore(QrRequest request, byte[] customLogo)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
            throw new ArgumentException("Content is empty.");

        var stopwatch = Stopwatch.StartNew();
        if (IsRmQR(request))
        {
            var rmData = CreateRmData(request);
            var rmBytes = CreateRmBuilder(request, rmData).ToByteArray();
            stopwatch.Stop();

            // matrixSize doubles as the width so older page scripts keep working, and matrixHeight is the rectangular extra.
            SetLastMeta(new GenerateMeta("rmqr", (int)rmData.Version, rmData.Width, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1), rmBytes.Length, rmData.Height, Gs1Encoded(request)));
            return rmBytes;
        }

        if (IsMicroQR(request))
        {
            var microData = CreateMicroData(request);
            var microBytes = CreateMicroBuilder(request, microData).ToByteArray();
            stopwatch.Stop();

            SetLastMeta(new GenerateMeta("microqr", (int)microData.Version, microData.Size, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1), microBytes.Length));
            return microBytes;
        }

        var data = CreateStandardData(request);

        var bytes = CreateBuilder(request, data, customLogo).ToByteArray();
        stopwatch.Stop();

        SetLastMeta(new GenerateMeta("qr", data.Version, data.Size, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1), bytes.Length, Encoded: Gs1Encoded(request)));
        return bytes;
    }

    private static void SetLastMeta(GenerateMeta meta)
        => s_lastMeta = JsonSerializer.Serialize(meta, PlaygroundJsonContext.Default.GenerateMeta);

    private static bool IsMicroQR(QrRequest request)
        => string.Equals(request.Symbology, "microqr", StringComparison.OrdinalIgnoreCase);

    private static bool IsRmQR(QrRequest request)
        => string.Equals(request.Symbology, "rmqr", StringComparison.OrdinalIgnoreCase);

    private static QRCodeData CreateStandardData(QrRequest request)
    {
        var text = request.Content.AsSpan();
        var ecc = ParseEcc(request.Ecc);
        var options = StandardOptions(request);
        if (ContentBytes(request) is { } bytes)
            return QRCodeGenerator.Create(bytes, ecc, options, request.EciAssignment);
        return Gs1Carrier(request) switch
        {
            "qr" => QRCodeGenerator.CreateGs1(text, ecc, options),
            "link" => QRCodeGenerator.CreateGs1DigitalLink(text, ecc, options, Gs1Stem(request)),
            _ => QRCodeGenerator.Create(text, ecc, options),
        };
    }

    private static QRCodeGeneratorOptions StandardOptions(QrRequest request) => new()
    {
        Version = ParseVersionRange(request.Version),
        QuietZoneSize = Math.Clamp(request.QuietZone, 0, 10),
        BoostEccLevel = request.EccBoost,
        AllowKanji = request.AllowKanji,
        Fnc1 = ParseFnc1(request),
    };

    private static RmQRCodeData CreateRmData(QrRequest request)
    {
        var text = request.Content.AsSpan();
        var ecc = ParseRmEcc(request.Ecc);
        var options = RmOptions(request);
        if (ContentBytes(request) is { } bytes)
            return RmQRCodeGenerator.Create(bytes, ecc, options, request.EciAssignment);
        return Gs1Carrier(request) switch
        {
            "qr" => RmQRCodeGenerator.CreateGs1(text, ecc, options),
            "link" => RmQRCodeGenerator.CreateGs1DigitalLink(text, ecc, options, Gs1Stem(request)),
            _ => RmQRCodeGenerator.Create(text, ecc, options),
        };
    }

    private static RmQRCodeGeneratorOptions RmOptions(QrRequest request) => new()
    {
        Version = ParseRmVersion(request.Version),
        FitStrategy = ParseRmFitStrategy(request.FitStrategy),
        Height = ParseRmHeight(request.Height),
        QuietZoneSize = Math.Clamp(request.QuietZone, 0, 10),
        AllowKanji = request.AllowKanji,
        Fnc1 = ParseFnc1(request),
    };

    /// <summary>
    /// How the request carries GS1 data: "qr" as a GS1 QR Code (the element string under FNC1 in first position), "link" as its Digital Link URI in a plain symbol, "none" as plain text.
    /// The content is then (AI) text or an element string, which the entry points tell apart by its first character.
    /// </summary>
    private static string Gs1Carrier(QrRequest request) => (request.Gs1 ?? "none").ToLowerInvariant() switch
    {
        "none" => "none",
        "qr" => "qr",
        "link" => "link",
        _ => throw new ArgumentException($"Unknown GS1 carrier '{request.Gs1}'. Use none, qr or link."),
    };

    /// <summary>The Digital Link stem, or null for the canonical one when the field is blank.</summary>
    private static string? Gs1Stem(QrRequest request) => string.IsNullOrWhiteSpace(request.Gs1Stem) ? null : request.Gs1Stem.Trim();

    /// <summary>
    /// The text a GS1 request wrote into the symbol, for the page to show and to check the decode against: the element string, or its Digital Link URI. Null for plain text.
    /// It runs after the entry point took the data, so each conversion passes, and it reads (AI) text by the entry points' own rule, a first character of '('.
    /// </summary>
    private static string? Gs1Encoded(QrRequest request)
    {
        var carrier = Gs1Carrier(request);
        if (carrier == "none")
            return null;
        var text = request.Content;
        var elementString = text.StartsWith('(') && Gs1ElementString.TryFromHri(text, out var converted, out _) ? converted : text;
        if (carrier == "qr")
            return elementString;
        return Gs1ElementString.TryToDigitalLink(elementString, out var uri, out _, Gs1Stem(request)) ? uri : null;
    }

    /// <summary>Micro QR cannot carry GS1 data: it has no FNC1, and GS1 excludes it as a carrier.</summary>
    private static void RequireNoGs1(QrRequest request)
    {
        if (Gs1Carrier(request) != "none")
            throw new ArgumentException("Micro QR cannot carry GS1 data. Use Standard QR or rMQR.");
    }

    /// <summary>
    /// Builds the rMQR image builder from request options.
    /// The requested size is the image width, and the height follows the rectangular symbol. rMQR has no icon overlay, so the page hides that control. The finder shape it does take is not offered here yet.
    /// </summary>
    private static RmQRCodeImageBuilder CreateRmBuilder(QrRequest request, RmQRCodeData data)
    {
        return new RmQRCodeImageBuilder(data)
            .WithWidth(Math.Clamp(request.Size, 64, 2048))
            .WithColors(
                ParseColor(request.Foreground, SKColors.Black),
                ParseColor(request.Background, SKColors.White))
            .WithModuleShape(CreateModuleShape(request), Math.Clamp(request.ModuleSizePercent, 0.5f, 1.0f))
            .WithGradient(CreateGradient(request.Gradient));
    }

    private static RmQREccLevel ParseRmEcc(string ecc) => ecc.ToUpperInvariant() switch
    {
        "M" => RmQREccLevel.M,
        "H" => RmQREccLevel.H,
        _ => throw new ArgumentException($"Unknown rMQR ECC level '{ecc}'. Use M or H."),
    };

    private static RmQRVersion? ParseRmVersion(int version) => version switch
    {
        -1 => null,
        >= 1 and <= 32 => (RmQRVersion)version,
        _ => throw new ArgumentException($"Unknown rMQR version '{version}'. Use 1-32 (R7x43-R17x139) or -1 for automatic selection."),
    };

    private static RmQRFitStrategy ParseRmFitStrategy(string? fitStrategy) => (fitStrategy ?? "area").ToLowerInvariant() switch
    {
        "area" => RmQRFitStrategy.MinimizeArea,
        "width" => RmQRFitStrategy.MinimizeWidth,
        "height" => RmQRFitStrategy.MinimizeHeight,
        _ => throw new ArgumentException($"Unknown rMQR fit strategy '{fitStrategy}'. Use area, width or height."),
    };

    private static RmQRHeight? ParseRmHeight(int height) => height switch
    {
        0 => null,
        7 or 9 or 11 or 13 or 15 or 17 => (RmQRHeight)height,
        _ => throw new ArgumentException($"Unknown rMQR height '{height}'. Use 7, 9, 11, 13, 15, 17 or 0 for any."),
    };

    private static MicroQRCodeData CreateMicroData(QrRequest request)
    {
        RequireNoFnc1(request);
        RequireNoGs1(request);
        if (ContentBytes(request) is { } bytes)
        {
            RequireNoEciAssignment(request);
            return MicroQRCodeGenerator.Create(bytes, ParseMicroEcc(request.Ecc), MicroOptions(request));
        }
        return MicroQRCodeGenerator.Create(
            request.Content.AsSpan(),
            ParseMicroEcc(request.Ecc),
            new MicroQRCodeGeneratorOptions
            {
                Version = ParseMicroVersion(request.Version),
                QuietZoneSize = Math.Clamp(request.QuietZone, 0, 10),
                AllowKanji = request.AllowKanji,
            });
    }

    private static MicroQRCodeGeneratorOptions MicroOptions(QrRequest request) => new()
    {
        Version = ParseMicroVersion(request.Version),
        QuietZoneSize = Math.Clamp(request.QuietZone, 0, 10),
        AllowKanji = request.AllowKanji,
    };

    /// <summary>Micro QR has no ECI. The page disables the field and sends none, so an assignment is a malformed request rather than one to ignore.</summary>
    private static void RequireNoEciAssignment(QrRequest request)
    {
        if (request.EciAssignment is not null)
            throw new ArgumentException("Micro QR has no ECI, so it takes no ECI assignment. Use Standard QR or rMQR, or leave the assignment blank.");
    }

    /// <summary>
    /// The bytes of a bytes request, or null for text, which keeps its own route.
    /// The bytes calls have no GS1 entry point, so a GS1 carrier is refused here. The library refuses Kanji mode and FNC1 with bytes itself.
    /// An assignment belongs to bytes, so a text request that carries one is malformed.
    /// </summary>
    private static byte[]? ContentBytes(QrRequest request)
    {
        switch ((request.ContentKind ?? "text").ToLowerInvariant())
        {
            case "text":
                if (request.EciAssignment is not null)
                    throw new ArgumentException("An ECI assignment applies to bytes. Text is written in the charset the library picks for it.");
                return null;
            case "bytes":
                if (Gs1Carrier(request) != "none")
                    throw new ArgumentException("GS1 data is written as text. Switch the content to Text for a GS1 format.");
                return ParseHex(request.Content);
            default:
                throw new ArgumentException($"Unknown content kind '{request.ContentKind}'. Use text or bytes.");
        }
    }

    /// <summary>
    /// The bytes of hex content, by the rule the page checks first: either case, with whitespace, ':' and '-' between digits skipped.
    /// The page sends checked hex, so these messages reach it only from a request it did not build.
    /// </summary>
    private static byte[] ParseHex(string content)
    {
        var digits = new char[content.Length];
        var count = 0;
        var position = 0;
        foreach (var rune in content.EnumerateRunes())
        {
            position++;
            if (Rune.IsWhiteSpace(rune) || rune.Value is ':' or '-')
                continue;
            if (!rune.IsAscii || !char.IsAsciiHexDigit((char)rune.Value))
            {
                var shown = Rune.IsControl(rune) ? $"U+{rune.Value:X4}" : $"'{rune}'";
                throw new ArgumentException($"{shown} at position {position} is not a hex digit. Use 0-9 and A-F, with spaces, ':' or '-' between bytes if you like.");
            }
            digits[count++] = (char)rune.Value;
        }
        if (count % 2 != 0)
            throw new ArgumentException($"The hex has {count} digits, an odd number. Each byte takes two digits.");
        if (count == 0)
            throw new ArgumentException("Content is empty.");
        return Convert.FromHexString(digits.AsSpan(0, count));
    }

    /// <summary>
    /// Builds the Micro QR image builder from request options.
    /// Icon options are ignored because the Micro QR builder does not support overlays.
    /// </summary>
    private static MicroQRCodeImageBuilder CreateMicroBuilder(QrRequest request, MicroQRCodeData data)
    {
        var size = Math.Clamp(request.Size, 64, 2048);
        return new MicroQRCodeImageBuilder(data)
            .WithSize(size, size)
            .WithColors(
                ParseColor(request.Foreground, SKColors.Black),
                ParseColor(request.Background, SKColors.White))
            .WithModuleShape(CreateModuleShape(request), Math.Clamp(request.ModuleSizePercent, 0.5f, 1.0f))
            .WithGradient(CreateGradient(request.Gradient));
    }

    private static MicroQREccLevel ParseMicroEcc(string ecc) => ecc.ToUpperInvariant() switch
    {
        "EDO" => MicroQREccLevel.ErrorDetectionOnly,
        "L" => MicroQREccLevel.L,
        "M" => MicroQREccLevel.M,
        "Q" => MicroQREccLevel.Q,
        _ => throw new ArgumentException($"Unknown Micro QR ECC level '{ecc}'. Use EDO, L, M or Q."),
    };

    private static MicroQRVersion? ParseMicroVersion(int version) => version switch
    {
        -1 => null,
        >= 1 and <= 4 => (MicroQRVersion)version,
        _ => throw new ArgumentException($"Unknown Micro QR version '{version}'. Use 1-4 (M1-M4) or -1 for automatic selection."),
    };

    /// <summary>
    /// The FNC1 mode of a Standard QR or rMQR request. The page sends the separator as U+001D, so the content is already the text the decoder returns.
    /// </summary>
    private static Fnc1Mode ParseFnc1(QrRequest request) => (request.Fnc1 ?? "none").ToLowerInvariant() switch
    {
        "none" => Fnc1Mode.None,
        "first" => Fnc1Mode.FirstPosition,
        "second" => ParseApplicationIndicator(request.ApplicationIndicator),
        _ => throw new ArgumentException($"Unknown FNC1 mode '{request.Fnc1}'. Use none, first or second."),
    };

    /// <summary>
    /// Second position from the indicator: 00 to 99 in one or two digits, or one letter.
    /// </summary>
    private static Fnc1Mode ParseApplicationIndicator(string? indicator)
    {
        var text = (indicator ?? "").Trim();
        if (text.Length is 1 or 2 && text.All(char.IsAsciiDigit))
            return Fnc1Mode.SecondPosition(int.Parse(text, CultureInfo.InvariantCulture));
        if (text.Length == 1 && char.IsAsciiLetter(text[0]))
            return Fnc1Mode.SecondPosition(text[0]);
        throw new ArgumentException($"The application indicator must be a number 00-99 or one letter A-Z or a-z, but was '{text}'.");
    }

    /// <summary>Micro QR has no FNC1 mode. The page hides the control and sends none, so another value is a malformed request rather than one to ignore.</summary>
    private static void RequireNoFnc1(QrRequest request)
    {
        if (ParseFnc1(request) != Fnc1Mode.None)
            throw new ArgumentException("Micro QR has no FNC1 mode. Use Standard QR or rMQR for GS1 data.");
    }

    /// <summary>
    /// The request carries -1 for "pick the smallest version that fits", which <see cref="QRVersionRange"/> spells as <see cref="QRVersionRange.Any"/>. It deliberately rejects -1 so a defaulted field cannot pass for automatic.
    /// </summary>
    private static QRVersionRange ParseVersionRange(int version)
        => version == -1 ? QRVersionRange.Any : QRVersionRange.Exactly(version);

    /// <summary>Builds the image builder from request options. It is shared by preview and benchmark rendering.</summary>
    private static QRCodeImageBuilder CreateBuilder(QrRequest request, QRCodeData data, byte[] customLogo)
    {
        var size = Math.Clamp(request.Size, 64, 2048);
        var builder = new QRCodeImageBuilder(data)
            .WithSize(size, size)
            .WithColors(
                ParseColor(request.Foreground, SKColors.Black),
                ParseColor(request.Background, SKColors.White))
            .WithModuleShape(CreateModuleShape(request), Math.Clamp(request.ModuleSizePercent, 0.5f, 1.0f))
            .WithGradient(CreateGradient(request.Gradient))
            .WithIcon(CreateIcon(request.Logo, customLogo));

        // Auto leaves the setter uncalled: plain full-size modules then draw the finders with the modules,
        // and a styled module set gets the solid square the renderer substitutes for it.
        var finderShape = CreateFinderShape(request.FinderShape);
        return finderShape is null ? builder : builder.WithFinderPatternShape(finderShape);
    }

    /// <summary>
    /// Runs one benchmark batch and returns stats as a JSON string: <c>{"count":N,"elapsedMs":N,"qrVersion":N,"matrixSize":N,"bytesTotal":N}</c>, or <c>{"error":"..."}</c> on failure.
    /// The page script chains batches so the UI stays responsive and can show progress / cancel.
    /// <para>
    /// Content is made unique per iteration by appending <c>" #&lt;index+1&gt;"</c>.
    /// Mode <c>encode</c> exercises the allocation-free <see cref="QRCodeGenerator.Create(ReadOnlySpan{char}, QREccLevel, Span{byte}, in QRCodeGeneratorOptions)"/> overload only. Mode <c>render</c> runs the full pipeline (encode + Skia render + PNG encode) with the current visual options.
    /// </para>
    /// </summary>
    /// <param name="optionsJson">Serialized <see cref="QrRequest"/> (camelCase).</param>
    /// <param name="mode">"encode" or "render".</param>
    /// <param name="startIndex">Global index of the first iteration in this batch.</param>
    /// <param name="count">Iterations to run in this batch.</param>
    /// <param name="customLogo">Uploaded logo bytes for render mode, empty otherwise.</param>
    [JSExport]
    public static string BenchmarkBatch(string optionsJson, string mode, int startIndex, int count, byte[] customLogo)
    {
        try
        {
            var request = JsonSerializer.Deserialize(optionsJson, PlaygroundJsonContext.Default.QrRequest)
                ?? throw new InvalidOperationException("Options JSON deserialized to null.");
            if (string.IsNullOrWhiteSpace(request.Content))
                throw new ArgumentException("Content is empty.");
            if (count is <= 0 or > 1_000_000)
                throw new ArgumentOutOfRangeException(nameof(count), "Batch count must be between 1 and 1,000,000.");

            // The render loops build each symbol through the Create*Data methods, which take bytes as they take text.
            if (ContentBytes(request) is { } bytes)
            {
                return (mode, IsRmQR(request), IsMicroQR(request)) switch
                {
                    ("encode", true, _) => BenchmarkRmBytesEncode(request, bytes, count),
                    ("render", true, _) => BenchmarkRmRender(request, count),
                    ("encode", _, true) => BenchmarkMicroBytesEncode(request, bytes, count),
                    ("render", _, true) => BenchmarkMicroRender(request, count),
                    ("encode", _, _) => BenchmarkBytesEncode(request, bytes, startIndex, count),
                    ("render", _, _) => BenchmarkBytesRender(request, bytes, startIndex, count, customLogo),
                    _ => throw new ArgumentException($"Unknown benchmark mode '{mode}'. Use 'encode' or 'render'."),
                };
            }

            if (Gs1Carrier(request) != "none")
            {
                return mode switch
                {
                    "encode" => BenchmarkGs1Encode(request, count),
                    "render" => BenchmarkGs1Render(request, count, customLogo),
                    _ => throw new ArgumentException($"Unknown benchmark mode '{mode}'. Use 'encode' or 'render'."),
                };
            }

            if (IsRmQR(request))
            {
                return mode switch
                {
                    "encode" => BenchmarkRmEncode(request, count),
                    "render" => BenchmarkRmRender(request, count),
                    _ => throw new ArgumentException($"Unknown benchmark mode '{mode}'. Use 'encode' or 'render'."),
                };
            }

            if (IsMicroQR(request))
            {
                return mode switch
                {
                    "encode" => BenchmarkMicroEncode(request, count),
                    "render" => BenchmarkMicroRender(request, count),
                    _ => throw new ArgumentException($"Unknown benchmark mode '{mode}'. Use 'encode' or 'render'."),
                };
            }

            return mode switch
            {
                "encode" => BenchmarkEncode(request, startIndex, count),
                "render" => BenchmarkRender(request, startIndex, count, customLogo),
                _ => throw new ArgumentException($"Unknown benchmark mode '{mode}'. Use 'encode' or 'render'."),
            };
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new ErrorPayload(ToUserFacingMessage(ex)), PlaygroundJsonContext.Default.ErrorPayload);
        }
    }

    /// <summary>
    /// Tight loop over the zero-allocation span API.
    /// The per-iteration text is composed in a pooled char buffer (no string allocation) and the module matrix is written into a pooled byte buffer sized for QR version 40, so the loop itself allocates nothing.
    /// </summary>
    private static string BenchmarkEncode(QrRequest request, int startIndex, int count)
    {
        var ecc = ParseEcc(request.Ecc);
        var quietZone = Math.Clamp(request.QuietZone, 0, 10);
        var fnc1 = ParseFnc1(request);
        var prefixLength = request.Content.Length;

        // " #" + int.MaxValue digits
        var textBuffer = ArrayPool<char>.Shared.Rent(prefixLength + 2 + 11);
        // Version 40 with quiet zone is the largest possible matrix.
        var maxSide = 177 + 2 * quietZone;
        var moduleBuffer = ArrayPool<byte>.Shared.Rent(maxSide * maxSide);
        try
        {
            request.Content.AsSpan().CopyTo(textBuffer);
            textBuffer[prefixLength] = ' ';
            textBuffer[prefixLength + 1] = '#';

            long bytesTotal = 0;
            var written = 0;
            var stopwatch = Stopwatch.StartNew();
            for (var i = 0; i < count; i++)
            {
                (startIndex + i + 1).TryFormat(textBuffer.AsSpan(prefixLength + 2), out var digits);
                var text = textBuffer.AsSpan(0, prefixLength + 2 + digits);
                written = QRCodeGenerator.Create(text, ecc, moduleBuffer, new QRCodeGeneratorOptions { Version = ParseVersionRange(request.Version), QuietZoneSize = quietZone, AllowKanji = request.AllowKanji, Fnc1 = fnc1 });
                bytesTotal += written;
            }
            stopwatch.Stop();

            var matrixSize = (int)Math.Sqrt(written);
            var qrVersion = (matrixSize - 2 * quietZone - 17) / 4;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"count\":{count},\"elapsedMs\":{stopwatch.Elapsed.TotalMilliseconds:F2},\"qrVersion\":{qrVersion},\"matrixSize\":{matrixSize},\"bytesTotal\":{bytesTotal}}}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(moduleBuffer, clearArray: false);
            ArrayPool<char>.Shared.Return(textBuffer, clearArray: false);
        }
    }

    /// <summary>Full pipeline per iteration: encode, Skia render with current options, PNG encode.</summary>
    private static string BenchmarkRender(QrRequest request, int startIndex, int count, byte[] customLogo)
    {
        var ecc = ParseEcc(request.Ecc);
        var quietZone = Math.Clamp(request.QuietZone, 0, 10);
        var fnc1 = ParseFnc1(request);

        long bytesTotal = 0;
        var qrVersion = 0;
        var matrixSize = 0;
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            var text = string.Create(CultureInfo.InvariantCulture, $"{request.Content} #{startIndex + i + 1}");
            var data = QRCodeGenerator.Create(text.AsSpan(), ecc, new QRCodeGeneratorOptions { Version = ParseVersionRange(request.Version), QuietZoneSize = quietZone, AllowKanji = request.AllowKanji, Fnc1 = fnc1 });
            qrVersion = data.Version;
            matrixSize = data.Size;
            bytesTotal += CreateBuilder(request, data, customLogo).ToByteArray().Length;
        }
        stopwatch.Stop();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"count\":{count},\"elapsedMs\":{stopwatch.Elapsed.TotalMilliseconds:F2},\"qrVersion\":{qrVersion},\"matrixSize\":{matrixSize},\"bytesTotal\":{bytesTotal}}}");
    }

    /// <summary>
    /// <see cref="BenchmarkEncode"/> for bytes: the same unique suffix, <c>" #&lt;index+1&gt;"</c>, appended as its ASCII bytes, so each code is new as it is for text.
    /// The bytes are composed in a pooled buffer and the matrix written into one sized for version 40, so the loop allocates nothing.
    /// </summary>
    private static string BenchmarkBytesEncode(QrRequest request, byte[] bytes, int startIndex, int count)
    {
        var ecc = ParseEcc(request.Ecc);
        var options = StandardOptions(request);
        var quietZone = options.QuietZoneSize;
        var prefixLength = bytes.Length;

        // " #" + int.MaxValue digits
        var dataBuffer = ArrayPool<byte>.Shared.Rent(prefixLength + 2 + 11);
        // Version 40 with quiet zone is the largest possible matrix.
        var maxSide = 177 + 2 * quietZone;
        var moduleBuffer = ArrayPool<byte>.Shared.Rent(maxSide * maxSide);
        try
        {
            bytes.CopyTo(dataBuffer, 0);
            dataBuffer[prefixLength] = (byte)' ';
            dataBuffer[prefixLength + 1] = (byte)'#';

            long bytesTotal = 0;
            var written = 0;
            var stopwatch = Stopwatch.StartNew();
            for (var i = 0; i < count; i++)
            {
                (startIndex + i + 1).TryFormat(dataBuffer.AsSpan(prefixLength + 2), out var digits, provider: CultureInfo.InvariantCulture);
                var data = dataBuffer.AsSpan(0, prefixLength + 2 + digits);
                written = QRCodeGenerator.Create(data, ecc, moduleBuffer, options, request.EciAssignment);
                bytesTotal += written;
            }
            stopwatch.Stop();

            var matrixSize = (int)Math.Sqrt(written);
            var qrVersion = (matrixSize - 2 * quietZone - 17) / 4;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"count\":{count},\"elapsedMs\":{stopwatch.Elapsed.TotalMilliseconds:F2},\"qrVersion\":{qrVersion},\"matrixSize\":{matrixSize},\"bytesTotal\":{bytesTotal}}}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(moduleBuffer, clearArray: false);
            ArrayPool<byte>.Shared.Return(dataBuffer, clearArray: false);
        }
    }

    /// <summary><see cref="BenchmarkRender"/> for bytes, with the suffix appended as its ASCII bytes.</summary>
    private static string BenchmarkBytesRender(QrRequest request, byte[] bytes, int startIndex, int count, byte[] customLogo)
    {
        var ecc = ParseEcc(request.Ecc);
        var options = StandardOptions(request);

        long bytesTotal = 0;
        var qrVersion = 0;
        var matrixSize = 0;
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            byte[] data = [.. bytes, .. Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $" #{startIndex + i + 1}"))];
            var symbol = QRCodeGenerator.Create(data, ecc, options, request.EciAssignment);
            qrVersion = symbol.Version;
            matrixSize = symbol.Size;
            bytesTotal += CreateBuilder(request, symbol, customLogo).ToByteArray().Length;
        }
        stopwatch.Stop();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"count\":{count},\"elapsedMs\":{stopwatch.Elapsed.TotalMilliseconds:F2},\"qrVersion\":{qrVersion},\"matrixSize\":{matrixSize},\"bytesTotal\":{bytesTotal}}}");
    }

    /// <summary><see cref="BenchmarkMicroEncode"/> for bytes: the same bytes every iteration, since a suffix would overflow most Micro QR payloads.</summary>
    private static string BenchmarkMicroBytesEncode(QrRequest request, byte[] bytes, int count)
    {
        RequireNoFnc1(request);
        RequireNoEciAssignment(request);
        var ecc = ParseMicroEcc(request.Ecc);
        var options = MicroOptions(request);
        var quietZone = options.QuietZoneSize;

        // M4 with quiet zone is the largest possible Micro QR matrix.
        var maxSide = 17 + 2 * quietZone;
        var moduleBuffer = ArrayPool<byte>.Shared.Rent(maxSide * maxSide);
        try
        {
            long bytesTotal = 0;
            var written = 0;
            var stopwatch = Stopwatch.StartNew();
            for (var i = 0; i < count; i++)
            {
                written = MicroQRCodeGenerator.Create(bytes, ecc, moduleBuffer, options);
                bytesTotal += written;
            }
            stopwatch.Stop();

            var matrixSize = (int)Math.Sqrt(written);
            var qrVersion = (matrixSize - 2 * quietZone - 9) / 2;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"count\":{count},\"elapsedMs\":{stopwatch.Elapsed.TotalMilliseconds:F2},\"qrVersion\":{qrVersion},\"matrixSize\":{matrixSize},\"bytesTotal\":{bytesTotal}}}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(moduleBuffer, clearArray: false);
        }
    }

    /// <summary><see cref="BenchmarkRmEncode"/> for bytes: the same bytes every iteration, as for text.</summary>
    private static string BenchmarkRmBytesEncode(QrRequest request, byte[] bytes, int count)
    {
        var ecc = ParseRmEcc(request.Ecc);
        var options = RmOptions(request);
        if (!RmQRCodeGenerator.TryGetRequiredBufferSize(bytes, ecc, out var calculated, options, request.EciAssignment))
            throw new ArgumentException($"Content does not fit any rMQR symbol under the requested constraints ({bytes.Length} bytes, ECC {ecc}, Version {(options.Version is null ? "auto" : options.Version.ToString())}, FitStrategy {options.FitStrategy}, Height {(options.Height is null ? "auto" : options.Height.ToString())}, QuietZone {options.QuietZoneSize}).", nameof(request));

        var moduleBuffer = ArrayPool<byte>.Shared.Rent(calculated.BufferSize);
        try
        {
            long bytesTotal = 0;
            var stopwatch = Stopwatch.StartNew();
            for (var i = 0; i < count; i++)
            {
                bytesTotal += RmQRCodeGenerator.Create(bytes, ecc, moduleBuffer, options, request.EciAssignment);
            }
            stopwatch.Stop();

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"count\":{count},\"elapsedMs\":{stopwatch.Elapsed.TotalMilliseconds:F2},\"symbology\":\"rmqr\",\"qrVersion\":{(int)calculated.Version},\"matrixSize\":{calculated.Width},\"matrixHeight\":{calculated.Height},\"bytesTotal\":{bytesTotal}}}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(moduleBuffer, clearArray: false);
        }
    }

    /// <summary>
    /// Tight loop over the zero-allocation Micro QR span API.
    /// Micro QR capacity is tiny (≤ 35 characters), so unlike the Standard QR benchmark the content is NOT suffixed with a unique index, appending one would overflow most payloads.
    /// </summary>
    private static string BenchmarkMicroEncode(QrRequest request, int count)
    {
        RequireNoFnc1(request);
        var ecc = ParseMicroEcc(request.Ecc);
        var version = ParseMicroVersion(request.Version);
        var quietZone = Math.Clamp(request.QuietZone, 0, 10);

        // M4 with quiet zone is the largest possible Micro QR matrix.
        var maxSide = 17 + 2 * quietZone;
        var moduleBuffer = ArrayPool<byte>.Shared.Rent(maxSide * maxSide);
        try
        {
            long bytesTotal = 0;
            var written = 0;
            var stopwatch = Stopwatch.StartNew();
            for (var i = 0; i < count; i++)
            {
                written = MicroQRCodeGenerator.Create(request.Content.AsSpan(), ecc, moduleBuffer, new MicroQRCodeGeneratorOptions { Version = version, QuietZoneSize = quietZone, AllowKanji = request.AllowKanji });
                bytesTotal += written;
            }
            stopwatch.Stop();

            var matrixSize = (int)Math.Sqrt(written);
            var qrVersion = (matrixSize - 2 * quietZone - 9) / 2;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"count\":{count},\"elapsedMs\":{stopwatch.Elapsed.TotalMilliseconds:F2},\"qrVersion\":{qrVersion},\"matrixSize\":{matrixSize},\"bytesTotal\":{bytesTotal}}}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(moduleBuffer, clearArray: false);
        }
    }

    /// <summary>
    /// Tight loop over the zero-allocation rMQR span API (same content every iteration, as for Micro QR: capacities are small).
    /// </summary>
    private static string BenchmarkRmEncode(QrRequest request, int count)
    {
        var ecc = ParseRmEcc(request.Ecc);
        var version = ParseRmVersion(request.Version);
        var fit = ParseRmFitStrategy(request.FitStrategy);
        var height = ParseRmHeight(request.Height);
        var quietZone = Math.Clamp(request.QuietZone, 0, 10);

        var rmOptions = new RmQRCodeGeneratorOptions { Version = version, FitStrategy = fit, Height = height, QuietZoneSize = quietZone, AllowKanji = request.AllowKanji, Fnc1 = ParseFnc1(request) };
        if (!RmQRCodeGenerator.TryGetRequiredBufferSize(request.Content.AsSpan(), ecc, out var calculated, rmOptions))
            throw new ArgumentException($"Content does not fit any rMQR symbol under the requested constraints ({request.Content.Length} characters, ECC {ecc}, Version {(version is null ? "auto" : version.ToString())}, FitStrategy {fit}, Height {(height is null ? "auto" : height.ToString())}, QuietZone {quietZone}).", nameof(request));

        var moduleBuffer = ArrayPool<byte>.Shared.Rent(calculated.BufferSize);
        try
        {
            long bytesTotal = 0;
            var stopwatch = Stopwatch.StartNew();
            for (var i = 0; i < count; i++)
            {
                bytesTotal += RmQRCodeGenerator.Create(request.Content.AsSpan(), ecc, moduleBuffer, rmOptions);
            }
            stopwatch.Stop();

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"count\":{count},\"elapsedMs\":{stopwatch.Elapsed.TotalMilliseconds:F2},\"symbology\":\"rmqr\",\"qrVersion\":{(int)calculated.Version},\"matrixSize\":{calculated.Width},\"matrixHeight\":{calculated.Height},\"bytesTotal\":{bytesTotal}}}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(moduleBuffer, clearArray: false);
        }
    }

    /// <summary>
    /// Tight loop over the GS1 entry points' span forms, the same text every iteration, since a suffix would make the data invalid.
    /// The data form runs once first, so that data the checks refuse reports their error rather than a size that does not fit.
    /// </summary>
    private static string BenchmarkGs1Encode(QrRequest request, int count)
    {
        if (IsMicroQR(request))
            RequireNoGs1(request);
        var text = request.Content.AsSpan();
        var link = Gs1Carrier(request) == "link";
        var stem = Gs1Stem(request);
        long bytesTotal = 0;
        if (IsRmQR(request))
        {
            _ = CreateRmData(request);
            var rmEcc = ParseRmEcc(request.Ecc);
            var rmOptions = RmOptions(request);
            var rmFits = link
                ? RmQRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(text, rmEcc, out var rmSize, rmOptions, stem)
                : RmQRCodeGenerator.TryGetRequiredBufferSizeGs1(text, rmEcc, out rmSize, rmOptions);
            if (!rmFits)
                throw new InvalidOperationException("The size form refused data the data form took.");
            var rmBuffer = ArrayPool<byte>.Shared.Rent(rmSize.BufferSize);
            try
            {
                var rmClock = Stopwatch.StartNew();
                for (var i = 0; i < count; i++)
                {
                    bytesTotal += link
                        ? RmQRCodeGenerator.CreateGs1DigitalLink(text, rmEcc, rmBuffer, rmOptions, stem)
                        : RmQRCodeGenerator.CreateGs1(text, rmEcc, rmBuffer, rmOptions);
                }
                rmClock.Stop();
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"{{\"count\":{count},\"elapsedMs\":{rmClock.Elapsed.TotalMilliseconds:F2},\"symbology\":\"rmqr\",\"qrVersion\":{(int)rmSize.Version},\"matrixSize\":{rmSize.Width},\"matrixHeight\":{rmSize.Height},\"bytesTotal\":{bytesTotal}}}");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rmBuffer, clearArray: false);
            }
        }

        _ = CreateStandardData(request);
        var ecc = ParseEcc(request.Ecc);
        var options = StandardOptions(request);
        var fits = link
            ? QRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(text, ecc, out var size, options, stem)
            : QRCodeGenerator.TryGetRequiredBufferSizeGs1(text, ecc, out size, options);
        if (!fits)
            throw new InvalidOperationException("The size form refused data the data form took.");
        var buffer = ArrayPool<byte>.Shared.Rent(size.BufferSize);
        try
        {
            var clock = Stopwatch.StartNew();
            for (var i = 0; i < count; i++)
            {
                bytesTotal += link
                    ? QRCodeGenerator.CreateGs1DigitalLink(text, ecc, buffer, options, stem)
                    : QRCodeGenerator.CreateGs1(text, ecc, buffer, options);
            }
            clock.Stop();
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"count\":{count},\"elapsedMs\":{clock.Elapsed.TotalMilliseconds:F2},\"qrVersion\":{size.Version},\"matrixSize\":{size.Size},\"bytesTotal\":{bytesTotal}}}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

    /// <summary>Full GS1 pipeline per iteration: the entry point, Skia render, PNG encode, the same text every iteration.</summary>
    private static string BenchmarkGs1Render(QrRequest request, int count, byte[] customLogo)
    {
        // CreateRmData already takes the GS1 entry points for a GS1 request.
        if (IsRmQR(request))
            return BenchmarkRmRender(request, count);
        if (IsMicroQR(request))
            RequireNoGs1(request);

        long bytesTotal = 0;
        var qrVersion = 0;
        var matrixSize = 0;
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            var data = CreateStandardData(request);
            qrVersion = data.Version;
            matrixSize = data.Size;
            bytesTotal += CreateBuilder(request, data, customLogo).ToByteArray().Length;
        }
        stopwatch.Stop();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"count\":{count},\"elapsedMs\":{stopwatch.Elapsed.TotalMilliseconds:F2},\"qrVersion\":{qrVersion},\"matrixSize\":{matrixSize},\"bytesTotal\":{bytesTotal}}}");
    }

    /// <summary>Full rMQR pipeline per iteration: encode, Skia render, PNG encode.</summary>
    private static string BenchmarkRmRender(QrRequest request, int count)
    {
        long bytesTotal = 0;
        var qrVersion = 0;
        var width = 0;
        var height = 0;
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            var data = CreateRmData(request);
            qrVersion = (int)data.Version;
            width = data.Width;
            height = data.Height;
            bytesTotal += CreateRmBuilder(request, data).ToByteArray().Length;
        }
        stopwatch.Stop();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"count\":{count},\"elapsedMs\":{stopwatch.Elapsed.TotalMilliseconds:F2},\"symbology\":\"rmqr\",\"qrVersion\":{qrVersion},\"matrixSize\":{width},\"matrixHeight\":{height},\"bytesTotal\":{bytesTotal}}}");
    }

    /// <summary>Full Micro QR pipeline per iteration: encode, Skia render, PNG encode.</summary>
    private static string BenchmarkMicroRender(QrRequest request, int count)
    {
        long bytesTotal = 0;
        var qrVersion = 0;
        var matrixSize = 0;
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            var data = CreateMicroData(request);
            qrVersion = (int)data.Version;
            matrixSize = data.Size;
            bytesTotal += CreateMicroBuilder(request, data).ToByteArray().Length;
        }
        stopwatch.Stop();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"count\":{count},\"elapsedMs\":{stopwatch.Elapsed.TotalMilliseconds:F2},\"qrVersion\":{qrVersion},\"matrixSize\":{matrixSize},\"bytesTotal\":{bytesTotal}}}");
    }

    private static QREccLevel ParseEcc(string ecc) => ecc.ToUpperInvariant() switch
    {
        "L" => QREccLevel.L,
        "M" => QREccLevel.M,
        "Q" => QREccLevel.Q,
        "H" => QREccLevel.H,
        _ => throw new ArgumentException($"Unknown ECC level '{ecc}'. Use L, M, Q or H."),
    };

    private static SKColor ParseColor(string value, SKColor fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        if (value.Equals("transparent", StringComparison.OrdinalIgnoreCase))
            return SKColors.Transparent;
        if (SKColor.TryParse(value, out var color))
            return color;
        throw new ArgumentException($"Could not parse color '{value}'. Use #RRGGBB or 'transparent'.");
    }

    private static ModuleShape CreateModuleShape(QrRequest request) => request.ModuleShape switch
    {
        "circle" => CircleModuleShape.Default,
        "rounded" => new RoundedRectangleModuleShape(Math.Clamp(request.ModuleCornerRadius, 0f, 1f)),
        _ => RectangleModuleShape.Default,
    };

    private static FinderPatternShape? CreateFinderShape(string finderShape) => finderShape switch
    {
        "rectangle" => RectangleFinderPatternShape.Default,
        "circle" => CircleFinderPatternShape.Default,
        "rounded" => RoundedRectangleFinderPatternShape.Default,
        "roundedCircle" => RoundedRectangleCircleFinderPatternShape.Default,
        _ => null, // auto: the setter is not called at all
    };

    private static GradientOptions? CreateGradient(GradientDto? gradient)
    {
        if (gradient is null || !gradient.Enabled || gradient.Colors.Length < 2)
            return null;

        var direction = Enum.TryParse<GradientDirection>(gradient.Direction, ignoreCase: true, out var parsed)
            ? parsed
            : GradientDirection.TopLeftToBottomRight;
        if (direction == GradientDirection.None)
            return null;

        var colors = new SKColor[gradient.Colors.Length];
        for (var i = 0; i < colors.Length; i++)
        {
            colors[i] = ParseColor(gradient.Colors[i], SKColors.Black);
        }
        return new GradientOptions(colors, direction);
    }

    private static IconData? CreateIcon(LogoDto? logo, byte[] customLogo)
    {
        if (logo is null)
            return null;

        var bitmap = logo.Mode switch
        {
            "default" => s_defaultLogo ??= CreateDefaultLogo(),
            "custom" => DecodeCustomLogo(customLogo),
            _ => null, // none
        };
        if (bitmap is null)
            return null;

        return IconData.FromImage(
            bitmap,
            iconSizePercent: Math.Clamp(logo.SizePercent, 1, 40),
            iconBorderWidth: Math.Clamp(logo.BorderWidth, 0, 64));
    }

    /// <summary>
    /// Decodes the uploaded logo, caching the bitmap keyed by content so slider-driven realtime regeneration does not re-decode the same image on every call.
    /// </summary>
    private static SKBitmap? DecodeCustomLogo(byte[] bytes)
    {
        if (bytes.Length == 0)
            return null;
        if (s_customLogoBitmap is not null && s_customLogoBytes is not null && bytes.AsSpan().SequenceEqual(s_customLogoBytes))
            return s_customLogoBitmap;

        var bitmap = SKBitmap.Decode(bytes)
            ?? throw new ArgumentException("Could not decode the uploaded logo image. Use PNG, JPEG or WebP.");
        s_customLogoBitmap?.Dispose();
        s_customLogoBytes = bytes;
        s_customLogoBitmap = bitmap;
        return bitmap;
    }

    /// <summary>
    /// Draws the built-in logo: an Instagram-style camera glyph on a warm-to-purple gradient rounded square.
    /// Rendered with SkiaSharp itself, so no binary asset is shipped.
    /// </summary>
    private static SKBitmap CreateDefaultLogo()
    {
        const int S = 256;
        var bitmap = new SKBitmap(new SKImageInfo(S, S, SKImageInfo.PlatformColorType, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        // Gradient rounded square (bottom-left warm to top-right purple).
        using var background = new SKPaint { IsAntialias = true };
        background.Shader = SKShader.CreateLinearGradient(
            new SKPoint(0, S),
            new SKPoint(S, 0),
            [SKColor.Parse("#FA7E1E"), SKColor.Parse("#D62976"), SKColor.Parse("#962FBF")],
            null,
            SKShaderTileMode.Clamp);
        canvas.DrawRoundRect(SKRect.Create(0, 0, S, S), S * 0.22f, S * 0.22f, background);

        // White camera outline: body, lens, flash dot.
        using var stroke = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = S * 0.055f,
            Color = SKColors.White,
            StrokeCap = SKStrokeCap.Round,
        };
        var inset = S * 0.19f;
        canvas.DrawRoundRect(SKRect.Create(inset, inset, S - 2 * inset, S - 2 * inset), S * 0.12f, S * 0.12f, stroke);
        canvas.DrawCircle(S / 2f, S / 2f, S * 0.15f, stroke);
        using var dot = new SKPaint { IsAntialias = true, Color = SKColors.White };
        canvas.DrawCircle(S * 0.685f, S * 0.315f, S * 0.033f, dot);

        return bitmap;
    }

    /// <summary>The error for the page. Data the GS1 checks refuse carries the place in the content, so the page can show the characters at fault.</summary>
    private static byte[] SerializeError(Exception ex)
    {
        var payload = ex.GetBaseException() is Gs1FormatException gs1
            ? new ErrorPayload(ToUserFacingMessage(ex), gs1.Error.Offset, gs1.Error.Length)
            : new ErrorPayload(ToUserFacingMessage(ex));
        return JsonSerializer.SerializeToUtf8Bytes(payload, PlaygroundJsonContext.Default.ErrorPayload);
    }

    /// <summary>
    /// The library's capacity errors point developers at the API ("use Standard QR (QRCodeGenerator)"). In the playground the actionable control is the Symbology selector, so the remedy is rephrased for the page.
    /// </summary>
    private static string ToUserFacingMessage(Exception ex)
    {
        return ex.GetBaseException().Message
            .Replace("use Standard QR (QRCodeGenerator)", "switch Symbology to QR Code");
    }
}

/// <summary>QR generation options passed from the page script as camelCase JSON.</summary>
public sealed record QrRequest
{
    public string Content { get; init; } = "";
    /// <summary>How <see cref="Content"/> is read: text (the text calls) or bytes (hex, written exactly as given through the bytes calls).</summary>
    public string ContentKind { get; init; } = "text";
    /// <summary>Bytes on Standard QR and rMQR only: the ECI assignment (0-999999) written as a header before the bytes, or null for none.</summary>
    public int? EciAssignment { get; init; }
    /// <summary>Symbology: "qr" (Standard QR), "microqr" (Micro QR M1-M4) or "rmqr" (rMQR R7x43-R17x139).</summary>
    public string Symbology { get; init; } = "qr";
    /// <summary>rMQR automatic fit: area (fewest modules, default), width or height.</summary>
    public string FitStrategy { get; init; } = "area";
    /// <summary>rMQR fixed symbol height in modules (7-17), or 0 for any.</summary>
    public int Height { get; init; }
    /// <summary>Error correction level: L, M, Q or H. Micro QR: EDO, L, M or Q. rMQR: M or H.</summary>
    public string Ecc { get; init; } = "M";
    /// <summary>Standard QR only: raise the ECC level above <see cref="Ecc"/> when the chosen version's spare capacity allows, without changing the version.</summary>
    public bool EccBoost { get; init; }
    /// <summary>Write Japanese text in Kanji mode where the library can (<see cref="QRCodeGeneratorOptions.AllowKanji"/> and its Micro QR and rMQR twins). Off by default, as in the library, because Android's own scanners read none of it.</summary>
    public bool AllowKanji { get; init; }
    /// <summary>FNC1 mode, Standard QR and rMQR only: none, first (GS1 data) or second (an AIM industry format named by <see cref="ApplicationIndicator"/>). The content then holds the separator as U+001D.</summary>
    public string Fnc1 { get; init; } = "none";
    /// <summary>The application indicator in second position: one or two digits for 00-99, or one letter A-Z or a-z.</summary>
    public string ApplicationIndicator { get; init; } = "";
    /// <summary>GS1 data, Standard QR and rMQR only: none (plain text), qr (a GS1 QR Code through <c>CreateGs1</c>) or link (a Digital Link URI through <c>CreateGs1DigitalLink</c>). The content is then (AI) text or an element string, and the FNC1 mode is the entry point's.</summary>
    public string Gs1 { get; init; } = "none";
    /// <summary>The Digital Link URI's stem, such as a brand's own domain. Blank writes the canonical <c>https://id.gs1.org</c>.</summary>
    public string Gs1Stem { get; init; } = "";
    /// <summary>Output image size in pixels (square for Standard / Micro QR, and the image width for rMQR, height from the symbol aspect ratio).</summary>
    public int Size { get; init; } = 512;
    /// <summary>Quiet zone in modules (0-10).</summary>
    public int QuietZone { get; init; } = 4;
    /// <summary>QR version 1-40 (Micro QR 1-4, rMQR 1-32 = R7x43..R17x139), or -1 for automatic selection.</summary>
    public int Version { get; init; } = -1;
    /// <summary>Module shape: rectangle, circle or rounded.</summary>
    public string ModuleShape { get; init; } = "rectangle";
    /// <summary>Module size as a fraction of the cell (0.5-1.0).</summary>
    public float ModuleSizePercent { get; init; } = 1.0f;
    /// <summary>Corner radius fraction for rounded modules (0.0-1.0).</summary>
    public float ModuleCornerRadius { get; init; } = 0.3f;
    /// <summary>Finder pattern shape: auto, rectangle, circle, rounded or roundedCircle.</summary>
    public string FinderShape { get; init; } = "auto";
    /// <summary>Module color as #RRGGBB.</summary>
    public string Foreground { get; init; } = "#000000";
    /// <summary>Background color as #RRGGBB or 'transparent'.</summary>
    public string Background { get; init; } = "#FFFFFF";
    public GradientDto? Gradient { get; init; }
    public LogoDto? Logo { get; init; }
}

public sealed record GradientDto
{
    public bool Enabled { get; init; }
    /// <summary>Gradient stops as #RRGGBB (2 or more).</summary>
    public string[] Colors { get; init; } = [];
    /// <summary>A <see cref="GradientDirection"/> member name.</summary>
    public string Direction { get; init; } = "TopLeftToBottomRight";
}

public sealed record LogoDto
{
    /// <summary>Logo mode: none, default (built-in camera glyph) or custom (uploaded image).</summary>
    public string Mode { get; init; } = "none";
    /// <summary>Logo size as a percentage of the QR side length (1-40).</summary>
    public int SizePercent { get; init; } = 16;
    /// <summary>Border padding around the logo in pixels (0-64).</summary>
    public int BorderWidth { get; init; } = 6;
}

/// <param name="Error">The message for the page.</param>
/// <param name="Offset">For data the GS1 checks refuse, where the fault starts in the content, else null.</param>
/// <param name="Length">For data the GS1 checks refuse, how many characters the fault spans, else null.</param>
public sealed record ErrorPayload(string Error, int? Offset = null, int? Length = null);

/// <summary>Stats of a <see cref="QrInterop.Generate"/> call, which <see cref="QrInterop.GetLastMeta"/> returns.</summary>
/// <param name="MatrixSize">The symbol's width in modules, the side for a square symbol.</param>
/// <param name="MatrixHeight">rMQR only: the symbol's height in modules.</param>
/// <param name="Encoded">For GS1 data, the text written into the symbol: the element string, or the Digital Link URI. Null for plain text.</param>
public sealed record GenerateMeta(string Symbology, int QrVersion, int MatrixSize, double TotalMs, int Bytes, int? MatrixHeight = null, string? Encoded = null);

/// <summary>One element of decoded GS1 data.</summary>
public sealed record Gs1ElementDto(string Ai, string Value);

/// <summary>Result of a <see cref="QrInterop.Decode"/> call, serialized to the page script.</summary>
/// <param name="Corners">
/// Where the symbol sits in the decoded image, as eight numbers: top-left, top-right, bottom-right
/// and bottom-left, each an x followed by a y, in the symbol's own frame. Null unless the decode
/// succeeded. A flat array rather than four objects because the page feeds it straight to a canvas
/// path, and it keeps the serializer to primitives.
/// </param>
/// <param name="ImageWidth">Pixel width of the image the decoder saw, so the page can scale the overlay to however it is displaying that image.</param>
/// <param name="ImageHeight">Pixel height of the image the decoder saw.</param>
/// <param name="Orientation">The file's EXIF orientation tag (1-8), or 1 when it has none. The decoder ignores it but a browser applies it, so the page maps <see cref="Corners"/> through it before drawing.</param>
/// <param name="Fnc1">"first" (GS1 data) or "second" (an AIM industry format) when the symbol carries FNC1, else null. Standard QR and rMQR only.</param>
/// <param name="ApplicationIndicator">In second position, the 8-bit codeword (0-99 as its value, or a letter's ASCII value plus 100), else null.</param>
/// <param name="Gs1Hri">GS1 data as (AI) text, for an element string under FNC1 in first position or a Digital Link URI without FNC1, else null.</param>
/// <param name="Gs1Elements">The same data's elements, AI and value, in the order the element string holds them.</param>
/// <param name="Gs1Error">Under FNC1 in first position, why the text does not read as GS1 data, else null.</param>
/// <param name="Gs1Check">For GS1 data that reads, why its element string fails the GS1 checks, with the place in the element string, else null.</param>
/// <param name="BytesHex">The bytes the symbol carries, as uppercase hex with nothing between the digits, or null when they were not read. A success with no <see cref="Text"/> holds content that is not text the library can convert.</param>
/// <param name="EciAssignment">The ECI assignment the content is declared under, or null for none. Micro QR has no ECI and reports none.</param>
public sealed record DecodePayload(
    bool Ok,
    string? Text,
    string Status,
    string? Symbology,
    int QrVersion,
    string? Ecc,
    int MaskPattern,
    int ErrorsCorrected,
    double TotalMs,
    float[]? Corners = null,
    int ImageWidth = 0,
    int ImageHeight = 0,
    int Orientation = 1,
    string? Fnc1 = null,
    int? ApplicationIndicator = null,
    string? Gs1Hri = null,
    Gs1ElementDto[]? Gs1Elements = null,
    string? Gs1Error = null,
    string? Gs1Check = null,
    string? BytesHex = null,
    int? EciAssignment = null);

/// <summary>Result of a <see cref="QrInterop.DecodeAll"/> call, serialized to the page script.</summary>
/// <param name="Ok">Whether any symbol decoded.</param>
/// <param name="Symbols">Each symbol read, in the order found, shaped as a <see cref="QrInterop.Decode"/> success. Its time is the whole call's.</param>
/// <param name="TotalMs">The whole call's time.</param>
/// <param name="ImageWidth">Pixel width of the image the decoder saw.</param>
/// <param name="ImageHeight">Pixel height of the image the decoder saw.</param>
/// <param name="Orientation">The file's EXIF orientation tag (1-8), or 1 when it has none, as on <see cref="DecodePayload"/>.</param>
public sealed record DecodeSymbolsPayload(
    bool Ok,
    DecodePayload[] Symbols,
    double TotalMs,
    int ImageWidth,
    int ImageHeight,
    int Orientation);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(QrRequest))]
[JsonSerializable(typeof(ErrorPayload))]
[JsonSerializable(typeof(DecodePayload))]
[JsonSerializable(typeof(DecodeSymbolsPayload))]
[JsonSerializable(typeof(PhotoRequest))]
[JsonSerializable(typeof(PhotoPayload))]
[JsonSerializable(typeof(GenerateMeta))]
internal sealed partial class PlaygroundJsonContext : JsonSerializerContext;
