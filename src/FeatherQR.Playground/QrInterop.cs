using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.Json.Serialization;
using FeatherQR.SkiaSharp;
using SkiaSharp;

namespace FeatherQR.Playground;

/// <summary>
/// Browser-callable QR generation API. Invoked by the host script after <c>runMain()</c> completes.
/// <para>
/// CRITICAL: Every <c>[JSExport]</c> method MUST catch all exceptions internally.
/// An unhandled exception propagating through the interop boundary causes the Mono WASM
/// runtime to abort (exit code 1). Once aborted, the runtime cannot be restarted without
/// a full page reload, and all subsequent calls fail with
/// "Assert failed: .NET runtime already exited with 1".
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
    /// Stats of the most recent successful <see cref="Generate"/> call as a JSON string:
    /// <c>{"qrVersion":N,"matrixSize":N,"totalMs":N,"bytes":N}</c>.
    /// </summary>
    [JSExport]
    public static string GetLastMeta() => s_lastMeta;

    /// <summary>
    /// Generates a QR code PNG from JSON options (see <see cref="QrRequest"/>).
    /// Returns PNG bytes on success (first byte 0x89), or UTF-8 JSON
    /// <c>{"error":"..."}</c> on failure (first byte 0x7B), the JS side
    /// distinguishes the two by the first byte.
    /// </summary>
    /// <param name="optionsJson">Serialized <see cref="QrRequest"/> (camelCase).</param>
    /// <param name="customLogo">Uploaded logo image bytes; empty unless logo mode is "custom".</param>
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
    /// Returns UTF-8 SVG bytes on success (first byte '&lt;' 0x3C), or UTF-8 JSON
    /// <c>{"error":"..."}</c> on failure (first byte 0x7B), the JS side
    /// distinguishes the two by the first byte.
    /// </summary>
    /// <param name="optionsJson">Serialized <see cref="QrRequest"/> (camelCase).</param>
    /// <param name="customLogo">Uploaded logo image bytes; empty unless logo mode is "custom".</param>
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
    /// Decodes a QR code from encoded image bytes (PNG/JPEG/WebP) and returns the
    /// result as a JSON string:
    /// <c>{"ok":true,"text":"...","qrVersion":N,"ecc":"M","maskPattern":N,"errorsCorrected":N,"totalMs":N}</c>
    /// on success, <c>{"ok":false,"status":"NotDetected","totalMs":N}</c> when no QR
    /// decodes, or <c>{"error":"..."}</c> on unexpected failure.
    /// <para>
    /// Uses the library's built-in image decoders, Standard QR first, then Micro QR, then
    /// rMQR (<c>"symbology"</c> reports which one matched; rMQR has a single fixed mask, so
    /// <c>"maskPattern"</c> is -1 for it): clean, screen-rendered images
    /// (arbitrary rotation, mirroring and mild perspective included). Heavily stylized codes
    /// (low-contrast colors, inverted palettes, strong decoration) may report
    /// NotDetected even when a computer-vision grade phone scanner reads them.
    /// </para>
    /// </summary>
    /// <param name="imageBytes">Encoded image file bytes.</param>
    [JSExport]
    public static string Decode(byte[] imageBytes)
    {
        try
        {
            if (imageBytes.Length == 0)
                throw new ArgumentException("Image is empty.");

            var stopwatch = Stopwatch.StartNew();
            using var bitmap = SKBitmap.Decode(imageBytes)
                ?? throw new ArgumentException("The file is not a decodable image (PNG/JPEG/WebP).");

            var success = QRCodeDecoder.TryDecode(bitmap, out var text, out var info);
            if (success)
            {
                stopwatch.Stop();
                var payload = new DecodePayload(
                    Ok: true,
                    Text: text,
                    Status: info.Status.ToString(),
                    Symbology: "qr",
                    QrVersion: info.Version,
                    Ecc: info.EccLevel.ToString(),
                    MaskPattern: info.MaskPattern,
                    ErrorsCorrected: info.ErrorsCorrected,
                    TotalMs: Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1));
                return JsonSerializer.Serialize(payload, PlaygroundJsonContext.Default.DecodePayload);
            }

            // Standard QR failed: try Micro QR, then rMQR (separate, explicitly-typed detectors)
            if (MicroQRCodeDecoder.TryDecode(bitmap, out var microText, out var microInfo))
            {
                stopwatch.Stop();
                var microPayload = new DecodePayload(
                    Ok: true,
                    Text: microText,
                    Status: microInfo.Status.ToString(),
                    Symbology: "microqr",
                    QrVersion: (int)microInfo.Version,
                    Ecc: microInfo.EccLevel.ToString(),
                    MaskPattern: microInfo.MaskPattern,
                    ErrorsCorrected: microInfo.ErrorsCorrected,
                    TotalMs: Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1));
                return JsonSerializer.Serialize(microPayload, PlaygroundJsonContext.Default.DecodePayload);
            }

            var rmSuccess = RmQRCodeDecoder.TryDecode(bitmap, out var rmText, out var rmInfo);
            stopwatch.Stop();

            // rMQR has a single fixed mask, so no mask pattern is reported (-1).
            var resultPayload = rmSuccess
                ? new DecodePayload(
                    Ok: true,
                    Text: rmText,
                    Status: rmInfo.Status.ToString(),
                    Symbology: "rmqr",
                    QrVersion: (int)rmInfo.Version,
                    Ecc: rmInfo.EccLevel.ToString(),
                    MaskPattern: -1,
                    ErrorsCorrected: rmInfo.ErrorsCorrected,
                    TotalMs: Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1))
                : new DecodePayload(
                    Ok: false,
                    Text: null,
                    Status: info.Status.ToString(),
                    Symbology: null,
                    QrVersion: info.Version,
                    Ecc: null,
                    MaskPattern: info.MaskPattern,
                    ErrorsCorrected: info.ErrorsCorrected,
                    TotalMs: Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1));
            return JsonSerializer.Serialize(resultPayload, PlaygroundJsonContext.Default.DecodePayload);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new ErrorPayload(ToUserFacingMessage(ex)), PlaygroundJsonContext.Default.ErrorPayload);
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

            // matrixSize doubles as the width so older page scripts keep working; matrixHeight is the rectangular extra.
            s_lastMeta = string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"symbology\":\"rmqr\",\"qrVersion\":{(int)rmData.Version},\"matrixSize\":{rmData.Width},\"matrixHeight\":{rmData.Height},\"totalMs\":{stopwatch.Elapsed.TotalMilliseconds:F1},\"bytes\":{rmBytes.Length}}}");
            return rmBytes;
        }

        if (IsMicroQR(request))
        {
            var microData = CreateMicroData(request);
            var microBytes = CreateMicroBuilder(request, microData).ToByteArray();
            stopwatch.Stop();

            s_lastMeta = string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"symbology\":\"microqr\",\"qrVersion\":{(int)microData.Version},\"matrixSize\":{microData.Size},\"totalMs\":{stopwatch.Elapsed.TotalMilliseconds:F1},\"bytes\":{microBytes.Length}}}");
            return microBytes;
        }

        var data = CreateStandardData(request);

        var bytes = CreateBuilder(request, data, customLogo).ToByteArray();
        stopwatch.Stop();

        s_lastMeta = string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"symbology\":\"qr\",\"qrVersion\":{data.Version},\"matrixSize\":{data.Size},\"totalMs\":{stopwatch.Elapsed.TotalMilliseconds:F1},\"bytes\":{bytes.Length}}}");
        return bytes;
    }

    private static bool IsMicroQR(QrRequest request)
        => string.Equals(request.Symbology, "microqr", StringComparison.OrdinalIgnoreCase);

    private static bool IsRmQR(QrRequest request)
        => string.Equals(request.Symbology, "rmqr", StringComparison.OrdinalIgnoreCase);

    private static QRCodeData CreateStandardData(QrRequest request)
    {
        return QRCodeGenerator.Create(request.Content.AsSpan(), ParseEcc(request.Ecc), new QRCodeGeneratorOptions
        {
            Version = ParseVersionRange(request.Version),
            QuietZoneSize = Math.Clamp(request.QuietZone, 0, 10),
            BoostEccLevel = request.EccBoost,
        });
    }

    private static RmQRCodeData CreateRmData(QrRequest request)
    {
        return RmQRCodeGenerator.Create(request.Content.AsSpan(), ParseRmEcc(request.Ecc), new RmQRCodeGeneratorOptions
        {
            Version = ParseRmVersion(request.Version),
            FitStrategy = ParseRmFitStrategy(request.FitStrategy),
            Height = ParseRmHeight(request.Height),
            QuietZoneSize = Math.Clamp(request.QuietZone, 0, 10),
        });
    }

    /// <summary>
    /// Builds the rMQR image builder from request options. The requested size is
    /// the image width; the height follows the rectangular symbol. rMQR has no icon
    /// overlay or finder pattern shape options, the page hides those controls.
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
        return MicroQRCodeGenerator.Create(
            request.Content.AsSpan(),
            ParseMicroEcc(request.Ecc),
            new MicroQRCodeGeneratorOptions
            {
                Version = ParseMicroVersion(request.Version),
                QuietZoneSize = Math.Clamp(request.QuietZone, 0, 10),
            });
    }

    /// <summary>
    /// Builds the Micro QR image builder from request options. Micro QR has no icon
    /// overlay or finder pattern shape options (single finder, no ECC headroom), so
    /// those request fields are ignored, the page hides the controls.
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
    /// The request carries -1 for "pick the smallest version that fits", which
    /// <see cref="QRVersionRange"/> spells as <see cref="QRVersionRange.Any"/>;
    /// it deliberately rejects -1 so a defaulted field cannot pass for automatic.
    /// </summary>
    private static QRVersionRange ParseVersionRange(int version)
        => version == -1 ? QRVersionRange.Any : QRVersionRange.Exactly(version);

    /// <summary>Builds the image builder from request options; shared by preview and benchmark rendering.</summary>
    private static QRCodeImageBuilder CreateBuilder(QrRequest request, QRCodeData data, byte[] customLogo)
    {
        var size = Math.Clamp(request.Size, 64, 2048);
        return new QRCodeImageBuilder(data)
            .WithSize(size, size)
            .WithColors(
                ParseColor(request.Foreground, SKColors.Black),
                ParseColor(request.Background, SKColors.White))
            .WithModuleShape(CreateModuleShape(request), Math.Clamp(request.ModuleSizePercent, 0.5f, 1.0f))
            .WithFinderPatternShape(CreateFinderShape(request.FinderShape))
            .WithGradient(CreateGradient(request.Gradient))
            .WithIcon(CreateIcon(request.Logo, customLogo));
    }

    /// <summary>
    /// Runs one benchmark batch and returns stats as a JSON string:
    /// <c>{"count":N,"elapsedMs":N,"qrVersion":N,"matrixSize":N,"bytesTotal":N}</c>,
    /// or <c>{"error":"..."}</c> on failure. The page script chains batches so the UI
    /// stays responsive and can show progress / cancel.
    /// <para>
    /// Content is made unique per iteration by appending <c>" #&lt;index+1&gt;"</c>.
    /// Mode <c>encode</c> exercises the allocation-free
    /// <see cref="QRCodeGenerator.Create(ReadOnlySpan{char}, QREccLevel, Span{byte}, bool, EciMode, int, int)"/>
    /// overload only; mode <c>render</c> runs the full pipeline (encode + Skia render + PNG encode)
    /// with the current visual options.
    /// </para>
    /// </summary>
    /// <param name="optionsJson">Serialized <see cref="QrRequest"/> (camelCase).</param>
    /// <param name="mode">"encode" or "render".</param>
    /// <param name="startIndex">Global index of the first iteration in this batch.</param>
    /// <param name="count">Iterations to run in this batch.</param>
    /// <param name="customLogo">Uploaded logo bytes for render mode; empty otherwise.</param>
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
    /// Tight loop over the zero-allocation span API. The per-iteration text is composed in a
    /// pooled char buffer (no string allocation) and the module matrix is written into a
    /// pooled byte buffer sized for QR version 40, so the loop itself allocates nothing.
    /// </summary>
    private static string BenchmarkEncode(QrRequest request, int startIndex, int count)
    {
        var ecc = ParseEcc(request.Ecc);
        var quietZone = Math.Clamp(request.QuietZone, 0, 10);
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
                written = QRCodeGenerator.Create(text, ecc, moduleBuffer, new QRCodeGeneratorOptions { Version = ParseVersionRange(request.Version), QuietZoneSize = quietZone });
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

        long bytesTotal = 0;
        var qrVersion = 0;
        var matrixSize = 0;
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            var text = string.Create(CultureInfo.InvariantCulture, $"{request.Content} #{startIndex + i + 1}");
            var data = QRCodeGenerator.Create(text.AsSpan(), ecc, new QRCodeGeneratorOptions { Version = ParseVersionRange(request.Version), QuietZoneSize = quietZone });
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
    /// Tight loop over the zero-allocation Micro QR span API. Micro QR capacity is
    /// tiny (≤ 35 characters), so unlike the Standard QR benchmark the content is
    /// NOT suffixed with a unique index, appending one would overflow most payloads.
    /// </summary>
    private static string BenchmarkMicroEncode(QrRequest request, int count)
    {
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
                written = MicroQRCodeGenerator.Create(request.Content.AsSpan(), ecc, moduleBuffer, new MicroQRCodeGeneratorOptions { Version = version, QuietZoneSize = quietZone });
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
    /// Tight loop over the zero-allocation rMQR span API (same content every iteration,
    /// as for Micro QR: capacities are small).
    /// </summary>
    private static string BenchmarkRmEncode(QrRequest request, int count)
    {
        var ecc = ParseRmEcc(request.Ecc);
        var version = ParseRmVersion(request.Version);
        var fit = ParseRmFitStrategy(request.FitStrategy);
        var height = ParseRmHeight(request.Height);
        var quietZone = Math.Clamp(request.QuietZone, 0, 10);

        var rmOptions = new RmQRCodeGeneratorOptions { Version = version, FitStrategy = fit, Height = height, QuietZoneSize = quietZone };
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

    private static ModuleShape? CreateModuleShape(QrRequest request) => request.ModuleShape switch
    {
        "circle" => CircleModuleShape.Default,
        "rounded" => new RoundedRectangleModuleShape(Math.Clamp(request.ModuleCornerRadius, 0f, 1f)),
        _ => null, // rectangle (default)
    };

    private static FinderPatternShape? CreateFinderShape(string finderShape) => finderShape switch
    {
        "rectangle" => RectangleFinderPatternShape.Default,
        "circle" => CircleFinderPatternShape.Default,
        "rounded" => RoundedRectangleFinderPatternShape.Default,
        "roundedCircle" => RoundedRectangleCircleFinderPatternShape.Default,
        _ => null, // auto: standard pattern, or module shape when one is set
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
    /// Decodes the uploaded logo, caching the bitmap keyed by content so slider-driven
    /// realtime regeneration does not re-decode the same image on every call.
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
    /// Draws the built-in logo: an Instagram-style camera glyph on a warm-to-purple
    /// gradient rounded square. Rendered with SkiaSharp itself, so no binary asset is shipped.
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

    private static byte[] SerializeError(Exception ex)
    {
        return JsonSerializer.SerializeToUtf8Bytes(new ErrorPayload(ToUserFacingMessage(ex)), PlaygroundJsonContext.Default.ErrorPayload);
    }

    /// <summary>
    /// The library's capacity errors point developers at the API ("use Standard QR
    /// (QRCodeGenerator)"); in the playground the actionable control is the
    /// Symbology selector, so the remedy is rephrased for the page.
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
    /// <summary>Symbology: "qr" (Standard QR), "microqr" (Micro QR M1-M4) or "rmqr" (rMQR R7x43-R17x139).</summary>
    public string Symbology { get; init; } = "qr";
    /// <summary>rMQR automatic fit: area (fewest modules, default), width or height.</summary>
    public string FitStrategy { get; init; } = "area";
    /// <summary>rMQR fixed symbol height in modules (7-17), or 0 for any.</summary>
    public int Height { get; init; }
    /// <summary>Error correction level: L, M, Q or H (Micro QR: EDO, L, M or Q; rMQR: M or H).</summary>
    public string Ecc { get; init; } = "M";
    /// <summary>Standard QR only: raise the ECC level above <see cref="Ecc"/> when the chosen version's spare capacity allows, without changing the version.</summary>
    public bool EccBoost { get; init; }
    /// <summary>Output image size in pixels (square for Standard / Micro QR; the image width for rMQR, height from the symbol aspect ratio).</summary>
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

public sealed record ErrorPayload(string Error);

/// <summary>Result of a <see cref="QrInterop.Decode"/> call, serialized to the page script.</summary>
public sealed record DecodePayload(
    bool Ok,
    string? Text,
    string Status,
    string? Symbology,
    int QrVersion,
    string? Ecc,
    int MaskPattern,
    int ErrorsCorrected,
    double TotalMs);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(QrRequest))]
[JsonSerializable(typeof(ErrorPayload))]
[JsonSerializable(typeof(DecodePayload))]
internal sealed partial class PlaygroundJsonContext : JsonSerializerContext;
