using System.Text;

namespace FeatherQR.Tests;

public static class VisualCompatibilityTestHelper
{
    private const string ActualSuffix = ".actual.pixels";

    /// <summary>
    /// Where a failed comparison writes what it rendered: beside the sample, under the same
    /// name with <c>.actual.pixels</c> for <c>.pixels</c>.
    /// </summary>
    public static string ActualPathOf(string samplePath) => Path.ChangeExtension(samplePath, null) + ActualSuffix;

    /// <summary>
    /// Load pixel data with metadata.
    /// </summary>
    public static (byte[] pixels, int size) LoadPixelData(string path)
    {
        using var fs = File.OpenRead(path);
        using var reader = new BinaryReader(fs);

        // Read metadata
        var size = reader.ReadInt32();
        var pixelCount = reader.ReadInt32();

        // Read pixel data
        var pixels = reader.ReadBytes(pixelCount);

        return (pixels, size);
    }

    public static void GenerateGoldenFilesReport(string directoryName)
    {
        // The search also finds what a failed comparison left behind (ActualPathOf). That file is
        // not a golden one, and its name does not parse as one, so it is left out.
        var goldenFiles = Directory.GetFiles(directoryName, "*.pixels")
            .Where(f => !f.EndsWith(ActualSuffix, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Group files by test category
        var testGroups = goldenFiles
            .Select(f => new
            {
                Path = f,
                FileName = Path.GetFileName(f),
                Data = LoadPixelData(f),
                Category = CategorizeTest(Path.GetFileName(f))
            })
            .GroupBy(x => x.Category)
            .OrderBy(g => g.Key);

        var html = new StringBuilder();

        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html><head>");
        html.AppendLine("<meta charset='utf-8'>");
        html.AppendLine("<title>QR Code Golden Files Report</title>");
        html.AppendLine("<style>");
        html.AppendLine("body { font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; margin: 20px; background: #f5f5f5; }");
        html.AppendLine("h1 { color: #333; border-bottom: 3px solid #0066cc; padding-bottom: 10px; }");
        html.AppendLine("h2 { color: #0066cc; margin-top: 30px; border-bottom: 2px solid #ccc; padding-bottom: 5px; }");
        html.AppendLine("table { width: 100%; border-collapse: collapse; background: white; margin-bottom: 30px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }");
        html.AppendLine("th { background: #0066cc; color: white; padding: 12px; text-align: left; font-weight: 600; }");
        html.AppendLine("td { padding: 10px; border-bottom: 1px solid #ddd; }");
        html.AppendLine("tr:hover { background: #f9f9f9; }");
        html.AppendLine(".qr-canvas { border: 1px solid #ccc; image-rendering: pixelated; display: block; }");
        html.AppendLine(".filename { font-family: monospace; color: #666; font-size: 0.9em; }");
        html.AppendLine(".metadata { color: #888; font-size: 0.85em; }");
        html.AppendLine(".version { font-weight: bold; color: #0066cc; }");
        html.AppendLine(".summary { background: #e3f2fd; padding: 15px; border-radius: 5px; margin-bottom: 20px; }");
        html.AppendLine(".summary-item { display: inline-block; margin-right: 30px; }");
        html.AppendLine(".summary-label { font-weight: bold; color: #0066cc; }");
        html.AppendLine("</style>");
        html.AppendLine("</head><body>");

        html.AppendLine("<h1>QR Code Golden Files Report</h1>");

        // Summary section
        html.AppendLine("<div class='summary'>");
        html.AppendLine($"<div class='summary-item'><span class='summary-label'>Total Files:</span> {goldenFiles.Length}</div>");
        html.AppendLine($"<div class='summary-item'><span class='summary-label'>Categories:</span> {testGroups.Count()}</div>");
        html.AppendLine($"<div class='summary-item'><span class='summary-label'>Generated:</span> {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>");
        html.AppendLine("</div>");

        // Table of contents
        html.AppendLine("<h2>Table of Contents</h2>");
        html.AppendLine("<ul>");
        foreach (var group in testGroups)
        {
            var count = group.Count();
            html.AppendLine($"<li><a href='#{GetAnchor(group.Key)}'>{group.Key}</a> ({count} files)</li>");
        }
        html.AppendLine("</ul>");

        // Each category as a table
        foreach (var group in testGroups)
        {
            html.AppendLine($"<h2 id='{GetAnchor(group.Key)}'>{group.Key}</h2>");
            html.AppendLine("<table>");
            html.AppendLine("<thead>");
            html.AppendLine("<tr>");
            html.AppendLine("<th style='width: 150px;'>QR Code</th>");
            html.AppendLine("<th>Content</th>");
            html.AppendLine("<th style='width: 100px;'>ECC Level</th>");
            html.AppendLine("<th style='width: 100px;'>ECI Mode</th>");
            html.AppendLine("<th style='width: 100px;'>Version</th>");
            html.AppendLine("<th style='width: 80px;'>Size</th>");
            html.AppendLine("<th style='width: 200px;'>File</th>");
            html.AppendLine("</tr>");
            html.AppendLine("</thead>");
            html.AppendLine("<tbody>");

            foreach (var item in group.OrderBy(x => x.FileName))
            {
                var (pixels, size) = item.Data;
                var version = (size - 8 - 21) / 4 + 1;
                var canvasId = Path.GetFileNameWithoutExtension(item.FileName).Replace(".", "_");
                var parsed = ParseFileName(item.FileName);

                html.AppendLine("<tr>");

                // QR Code image
                html.AppendLine("<td style='text-align: center;'>");
                html.AppendLine($"<canvas id='{canvasId}' class='qr-canvas' width='{size * 3}' height='{size * 3}'></canvas>");
                html.AppendLine("</td>");

                // Content
                html.AppendLine("<td>");
                html.AppendLine($"<div style='font-weight: bold;'>{EscapeHtml(parsed.Content)}</div>");
                html.AppendLine($"<div class='metadata'>{parsed.Content.Length} chars</div>");
                html.AppendLine("</td>");

                // ECC Level
                html.AppendLine($"<td>{parsed.EccLevel}</td>");

                // ECI Mode
                html.AppendLine("<td>");
                html.AppendLine($"{parsed.EciModeName}");
                html.AppendLine($"<div class='metadata'>({parsed.EciModeValue})</div>");
                html.AppendLine("</td>");

                // Version
                html.AppendLine($"<td><span class='version'>V{version}</span></td>");

                // Size
                html.AppendLine($"<td>{size}×{size}</td>");

                // Filename
                html.AppendLine($"<td><div class='filename'>{item.FileName}</div></td>");

                html.AppendLine("</tr>");

                // JavaScript to render QR code
                html.AppendLine("<script>");
                html.AppendLine("(function(){");
                html.AppendLine($"  var canvas = document.getElementById('{canvasId}');");
                html.AppendLine("  if (!canvas) return;");
                html.AppendLine("  var ctx = canvas.getContext('2d');");
                html.AppendLine($"  var pixels = [{string.Join(",", pixels)}];");
                html.AppendLine($"  var size = {size};");
                html.AppendLine("  var scale = 3;");
                html.AppendLine("  for(var y=0; y<size; y++){");
                html.AppendLine("    for(var x=0; x<size; x++){");
                html.AppendLine("      var idx = y*size + x;");
                html.AppendLine("      ctx.fillStyle = pixels[idx] === 0 ? '#000' : '#fff';");
                html.AppendLine("      ctx.fillRect(x*scale, y*scale, scale, scale);");
                html.AppendLine("    }");
                html.AppendLine("  }");
                html.AppendLine("})();");
                html.AppendLine("</script>");
            }

            html.AppendLine("</tbody>");
            html.AppendLine("</table>");
        }

        html.AppendLine("</body></html>");

        var reportPath = Path.Combine(directoryName, "report.html");
        File.WriteAllText(reportPath, html.ToString());

        Console.WriteLine($"Report generated: {reportPath}");
    }

    // Helper methods for report generation

    private static string CategorizeTest(string filename)
    {
        if (filename.StartsWith("empty_"))
            return "1. Empty String Tests";
        if (filename.Contains("_eci0."))
            return "2. Default ECI Mode Tests";
        if (filename.Contains("_eci26."))
            return "3. UTF-8 Tests";
        if (filename.Contains("_eci3."))
            return "4. ISO-8859-1 Tests";
        if (filename.Contains("_eci4."))
            return "5. ISO-8859-2 Tests";
        if (filename.StartsWith("1111111") || filename.StartsWith("AAAAAAA") || filename.StartsWith("9999999"))
            return "6. Version Boundary Tests";
        if (filename.Contains("\t") || filename.Contains("\n") || filename == " _")
            return "7. Edge Cases (Special Characters)";

        return "8. Other Tests";
    }

    private static string GetAnchor(string category)
    {
        return category.Replace(" ", "-").Replace(".", "").ToLower();
    }

    private static (string Content, string EccLevel, string EciModeName, int EciModeValue) ParseFileName(string filename)
    {
        // Format: {content}_ecc{L|M|Q|H}_eci{number}.pixels
        var parts = filename.Replace(".pixels", "").Split("_ecc");
        var content = parts[0];

        var eccParts = parts.Length > 1 ? parts[1].Split("_eci") : new[] { "?", "0" };
        var eccLevel = eccParts[0];
        var eciModeValue = eccParts.Length > 1 ? int.Parse(eccParts[1]) : 0;

        var eciModeName = eciModeValue switch
        {
            0 => "Default",
            3 => "ISO-8859-1",
            4 => "ISO-8859-2",
            5 => "ISO-8859-5",
            7 => "ISO-8859-7",
            9 => "ISO-8859-15",
            20 => "Shift_JIS",
            26 => "UTF-8",
            _ => $"ECI-{eciModeValue}"
        };

        // Decode content
        if (content == "empty")
            content = "(empty)";
        else if (content.Contains("_"))
        {
            // Handle special characters that were sanitized
            content = content.Replace("_", " ");
        }

        return (content, eccLevel, eciModeName, eciModeValue);
    }

    private static string EscapeHtml(string text)
    {
        if (string.IsNullOrEmpty(text) || text == "(empty)")
            return "<i>(empty string)</i>";

        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;")
            .Replace("\t", "<span style='color:#999;'>[TAB]</span>")
            .Replace("\n", "<span style='color:#999;'>[LF]</span>")
            .Replace("\r", "<span style='color:#999;'>[CR]</span>");
    }
}
