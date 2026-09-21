using System.Globalization;
using System.Text;

namespace QRImageDecodeSweep;

/// <summary>
/// Two result files of the same sweep, image for image: what this library started reading, what it stopped reading, and every image it lost by name.
/// The two files come from two trees (a worktree at the commit before, and the change). The keys are how each image was made, so they pair whatever the decoder does; the pixel digest then says whether the pair is one image.
/// A pair whose pixels differ (an encoder chose another mask, a renderer changed) is counted apart and kept out of gained and lost: a read that moved there says nothing about the decoder.
/// </summary>
internal static class Compare
{
    public static int Run(string beforePath, string afterPath)
    {
        var (beforeColumns, before) = Csv.Read(beforePath);
        var (afterColumns, after) = Csv.Read(afterPath);
        if (!beforeColumns.AsSpan().SequenceEqual(afterColumns))
        {
            Console.Error.WriteLine("The two files have different key columns.");
            return 1;
        }

        // A sweep is grouped by kind, a corpus by sample set
        var groupColumn = Array.IndexOf(beforeColumns, "kind");
        if (groupColumn < 0)
            groupColumn = Math.Max(0, Array.IndexOf(beforeColumns, "set"));
        var afterByKey = after.ToDictionary(static r => string.Join('|', r.Key));
        var matched = before.Count(r => afterByKey.ContainsKey(string.Join('|', r.Key)));
        var unmatched = before.Count - matched + after.Count - matched;

        var sb = new StringBuilder();
        sb.AppendLine("| | Same image | Before | After | Gained | Lost | Misread before | Misread after | Other image |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        var lost = new List<ResultRow>();
        var otherImage = 0;
        foreach (var group in before.Where(r => afterByKey.ContainsKey(string.Join('|', r.Key))).GroupBy(r => $"{r.Key[0]}: {r.Key[groupColumn]}").OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            var all = group.Select(r => (Before: r, After: afterByKey[string.Join('|', r.Key)])).ToList();
            var pairs = all.Where(static p => p.Before.Image == p.After.Image).ToList();
            otherImage += all.Count - pairs.Count;
            var groupLost = pairs.Where(static p => p.Before.FeatherQr && !p.After.FeatherQr).Select(static p => p.After).ToList();
            lost.AddRange(groupLost);
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key} | {pairs.Count:N0} | {pairs.Count(static p => p.Before.FeatherQr):N0} | {pairs.Count(static p => p.After.FeatherQr):N0} | {pairs.Count(static p => !p.Before.FeatherQr && p.After.FeatherQr):N0} | {groupLost.Count:N0} | {pairs.Count(static p => p.Before.Misread):N0} | {pairs.Count(static p => p.After.Misread):N0} | {all.Count - pairs.Count:N0} |");
        }
        Console.WriteLine(sb.ToString());

        Console.WriteLine($"{before.Count:N0} images before, {after.Count:N0} after, {unmatched:N0} without a partner, {otherImage:N0} pairs whose pixels differ.");
        if (otherImage > 0)
            Console.WriteLine("Pairs whose pixels differ are not the same image: an encoder or a renderer changed between the two trees. They are left out of every other column.");
        if (lost.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Lost ({lost.Count:N0}):");
            foreach (var row in lost)
                Console.WriteLine($"  {string.Join(", ", row.Key)} -> {row.Status}");
        }
        return 0;
    }
}
