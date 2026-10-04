/// <summary>
/// One line of the corpus manifest (<c>manifest.tsv</c>): an operation on one case's input, which is what a CLI process measures.
/// The collector writes it with the corpus; the BenchmarkDotNet reference project reads it to time the same entries.
/// </summary>
/// <param name="Key">The operation and the case, such as <c>decode-image/qr-url</c>.</param>
/// <param name="Input">The input file, relative to the corpus directory.</param>
/// <param name="Ecc">The pinned error correction level, for encode only.</param>
/// <param name="Version">The pinned symbol version, for encode only.</param>
/// <param name="ExpectedHex">The payload's UTF-8 bytes in hex, which every decode returns and every encoded matrix decodes to.</param>
internal sealed record ManifestEntry(string Key, string Case, string Op, string Symbology, string Input, string? Ecc, string? Version, string ExpectedHex)
{
    public const string FileName = "manifest.tsv";
    public const string Header = "key\tcase\top\tsymbology\tinput\tecc\tversion\texpected_hex";

    public static ManifestEntry[] Read(string corpusDir)
    {
        var lines = File.ReadAllLines(Path.Combine(corpusDir, FileName));
        if (lines.Length == 0 || lines[0] != Header)
            throw new InvalidDataException($"{Path.Combine(corpusDir, FileName)} does not start with the manifest header.");
        return lines.Skip(1).Where(line => line.Length > 0).Select(line =>
        {
            var f = line.Split('\t');
            return new ManifestEntry(f[0], f[1], f[2], f[3], f[4], f[5] == "-" ? null : f[5], f[6] == "-" ? null : f[6], f[7]);
        }).ToArray();
    }

    public string ToLine() => string.Join('\t', Key, Case, Op, Symbology, Input, Ecc ?? "-", Version ?? "-", ExpectedHex);

    /// <summary>The protocol's positional arguments and pins after the mode: op, symbology, input, then <c>--ecc</c> and <c>--version</c> for encode.</summary>
    public string[] Arguments(string corpusDir)
    {
        var args = new List<string> { Op, Symbology, Path.Combine(corpusDir, Input) };
        if (Ecc is not null)
            args.AddRange(["--ecc", Ecc]);
        if (Version is not null)
            args.AddRange(["--version", Version]);
        return [.. args];
    }
}
