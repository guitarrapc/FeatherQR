using System.Text;
using ZXingCpp;

namespace QRInteropFixtures;

/// <summary>
/// Sanity gate for a Structured Append set: every symbol is rendered, read with the
/// zxing-cpp reader, and the header it reports (index, count, parity) must agree with
/// what the encoder claimed. The parity is checked against its definition, the XOR of
/// the bytes the set carries: the reader's <c>Bytes</c> are the raw segment bytes
/// (ISO-8859-1, UTF-8 or Shift_JIS for a Kanji segment), concatenated in index order.
/// Those bytes must also be one known encoding of the case text, which names the
/// charset for the manifest. The reader supplies per-symbol text where the encoder does
/// not expose it, and the texts concatenated in index order must equal the case text.
/// </summary>
public static class StructuredAppendSanityGate
{
    public static GeneratedFixture[] Verify(GeneratedFixture[] set, StructuredAppendCaseDefinition caseDefinition)
    {
        var lineage = set[0].Manifest.Generator;
        var results = new Barcode[set.Length];
        for (var i = 0; i < set.Length; i++)
        {
            results[i] = Read(set[i]);
            var claimed = set[i].Manifest.StructuredAppend ?? throw new InvalidOperationException($"sanity gate: {lineage}/{set[i].Manifest.Id} has no Structured Append claim.");
            if (results[i].SequenceIndex != claimed.Index || results[i].SequenceSize != claimed.Count)
                throw new InvalidOperationException($"sanity gate: {lineage}/{set[i].Manifest.Id} reads as {results[i].SequenceIndex} of {results[i].SequenceSize}, encoder claims {claimed.Index} of {claimed.Count}.");
        }

        var ordered = results.OrderBy(r => r.SequenceIndex).ToArray();
        var carried = ordered.SelectMany(r => r.Bytes).ToArray();
        var expectedParity = Xor(carried);
        var (charset, kanji) = Classify(carried, caseDefinition.PayloadText)
            ?? throw new InvalidOperationException($"sanity gate: set {lineage}/{caseDefinition.Id} carries bytes that are not the case text in ISO-8859-1, UTF-8 or Shift_JIS.");

        var hasEci = ordered[0].HasECI;
        if (ordered.Any(r => r.HasECI != hasEci))
            throw new InvalidOperationException($"sanity gate: set {lineage}/{caseDefinition.Id} mixes symbols with and without an ECI header.");
        // Pure ASCII is the same bytes in every charset, and the reader exposes only whether an
        // ECI is present, not which; a set like that has no charset the manifest could name.
        if (hasEci && carried.All(b => b < 0x80))
            throw new InvalidOperationException($"sanity gate: set {lineage}/{caseDefinition.Id} carries an ECI header over ASCII-only bytes, so its charset cannot be named from the reader.");

        var completed = new GeneratedFixture[set.Length];
        var texts = new string[set.Length];
        for (var i = 0; i < set.Length; i++)
        {
            var fixture = set[i];
            var manifest = fixture.Manifest;
            var claimed = manifest.StructuredAppend!;
            var result = results[i];

            if (!int.TryParse(result.SequenceId, out var parity) || parity != expectedParity)
                throw new InvalidOperationException($"sanity gate: {lineage}/{manifest.Id} carries parity '{result.SequenceId}', the XOR of the bytes the set carries ({charset}) is {expectedParity}.");

            var version = ParseVersion(result.Extra("Version"));
            if (version != manifest.Version)
                throw new InvalidOperationException($"sanity gate: {lineage}/{manifest.Id} reads as version {version}, manifest says {manifest.Version}.");

            var ecc = result.Extra("EcLevel");
            if (ecc != manifest.ErrorCorrectionLevel)
                throw new InvalidOperationException($"sanity gate: {lineage}/{manifest.Id} reads as ECC {ecc}, manifest says {manifest.ErrorCorrectionLevel}.");

            if (int.TryParse(result.Extra("DataMask"), out var mask) && mask != manifest.MaskPattern)
                throw new InvalidOperationException($"sanity gate: {lineage}/{manifest.Id} reads as mask {mask}, manifest says {manifest.MaskPattern}.");

            if (manifest.PayloadText.Length > 0 && result.Text != manifest.PayloadText)
                throw new InvalidOperationException($"sanity gate: {lineage}/{manifest.Id} decodes as \"{result.Text}\", manifest says \"{manifest.PayloadText}\".");

            texts[claimed.Index] = result.Text;
            completed[i] = fixture with
            {
                Manifest = manifest with
                {
                    PayloadText = result.Text,
                    PayloadUtf8Hex = Convert.ToHexString(Encoding.UTF8.GetBytes(result.Text)),
                    EciCharset = hasEci ? charset : null,
                    // Shift_JIS bytes without an ECI can only come from Kanji segments.
                    Mode = kanji && !hasEci ? "Kanji" : manifest.Mode,
                    StructuredAppend = claimed with { Parity = parity },
                },
            };
        }

        var joined = string.Concat(texts);
        if (joined != caseDefinition.PayloadText)
            throw new InvalidOperationException($"sanity gate: set {lineage}/{caseDefinition.Id} concatenates to a different text than the case ({joined.Length} vs {caseDefinition.PayloadText.Length} chars).");

        return completed;
    }

    public static int Xor(ReadOnlySpan<byte> bytes)
    {
        var parity = 0;
        foreach (var b in bytes)
            parity ^= b;
        return parity;
    }

    /// <summary>Which encoding of the case text the carried bytes are, or null when none matches.</summary>
    private static (string Charset, bool Kanji)? Classify(byte[] carried, string text)
    {
        if (carried.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(text)))
            return ("UTF-8", false);
        if (text.All(c => c <= 0xFF) && carried.AsSpan().SequenceEqual(Encoding.Latin1.GetBytes(text)))
            return ("ISO-8859-1", false);
        if (carried.AsSpan().SequenceEqual(KanjiPayload.ShiftJis.GetBytes(text)))
            return ("Shift_JIS", true);
        return null;
    }

    public static int ParseVersion(string extra)
    {
        var digits = new string(extra.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? -1 : int.Parse(digits);
    }

    private static Barcode Read(GeneratedFixture fixture)
    {
        var manifest = fixture.Manifest;
        var ppm = manifest.PixelsPerModule;
        var quietZone = manifest.QuietZoneModules;
        var sizeWithQuietZone = manifest.Width + quietZone * 2;
        var widthPixels = sizeWithQuietZone * ppm;

        var luminance = new byte[widthPixels * widthPixels];
        luminance.AsSpan().Fill(255);
        for (var row = 0; row < manifest.Width; row++)
        {
            for (var col = 0; col < manifest.Width; col++)
            {
                if (fixture.Modules[row * manifest.Width + col] == 0)
                    continue;

                var pixelRow = (quietZone + row) * ppm;
                var pixelCol = (quietZone + col) * ppm;
                for (var y = 0; y < ppm; y++)
                    luminance.AsSpan((pixelRow + y) * widthPixels + pixelCol, ppm).Clear();
            }
        }

        var results = new BarcodeReader { Formats = BarcodeFormat.QRCode, TryHarder = true }.From(new ImageView(luminance, widthPixels, widthPixels, ImageFormat.Lum));
        if (results.Length != 1)
            throw new InvalidOperationException($"sanity gate: zxing-cpp found {results.Length} symbols in fixture {manifest.Generator}/{manifest.Id}.");
        return results[0];
    }
}
