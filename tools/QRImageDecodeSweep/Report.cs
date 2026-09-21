using System.Globalization;
using System.Text;

namespace QRImageDecodeSweep;

/// <summary>
/// The gap table as Markdown: one line per row group, this library's reads per column group, then all three readers, the gap (zxing-cpp reads the image and this library does not get through it) and the reverse.
/// An image this library decodes into another text, or whose content it refuses, got through the image; it is counted apart as content and is not gap.
/// </summary>
internal static class Report
{
    /// <param name="rowColumn">Index of the key column that names a table line (kind, or sample set).</param>
    /// <param name="splitColumn">Index of the key column this library's reads are split by (encoder, or rotation).</param>
    public static string Table(string title, IReadOnlyList<ResultRow> rows, int rowColumn, int splitColumn, bool withZXingNet)
    {
        var failed = rows.Where(static r => IsFailure(r.Status)).ToList();
        var read = rows.Where(static r => !IsFailure(r.Status)).ToList();
        // This library's own symbols first where the split is by encoder; the sort is stable
        var splits = read.Select(r => r.Key[splitColumn]).Distinct().OrderBy(static s => s != "FeatherQR").ToList();
        var sb = new StringBuilder();

        sb.AppendLine(CultureInfo.InvariantCulture, $"## {title}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{read.Count:N0} images, {read.Count(static r => r.Misread):N0} decoded with another text, {failed.Count:N0} not produced.");
        sb.AppendLine();
        sb.Append("| | ").AppendJoin(" | ", splits).Append(" | FeatherQR | zxing-cpp | ");
        if (withZXingNet)
            sb.Append("ZXing.Net | ");
        var withContent = read.Any(IsContent);
        sb.Append("Gap | Reverse |");
        sb.AppendLine(withContent ? " Content |" : "");
        sb.Append("|---|").AppendJoin("", splits.Select(static _ => "---|")).Append("---|---|---|---|");
        sb.Append(withZXingNet ? "---|" : "");
        sb.AppendLine(withContent ? "---|" : "");

        foreach (var group in read.GroupBy(r => r.Key[rowColumn]).OrderBy(static g => g.Key, StringComparer.Ordinal))
            AppendLine(sb, group.Key, [.. group], splits, splitColumn, withZXingNet, withContent);
        AppendLine(sb, "**All**", read, splits, splitColumn, withZXingNet, withContent);

        var statuses = read.Where(static r => !r.FeatherQr).GroupBy(r => r.Key[rowColumn]).OrderBy(static g => g.Key, StringComparer.Ordinal).ToList();
        if (statuses.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Where FeatherQR stopped:");
            sb.AppendLine();
            foreach (var group in statuses)
            {
                var counts = group.GroupBy(static r => r.Status).OrderByDescending(static g => g.Count()).Select(static g => $"{g.Key} {g.Count():N0}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {group.Key}: {string.Join(", ", counts)}");
            }
        }

        if (failed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Not produced:");
            sb.AppendLine();
            foreach (var group in failed.GroupBy(r => $"{r.Key[splitColumn]}, {r.Key[rowColumn]}: {r.Status}").OrderBy(static g => g.Key, StringComparer.Ordinal))
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {group.Key} ({group.Count():N0})");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    private static void AppendLine(StringBuilder sb, string name, List<ResultRow> rows, List<string> splits, int splitColumn, bool withZXingNet, bool withContent)
    {
        sb.Append(CultureInfo.InvariantCulture, $"| {name} |");
        foreach (var split in splits)
        {
            var cell = rows.Where(r => r.Key[splitColumn] == split).ToList();
            sb.Append(cell.Count == 0 ? " - |" : string.Create(CultureInfo.InvariantCulture, $" {cell.Count(static r => r.FeatherQr):N0}/{cell.Count:N0} |"));
        }
        sb.Append(CultureInfo.InvariantCulture, $" {rows.Count(static r => r.FeatherQr):N0}/{rows.Count:N0} | {rows.Count(static r => r.ZXingCpp):N0} |");
        if (withZXingNet)
            sb.Append(CultureInfo.InvariantCulture, $" {rows.Count(static r => r.ZXingNet):N0} |");
        sb.Append(CultureInfo.InvariantCulture, $" {rows.Count(static r => !r.FeatherQr && !IsContent(r) && r.ZXingCpp):N0} | {rows.Count(static r => r.FeatherQr && !r.ZXingCpp):N0} |");
        sb.AppendLine(withContent ? string.Create(CultureInfo.InvariantCulture, $" {rows.Count(IsContent):N0} |") : "");
    }

    /// <summary>The symbol was located, sampled and error-corrected; what stopped the read, or changed the text, is in the bit stream or its character set.</summary>
    private static bool IsContent(ResultRow row) => row.Misread || row.Status is "InvalidBitstream" or "UnsupportedContent" or "UnmappedCharacter";

    private static bool IsFailure(string status) => status.StartsWith("EncodeFailed", StringComparison.Ordinal) || status.StartsWith("RenderFailed", StringComparison.Ordinal);
}
