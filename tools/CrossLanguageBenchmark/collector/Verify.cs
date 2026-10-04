using System.Text;
using System.Text.Json;
using FeatherQR;

/// <summary>
/// Checks the result a CLI printed before timing: a decode must return the payload, and an encoded matrix must decode to it through FeatherQR's matrix decoder in the pinned version and level.
/// </summary>
internal static class Verify
{
    /// <summary>Null when the result is right; otherwise why the process's timings do not count.</summary>
    public static string? Check(ManifestEntry entry, JsonElement output)
    {
        var status = output.TryGetProperty("status", out var s) ? s.GetString() : null;
        if (status != "ok")
            return status ?? "no status";

        if (entry.Op != "encode")
        {
            var text = output.TryGetProperty("text", out var t) ? t.GetString() : null;
            return text == entry.ExpectedHex ? null : $"decoded {Describe(text)}";
        }

        if (!output.TryGetProperty("matrix", out var matrix))
            return "no matrix";
        var width = matrix.GetProperty("width").GetInt32();
        var height = matrix.GetProperty("height").GetInt32();
        var rows = matrix.GetProperty("rows").EnumerateArray().Select(r => r.GetString() ?? "").ToArray();
        if (rows.Length != height || rows.Any(r => r.Length != width))
            return $"matrix rows do not match {width}x{height}";

        // The library's quiet zone, if any, is dropped: every symbology's outermost rows and columns hold dark modules.
        int top = height, bottom = -1, left = width, right = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (rows[y][x] != '1')
                    continue;
                (top, bottom, left, right) = (Math.Min(top, y), Math.Max(bottom, y), Math.Min(left, x), Math.Max(right, x));
            }
        }
        if (bottom < 0)
            return "matrix has no dark module";
        var (w, h) = (right - left + 1, bottom - top + 1);
        var modules = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
                modules[y * w + x] = rows[top + y][left + x] == '1' ? (byte)1 : (byte)0;
        }

        string decoded;
        string version, ecc;
        switch (entry.Symbology)
        {
            case "qr":
                {
                    if (w != h || !QRCodeDecoder.TryDecode(modules, w, out decoded, out var info))
                        return $"{w}x{h} matrix does not decode";
                    (version, ecc) = (info.Version.ToString(), info.EccLevel.ToString());
                    break;
                }
            case "microqr":
                {
                    if (w != h || !MicroQRCodeDecoder.TryDecode(modules, w, out decoded, out var info))
                        return $"{w}x{h} matrix does not decode";
                    (version, ecc) = (info.Version.ToString(), info.EccLevel.ToString());
                    break;
                }
            case "rmqr":
                {
                    if (!RmQRCodeDecoder.TryDecode(modules, w, h, out decoded, out var info))
                        return $"{w}x{h} matrix does not decode";
                    (version, ecc) = (info.Version.ToString(), info.EccLevel.ToString());
                    break;
                }
            default:
                return $"unknown symbology {entry.Symbology}";
        }

        var hex = Convert.ToHexString(Encoding.UTF8.GetBytes(decoded));
        if (hex != entry.ExpectedHex)
            return $"matrix decodes to {Describe(hex)}";
        if (version != entry.Version || ecc != entry.Ecc)
            return $"encoded {version}-{ecc}, pinned {entry.Version}-{entry.Ecc}";
        return null;
    }

    private static string Describe(string? hex)
    {
        if (hex is null)
            return "nothing";
        try
        {
            return $"\"{Encoding.UTF8.GetString(Convert.FromHexString(hex))}\"";
        }
        catch (FormatException)
        {
            return $"non-hex text {hex}";
        }
    }
}
