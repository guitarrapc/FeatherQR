# GS1: element strings, `(AI)` text and Digital Link

## Purpose

The maintainer asked for GS1 support on 2026-10-10. [fnc1-support-plan.md](fnc1-support-plan.md) gives the encoders and decoders FNC1, so a GS1 element string can go into a symbol and come back out. This plan covers the layer above it: the AI table, the checks GS1 defines, `(01)…(17)…` text in both directions, Digital Link URIs, and how GS1's carrier rules meet the three symbologies. The [2.0.0 plan](featherqr-2.0.0-plan.md) first put "GS1 syntax validation and human-readable `(AI)` formatting, which need the GS1 AI table" out of scope. This plan is that table and what it costs, and since 2026-10-10 it ships with FNC1 encode (G1).

The research behind it (GS1 sections, the dictionary format, other libraries, the oracles) is in [references/gs1-support-research.md](references/gs1-support-research.md). When the plan completes, its durable content moves into the specs, and the plan and its research file are deleted.

## What GS1 asks of a QR library

GS1 uses two QR carriers, and they need different things:

- GS1 QR Code carries an element string behind FNC1 in first position and is transmitted as `]Q3`. It is used in healthcare, logistics, assets, tobacco and with AI 8200. GS1 does not support ECI, Structured Append or Kanji mode in it.
- QR Code with a GS1 Digital Link URI is plain QR with no FNC1, transmitted as `]Q1`. It is the retail carrier: GS1's 2D migration at the point of sale (goal end of 2027) lists it with GS1 DataMatrix, and GS1 QR Code is not a retail option.
- Micro QR is excluded by the General Specifications, and rMQR is not listed as a carrier in Release 26.0.

Both carriers rest on the same data rules, so a caller needs three jobs from this layer:

1. Build: turn `(AI)` text or a Digital Link URI into an element string, or an element string into a Digital Link URI, with the separators and checks GS1 requires.
2. Read: split a decoded element string into AIs, show it as `(AI)` text, and turn a scanned Digital Link URI into an element string.
3. Check: lengths, character sets, check digits, dates, the other linters, and the AI pairing rules.

## Where it stands (2026-10-10, `main` at f4b74de)

- No GS1 code exists. FNC1 is planned in [fnc1-support-plan.md](fnc1-support-plan.md): `Fnc1Mode` as `Fnc1` on the Standard QR and rMQR options and decode info, an element string with U+001D separators in and out, no GS1 syntax checks. Its decode ships in 2.0.0 on its own, and its encode ships with this layer (G1). Its decision 1 (the shape of `Fnc1Mode`) is still open, and the recipes below follow its current proposal.
- A Digital Link URI already encodes and decodes as plain text. What is missing is building one correctly and reading GS1 data out of one.
- The real-image corpus holds one GS1 symbol (`qrcode-2/gs1-figure-4.15.1-2`), whose element string uses AIs 01, 21, 10, 17 and 8200.

## What the investigation found

### Every implementation that checks GS1 data generates its table from the Syntax Dictionary

libzint, BWIPP and CodeGlyphX generate their AI tables from the GS1 Barcode Syntax Dictionary. zxing-cpp keeps a hand-maintained, length-only table that cites an older dictionary release. The dictionary is Apache-2.0, released about once a year with the General Specifications, and holds 224 entry lines (about 541 AIs): component types and lengths, the predefined-length flag, the Digital Link attribute flag, 34 linter names, the pairing rules and the Digital Link key qualifiers. A hand-maintained table drifts. A generated one changes only when the pinned release does.

### Reading needs the table too, but it can tolerate newer AIs

An element string cannot be split without lengths, because a predefined-length element carries no separator. So formatting a decoded symbol as `(AI)` text needs the table, as the 2.0.0 plan said. A reader can still accept AIs newer than its table. The AI's own length follows from its first two digits (Table 7-5), and the predefined-length prefixes (Table 7-6) "will remain unchanged", so an unknown AI under a predefined-length prefix can be read by that length and any other unknown AI up to the next separator. The Syntax Engine does the same under "permit unknown AIs" (it vivifies a fixed-length entry for an unknown AI under prefixes 31 to 36 and 41). When building, an unknown AI is more likely a typo than a new AI, so building should refuse it.

### Pairing rules belong to the item, not to one symbol

The dictionary's `req` and `ex` rules (AI 10 requires 01, 02, 03, 8006 or 8026, and AI 21 excludes 235) are to be evaluated "over the combined data received from all GS1 carriers marking a physical item". A per-symbol check can refuse data that is valid on the item, for example a QR with batch and expiry beside a linear barcode with the GTIN. The Syntax Engine, BWIPP and CodeGlyphX check pairings by default. libzint does not.

### The retail half is Digital Link and needs no FNC1

A plan that stops at FNC1 misses retail. Digital Link URI Syntax 1.7.0 (August 2026) fixes the path (one primary key, its qualifiers in dictionary order), the query (data attributes, AIs flagged `?`), percent-encoding and a canonical form (`https://id.gs1.org`, query keys sorted). Retail requires the uncompressed form, and compression has moved to a separate EPC binary standard.

### GS1's rules on the symbol hold by construction

GS1 data is ASCII (CSET 82, 39 and 64). With `EciMode.Default` the encoders write no ECI header for ASCII text and never write Kanji mode for it, and the FNC1 plan refuses Structured Append with any FNC1 mode. So an element string from this layer, encoded with FNC1 in first position and default options, is a compliant GS1 QR Code. Only an `EciMode` the caller names breaks that. A test can pin the property, so no API has to refuse anything.

### The oracles disagree by dictionary release

CodeGlyphX 2.1.0 (already pinned) uses dictionary 2026-01-27, the release this plan pins. libzint 2.16.0 and zxing-cpp 3.1.0 (both through the pinned ZXingCpp 0.5.2) use older releases. The Syntax Engine 1.4.1 is the reference, but it ships only as prebuilt release assets (a Windows .NET wrapper among them), with no NuGet package. Fixtures therefore record each oracle's verdict and its dictionary release, and a disagreement is explained by release before it is called a defect.

## Scope

| In | Out |
|---|---|
| An AI table generated from a pinned dictionary release, checked for drift by a test | A hand-maintained table |
| Reading an element string: split into AIs, tolerant of unknown AIs and of a redundant or trailing separator | Scanner transmission strings (a `]Q3` prefix, keyboard-wedge forms). The decoder reports the mode instead |
| Checks: component lengths and character sets, all 34 linters including the ISO lists, `req` and `ex` | Data titles and non-HRI text (`GTIN`, `BEST BEFORE`), which would add the titles to the table |
| `(AI)` text in both directions, with the `\(` escape for a literal parenthesis and `\\` for a backslash | Compressed Digital Link URIs, resolvers and `linkType` |
| Digital Link URIs in both directions: uncompressed, URI Syntax 1.7.0, any stem on input, the canonical form on output | GS1 DataMatrix, GS1-128 and DataBar, which this library does not draw |
| Documentation: which carrier to use, the two-line recipes, GS1's no-ECI rule | Print guidance (X-dimension, print grade). The README says only that the default quiet zone is GS1's 4X |
| The Playground's GS1 input and decoded-AI view | GS1 on Micro QR, which has no FNC1 and is excluded by GS1 |
| GS1 entry points on the generators (G4, decided 2026-10-12. This table listed them as out of scope until the review of 2026-10-13) | |

## Design

### The element string is the hub

```text
(AI) text ── FromHri ──┐                       ┌── ToHri ──> (AI) text
                       ▼                       │
DL URI ── FromDigitalLink ──> element string (GS as U+001D) ── ToDigitalLink ──> DL URI
                                  │           ▲
              Fnc1 = FirstPosition│           │ info.Fnc1.IsFirstPosition
                                  ▼           │
                QRCodeGenerator / RmQRCodeGenerator / decoders   (fnc1-support-plan.md)
```

Every conversion is one hop, and the hub is the form the FNC1 encoder takes and the decoder returns. A Digital Link URI goes into a symbol as plain text, with no FNC1.

### API (approved 2026-10-12)

The listing below is the Phase 1 deliverable, reviewed in three rounds and approved by the maintainer on 2026-10-12. Phase 2 starts from it, and a change to it is a change to this section first. The recipes use the FNC1 plan's decision 1. Its read members (`info.Fnc1.IsFirstPosition`) are public from FNC1 decode in 2.0.0. Its construction members (`Fnc1Mode.FirstPosition`, the options' `Fnc1`) go public with FNC1 encode, which is the release this layer ships in (G1). The shape follows the existing pair: the data layer looks like the decoders (`Try` only, with a string or a caller's span, never throwing for input), and the generator entry points look like the generators (a one-line form that throws).

Package, namespace and names (G2, the entry type decided 2026-10-12): the package is `FeatherQR.Gs1`, its assembly `FeatherQR.Gs1.dll`, its namespace `FeatherQR.Gs1`, with internals under `FeatherQR.Gs1.Internals`, and it depends on `FeatherQR` and nothing else (decided with G4, 2026-10-12, the same direction as `FeatherQR.SkiaSharp`). The entry type is the static class `Gs1ElementString`, named after the hub every member converts to or from. A type named `Gs1` was compiled against callers in four namespaces: it resolves for a caller in the global namespace, in an application's own namespace and inside `FeatherQR.Gs1.Internals`, and fails (CS0234, the simple name `Gs1` binds to the namespace) for a caller in any other `FeatherQR` namespace, which is where this repository's 302 test files and the Playground live. An alias would have carried that, and the longer name costs one word. Every public type carries the `Gs1` prefix, as `Fnc1Mode` carries `Fnc1`. Six types: `Gs1ElementString`, `Gs1Error`, `Gs1ErrorKind`, `Gs1FormatException`, `Gs1Element` and `Gs1ElementEnumerator`, plus the two static classes the generator extensions compile to (`Gs1QRCodeGenerator`, `Gs1RmQRCodeGenerator`), which a consumer below C# 14 calls by name as the rendering package's `QRCodeImageDecoder` is called.

```csharp
namespace FeatherQR.Gs1;

/// <summary>Conversions between a GS1 element string (AI values with the group separator U+001D after each variable-length value another follows), its (AI) text and a GS1 Digital Link URI, and the checks the GS1 General Specifications define on the data.</summary>
public static class Gs1ElementString
{
    /// <summary>The release of the GS1 Barcode Syntax Dictionary the table was generated from, as the dictionary names it (<c>2026-01-27</c>). An AI assigned after it is read as data but not checked or built, so <see cref="Gs1ErrorKind.UnknownAi"/> from a building member means an AI newer than this release or a typo. Added 2026-10-12 before the pull request, so a caller can tell the two apart.</summary>
    public static string DictionaryRelease { get; }

    /// <summary>Builds an element string from (AI) text such as <c>(01)09506000134352(17)201225(10)ABC123</c>, a literal parenthesis in a value written <c>\(</c> and a backslash <c>\\</c>. Every check runs, pairings included: <c>false</c> with the error when the text is not valid GS1 data.</summary>
    public static bool TryFromHri(ReadOnlySpan<char> hri, out string elementString, out Gs1Error error);
    /// <summary>The span form of <see cref="TryFromHri"/>: writes into the caller's buffer and allocates nothing. <see cref="TryGetFromHriLength"/> gives the exact length, and the result is never longer than the text. A short destination fails with <see cref="Gs1ErrorKind.DestinationTooSmall"/>.</summary>
    public static bool TryFromHri(ReadOnlySpan<char> hri, Span<char> destination, out int charsWritten, out Gs1Error error);
    /// <summary>The length <see cref="TryFromHri"/> would write for this text, after the same checks: <c>false</c> with the same error where the conversion would fail.</summary>
    public static bool TryGetFromHriLength(ReadOnlySpan<char> hri, out int length, out Gs1Error error);

    /// <summary>Formats an element string as (AI) text, <c>(01)09506000134352(17)201225(10)ABC123</c>, a literal parenthesis in a value written <c>\(</c> and a backslash <c>\\</c> so that <see cref="TryFromHri"/> reads the text back. Runs what splitting needs and no more: an unknown AI is read by its predefined length or up to the next separator, a redundant or trailing separator is accepted.</summary>
    public static bool TryToHri(ReadOnlySpan<char> elementString, out string hri, out Gs1Error error);
    /// <summary>The span form of <see cref="TryToHri"/>. <see cref="TryGetToHriLength"/> gives the exact length, and the text is never longer than twice the element string.</summary>
    public static bool TryToHri(ReadOnlySpan<char> elementString, Span<char> destination, out int charsWritten, out Gs1Error error);
    /// <summary>The length <see cref="TryToHri"/> would write for this element string, after the same splitting.</summary>
    public static bool TryGetToHriLength(ReadOnlySpan<char> elementString, out int length, out Gs1Error error);

    /// <summary>Builds a GS1 Digital Link URI (URI Syntax 1.7.0, uncompressed) from an element string: the primary key and its qualifiers in the path, the other AIs as query attributes in the lexical order of the AIs, reserved characters percent-encoded. With no stem the canonical <c>https://id.gs1.org</c>. Every check runs, pairings included, plus what a URI can carry: <c>false</c> with the error for the data. A stem that is not an <c>http</c> or <c>https</c> URI prefix throws <see cref="ArgumentException"/>, as a malformed option does elsewhere.</summary>
    public static bool TryToDigitalLink(ReadOnlySpan<char> elementString, out string uri, out Gs1Error error, string? stem = null);
    /// <summary>The span form of <see cref="TryToDigitalLink"/>. <see cref="TryGetToDigitalLinkLength"/> gives the exact length, and the URI is never longer than the stem plus three times the element string plus two.</summary>
    public static bool TryToDigitalLink(ReadOnlySpan<char> elementString, Span<char> destination, out int charsWritten, out Gs1Error error, string? stem = null);
    /// <summary>The length <see cref="TryToDigitalLink"/> would write for this element string and stem, after the same checks.</summary>
    public static bool TryGetToDigitalLinkLength(ReadOnlySpan<char> elementString, out int length, out Gs1Error error, string? stem = null);

    /// <summary>Reads the GS1 data out of a Digital Link URI with any stem: the path's key and qualifiers, then the numeric query keys in URI order, percent-decoded, non-numeric keys ignored, an AI repeated anywhere refused as the Syntax Engine refuses it. Runs what reading needs: the URI's structure. A compressed URI is refused.</summary>
    public static bool TryFromDigitalLink(ReadOnlySpan<char> uri, out string elementString, out Gs1Error error);
    /// <summary>The span form of <see cref="TryFromDigitalLink"/>. <see cref="TryGetFromDigitalLinkLength"/> gives the exact length, and the element string is never longer than the URI.</summary>
    public static bool TryFromDigitalLink(ReadOnlySpan<char> uri, Span<char> destination, out int charsWritten, out Gs1Error error);
    /// <summary>The length <see cref="TryFromDigitalLink"/> would write for this URI, after the same reading.</summary>
    public static bool TryGetFromDigitalLinkLength(ReadOnlySpan<char> uri, out int length, out Gs1Error error);

    /// <summary>Reads what a decoder returned as GS1 data: with FNC1 in first position the text is an element string and comes back as (AI) text, otherwise the text is read as a Digital Link URI and its element string comes back as (AI) text. <c>false</c> when it is neither. The decode-side mirror of <c>CreateGs1</c>, independent of which decode entry point read the symbol.</summary>
    public static bool TryRead(ReadOnlySpan<char> text, Fnc1Mode fnc1, out string hri, out Gs1Error error);
    /// <summary>The span form of <see cref="TryRead"/>. The text is never longer than twice the input.</summary>
    public static bool TryRead(ReadOnlySpan<char> text, Fnc1Mode fnc1, Span<char> destination, out int charsWritten, out Gs1Error error);
    /// <summary>The length <see cref="TryRead"/> would write, after the same reading.</summary>
    public static bool TryGetReadLength(ReadOnlySpan<char> text, Fnc1Mode fnc1, out int length, out Gs1Error error);

    /// <summary>Runs every check on an element string: AI lengths and character sets, check digits and check character pairs, dates and the other linters, requisite and exclusive pairings, repeated AIs. <c>true</c> with <c>default</c> error when it passes.</summary>
    public static bool TryValidate(ReadOnlySpan<char> elementString, out Gs1Error error);

    /// <summary>Splits an element string into its elements without allocating: <c>foreach (var element in Gs1ElementString.EnumerateElements(text))</c>. Runs what splitting needs, as <see cref="TryToHri"/> does, and stops at the first element it cannot split. <see cref="TryValidate"/> says why.</summary>
    public static Gs1ElementEnumerator EnumerateElements(ReadOnlySpan<char> elementString);
}

/// <summary>Why a conversion or check refused its input: the kind, and where in the input (<see cref="Offset"/>, <see cref="Length"/>, both 0 when the kind names no place). <c>default</c> is <see cref="Gs1ErrorKind.None"/>. <see cref="ToString"/> is the message the generator entry points put in their <see cref="Gs1FormatException"/>.</summary>
public readonly record struct Gs1Error
{
    /// <summary>Creates an error. <paramref name="offset"/> and <paramref name="length"/> are positions in the input the error is about, 0 where the kind names no place. Throws <see cref="ArgumentOutOfRangeException"/> for a negative one.</summary>
    public Gs1Error(Gs1ErrorKind kind, int offset, int length);
    public Gs1ErrorKind Kind { get; }
    public int Offset { get; }
    public int Length { get; }
    public override string ToString();
}

/// <summary>Thrown by the generator entry points (<c>CreateGs1</c>, <c>CreateGs1DigitalLink</c>) when the data is not valid GS1 data. Nothing on <see cref="Gs1ElementString"/> throws for its input. A <see cref="FormatException"/>, as <see cref="UriFormatException"/> is, so an existing <c>catch (FormatException)</c> still catches it, with the <see cref="Error"/> for callers that want the kind and the place.</summary>
public sealed class Gs1FormatException : FormatException
{
    /// <summary>Creates the exception for an error of the data. Its <see cref="Gs1Error.ToString"/> becomes the message.</summary>
    public Gs1FormatException(Gs1Error error);
    public Gs1Error Error { get; }
}

/// <summary>The kinds of <see cref="Gs1Error"/>: why a conversion or check refused its input. Later releases may add members at the end.</summary>
public enum Gs1ErrorKind
{
    None,
    DestinationTooSmall,
    /// <summary>The (AI) text is malformed: an unbalanced or empty parenthesis, a non-digit AI, a stray character outside the elements.</summary>
    InvalidText,
    /// <summary>The input is not an http or https URI with a path.</summary>
    NotAUri,
    CompressedUri,
    InvalidPercentEncoding,
    /// <summary>A separator where no value can end: at the start, after another separator.</summary>
    UnexpectedSeparator,
    /// <summary>An AI the table does not know, where building refuses it (G6).</summary>
    UnknownAi,
    /// <summary>An AI a Digital Link URI cannot carry: (03), (8014), (8200), or one the table does not flag as a data attribute.</summary>
    AiNotAllowedInUri,
    MissingPrimaryKey,
    InvalidKeyQualifierSequence,
    EmptyValue,
    ValueTooShort,
    ValueTooLong,
    InvalidCharacter,
    InvalidCheckDigit,
    InvalidDate,
    /// <summary>A linter other than the above refused the value: an ISO 3166 or 4217 code, a yes/no flag, a zero or non-zero rule, a coordinate, an IBAN, a coupon field, a position in a sequence.</summary>
    InvalidValue,
    MissingRequiredAi,
    ExcludedAiPair,
    /// <summary>The same AI twice with different values in an element string, or twice at all in a Digital Link URI.</summary>
    RepeatedAi,
}

/// <summary>One element of an element string as spans into it: the AI, its value and the AI's offset. The AI is 2 to 4 digits, the value excludes the separator.</summary>
public readonly ref struct Gs1Element
{
    public ReadOnlySpan<char> Ai { get; }
    public ReadOnlySpan<char> Value { get; }
    public int Offset { get; }
}

/// <summary>Enumerates the elements of an element string. Allocation-free, usable in <c>foreach</c>.</summary>
public ref struct Gs1ElementEnumerator
{
    public Gs1ElementEnumerator GetEnumerator();
    public bool MoveNext();
    public Gs1Element Current { get; }
}

/// <summary>GS1 entry points on the core generators (G4). A C# 14 consumer writes <c>QRCodeGenerator.CreateGs1(…)</c>, an older one <c>Gs1QRCodeGenerator.CreateGs1(…)</c>. The text is (AI) text or an element string, told apart by its first character: (AI) text starts with <c>(</c>, an element string with a digit. Every check runs, pairings included, and the element string goes into the symbol with FNC1 in first position. An <c>options.Fnc1</c> other than <c>default</c> or first position throws <see cref="ArgumentException"/>. Micro QR has no GS1 entry, since GS1 does not support it.</summary>
public static class Gs1QRCodeGenerator
{
    extension(QRCodeGenerator)
    {
        /// <summary>A GS1 QR Code of the data. Throws <see cref="Gs1FormatException"/> for the data and <see cref="ArgumentException"/> as <c>Create</c> does for its options.</summary>
        public static QRCodeData CreateGs1(ReadOnlySpan<char> text, QREccLevel eccLevel, in QRCodeGeneratorOptions options = null);
        /// <summary>The span form of <see cref="CreateGs1"/>, writing the modules into the caller's buffer as <c>Create</c> does.</summary>
        public static int CreateGs1(ReadOnlySpan<char> text, QREccLevel eccLevel, Span<byte> destination, in QRCodeGeneratorOptions options = null);
        /// <summary>The size <see cref="CreateGs1"/> needs, as <c>TryGetRequiredBufferSize</c> gives it for <c>Create</c>. <c>false</c> when the data is not valid GS1 data or does not fit.</summary>
        public static bool TryGetRequiredBufferSizeGs1(ReadOnlySpan<char> text, QREccLevel eccLevel, out QRCodeCalculatedSize size, in QRCodeGeneratorOptions options = null);
        /// <summary>A QR Code holding the Digital Link URI of the data, the retail carrier: plain QR, no FNC1, the canonical <c>https://id.gs1.org</c> stem unless one is given.</summary>
        public static QRCodeData CreateGs1DigitalLink(ReadOnlySpan<char> text, QREccLevel eccLevel, in QRCodeGeneratorOptions options = null, string? stem = null);
        /// <summary>The same under the canonical stem, with the options required (added 2026-10-13, approved by the maintainer in the review of Phase 6). A bare <c>default</c> or a target-typed <c>new()</c> as the third argument binds here. With only the two forms that end in an optional stem it binds to the destination form.</summary>
        public static QRCodeData CreateGs1DigitalLink(ReadOnlySpan<char> text, QREccLevel eccLevel, in QRCodeGeneratorOptions options);
        public static int CreateGs1DigitalLink(ReadOnlySpan<char> text, QREccLevel eccLevel, Span<byte> destination, in QRCodeGeneratorOptions options = null, string? stem = null);
        public static bool TryGetRequiredBufferSizeGs1DigitalLink(ReadOnlySpan<char> text, QREccLevel eccLevel, out QRCodeCalculatedSize size, in QRCodeGeneratorOptions options = null, string? stem = null);
    }
}

/// <summary>The same seven members on <c>RmQRCodeGenerator</c> with its level, options and size types. The docs say GS1 does not list rMQR as a carrier (G10).</summary>
public static class Gs1RmQRCodeGenerator
{
    extension(RmQRCodeGenerator) { /* CreateGs1 ×2, TryGetRequiredBufferSizeGs1, CreateGs1DigitalLink ×3, TryGetRequiredBufferSizeGs1DigitalLink */ }
}
```

The recipes for the three jobs:

```csharp
using FeatherQR;
using FeatherQR.Gs1;

// Build a GS1 QR Code (healthcare, logistics). The entry point checks the data, writes the separators and sets FNC1.
var qr = QRCodeGenerator.CreateGs1("(01)09506000134352(17)201225(10)ABC123", QREccLevel.M);

// Build a QR Code with a Digital Link URI (retail). Plain QR, no FNC1.
var dl = QRCodeGenerator.CreateGs1DigitalLink("(01)09506000134352(10)ABC123", QREccLevel.M);

// Read: one line after any decode entry point, both carriers
if (QRCodeDecoder.TryDecode(data, out var text, out var info) && Gs1ElementString.TryRead(text, info.Fnc1, out var hri, out _))
{
    /* (01)09506000134352(17)201225(10)ABC123, from the element string or from the URI */
}

// The conversions on their own: a label's HRI, an element string for another carrier, a URI for a web page. The data layer never throws for input.
if (Gs1ElementString.TryFromHri("(01)09506000134352(10)ABC123", out var elementString, out var error)
    && Gs1ElementString.TryToDigitalLink(elementString, out var uri, out error, stem: "https://brand.example.com")) { }

// Check data from elsewhere, and look at single elements without allocating
if (!Gs1ElementString.TryValidate(elementString, out Gs1Error error)) { /* error.Kind, error.Offset, error.Length, error.ToString() */ }
foreach (var element in Gs1ElementString.EnumerateElements(elementString)) { /* element.Ai, element.Value */ }
```

| Member | From → to | Checks (G5) |
|---|---|---|
| `TryFromHri` | `(AI)` text → element string | every check, pairings included |
| `TryToDigitalLink` | element string → URI, with an optional stem (default `https://id.gs1.org`) | every check, pairings included, plus what a URI can carry |
| `TryToHri` | element string → `(AI)` text | what splitting needs, unknown AIs read |
| `TryFromDigitalLink` | URI → element string | what reading needs: the URI's structure, as the Syntax Engine's parser reads it (settled in Phase 5): the key qualifier order, a numeric query key an AI the table knows and a data attribute not already in or belonging to the path, a value that decodes. Plus what the element string needs to split back into the same elements: a predefined-length value has its length, no escape decodes to a control character |
| `TryValidate` | element string | every check |
| `EnumerateElements` | element string → `(Ai, Value)` spans | what splitting needs |
| `TryRead` | decoded text + `Fnc1Mode` → `(AI)` text | what reading needs: `TryToHri`'s under FNC1, `TryFromDigitalLink`'s then `TryToHri`'s otherwise |
| `CreateGs1`, `CreateGs1DigitalLink` (generator extensions) | `(AI)` text or element string → symbol | every check, pairings included, as the builders |

The rules the listing rests on:

- Every input is a `ReadOnlySpan<char>`, so a `string` converts without an overload. `Gs1ElementString` has `Try` forms only (decided 2026-10-12): with the generator entry points taking `(AI)` text directly, a throwing `FromHri` or `ToDigitalLink` had no caller a `Try` form does not serve, and the data layer is cleaner never throwing for input. Each `Try` form exists twice, returning a `string` or writing into a caller's span with `charsWritten`, and the span form allocates nothing.
- A short destination fails as the decoders' does: `false`, `charsWritten` 0 and `Gs1ErrorKind.DestinationTooSmall`. Each span form has a `TryGet…Length` twin that returns the exact length after the same checks, as the generators' `TryGetRequiredBufferSize` does, so a caller can allocate exactly, and a destination of that length succeeds with `charsWritten` equal to it. The bound each span form also documents (the text, twice the element string, the stem plus three times the element string plus two, the URI) lets a caller size from the input alone instead. Decided 2026-10-12 over documented bounds alone and over `GetMax…Length` formulas, because the exact form leaves both choices to the caller. The twins are named after the member they size (`TryGetFromHriLength` for `FromHri`), since two of the four produce an element string and a name built from the output would collide. The exact length costs a second pass over the input, which the pure-formula helper would not, and no caller on a hot path has to take it.
- No `Try` form throws for any input text (the invariant below). A malformed stem throws `ArgumentException` from every `TryToDigitalLink` form, as a malformed option does in the generators (G7).
- The generator entry points throw `Gs1FormatException` for the data, a `FormatException` whose message is `Gs1Error.ToString()` and whose `Error` is the `Gs1Error`, as `UriFormatException` derives in the BCL (G7, decided 2026-10-12 over a plain `FormatException`, so the one-line forms give the place of the error too).
- `TryToHri` escapes a literal `(` in a value as `\(`, so `TryFromHri` reads its output back (decided 2026-10-12), and a `\` as `\\` (decided 2026-10-12 in the review before the pull request), so that a value ending in a backslash, which only a decoded symbol with non-GS1 data can hold, cannot run into the next pair and read back as other data. A `\` before any other character is read as itself. Valid data never holds a backslash, so the second escape changes no text of valid data and reads every other tool's text of valid data as before. zxing-cpp's `TextMode.HRI` does not escape, and the fixtures' comparison in Phase 4 unescapes before comparing.
- Readers tolerate a redundant separator after a predefined-length element and a trailing one, as the Syntax Engine does and as 7.8.6.3 asks of a processing routine, and refuse a separator at the start or two in a row. Whether `TryValidate` reports a tolerated separator is settled in Phase 3 against the fixtures (the engine accepts both).
- The generators get GS1 entry points from this package through C# 14 extension blocks (G4, decided 2026-10-12): `CreateGs1` for the GS1 QR Code carrier and `CreateGs1DigitalLink` for the retail carrier, each with the data, destination and size forms of `Create`, on Standard QR and rMQR, twelve members in two enclosing classes, and since 2026-10-13 fourteen with the options-only form of `CreateGs1DigitalLink`. The decode side gets no twin on the decoders: `TryRead` on `Gs1ElementString` takes any decoder's text and `Fnc1Mode` and gives the `(AI)` text for either carrier, so the sixteen decode entry points (eight per symbology, two of them in the rendering package) stay as they are. No other library gives its decoder a GS1 entry: zxing-cpp formats HRI in a text mode, the rest return a flag and the element string.
- Both entry points take `(AI)` text or an element string in one parameter, told apart by the first character, which is unambiguous: `(AI)` text starts with `(` and an element string with a digit.

### The table

- Source: the dictionary file of the pinned release, committed with its license and a provenance note under the test data. The generator lives in the test project, as `SimdTiersDocTest` does: it fails when the generated source differs from what the pinned release produces and rewrites it outside CI.
- Output: RVA data and a lookup by AI, with no allocation and no static constructor, like `ShiftJisKanjiTable`. Titles are not stored.
- The list linters (`iso3166`, `iso3166alpha2`, `iso3166999`, `iso4217`, `iso5218`, `mediatype`, `packagetype`) need data the dictionary does not hold. Phase 0 found it in the same repository at the same tag, `src/lint_*.c`: bit fields for the ISO 3166 numeric, alpha-2 and ISO 4217 codes and the media types, a 431-entry string list for the package types, and the `iso5218` and `iso3166999` rules in code. The seven files are committed beside the dictionary with the same provenance note.
- The generator refuses a dictionary that names a linter the code does not implement, so a release that adds one cannot be pinned without it.
- Update: as its own pull request after each dictionary release, by the procedure under "Updating the dictionary" below.

### Updating the dictionary

The cadence. The dictionary's releases are date tags: 2022-08-10, 2022-11-24, 2023-03-22, 2023-07-04, 2023-09-22, 2023-12-11, 2024-06-10, 2025-01-30 and 2026-01-27 (the tag list checked on 2026-10-12, nothing newer). Since 2025 there has been one a year, at the end of January, tracking the General Specifications release ratified that month (26.0 for 2026-01-27). Before that there were several a year, each carrying one GSCN. So "once a year in January" is the recent pattern and not a rule, a mid-year release can carry an urgent GSCN, and the next one is expected in late January 2027 with General Specifications 27.0.

What a release can change, with the past releases as the record: new AIs (8040 to 8043 and 717 in 2026, 7041, 716, 03 and 8014 in 2025, 7250 to 7259 in 2024), changed flags (8020 lost `?` in 2025, `?` itself arrived in 2024), renamed linters (key and keyoff1 became gcppos1 and gcppos2 in 2026, hhmm became hhmi in 2025), new linters (the 8-digit dates, iso5218 and posinseqslash in 2024, hyphen in 2023), changed linter rules (csumalpha and iban in 2023-12), code list updates (ISO 4217 924 in 2025, currency codes in 2026) and changes to the file's own syntax (`+` requisites in 2023-12, the `?` flag in 2024). Each kind lands in a known place in this repository, listed next.

What an update touches:

- The pins: the release and commit in `tools/get_gs1_syntax_engine.cs` and `Gs1FixtureGenerator`, `PROVENANCE.md` with each file's SHA-256, `THIRD-PARTY-NOTICES.md`, and the release named in the "GS1 data" section of the symbologies record.
- The generated table and code lists, rewritten by `Gs1AiTableGeneratorTest` (it fails in CI on drift and rewrites outside CI), and the dictionary parser `Gs1SyntaxDictionary.cs` when the file's syntax changed.
- The hand-written parts the generator holds to the file: the `Gs1Linter` enum and `Gs1Linters` (a new or renamed linter makes the generator refuse the release until the code and its imported vectors exist), the frozen Tables 7-5 and 7-6 (cross-checked against the dictionary by `Gs1AiTableTest`), and the counts pinned in `Gs1AiTableTest` (224 entries, 541 AIs) and `Gs1FixtureTest` (541 per-AI cases, 34 linters).
- The fixtures: `regenerate-gs1` rebuilds the corpus against the oracles, and the per-AI group follows the dictionary. The Syntax Engine console app loads the new dictionary through `--syndict`, so its verdicts follow the release without a new engine, but a release that adds a linter needs the engine release that implements it. CodeGlyphX and libzint stay at their pinned versions and fall behind the dictionary, so their disagreement lists in the fixture spec grow, each case with its cause.
- Decisions a release can settle: G9's (7041) and (8040) to (8043), refused until the dictionary flags them.

The procedure, one pull request per release:

1. Set the new release tag and commit in `tools/get_gs1_syntax_engine.cs` and `Gs1FixtureGenerator`, run the app, then `import-gs1-syntax-dictionary` (it copies the dictionary, the seven `lint_*.c` files and the license, and writes `PROVENANCE.md`).
2. Run `Gs1AiTableGeneratorTest` outside CI to regenerate the table and code lists. If it refuses a linter, implement it in `Gs1Linters` with its `Gs1Linter` member and hold it to the imported vectors in `Gs1LinterTest`, as Phase 3 did for the 34.
3. Run `regenerate-gs1`, which also imports the linter unit tests, and read the disagreement report by cause. A new disagreement is either the pinned oracle falling behind (recorded in the fixture spec) or a defect (fixed before the pull request).
4. Update the pinned counts, `THIRD-PARTY-NOTICES.md` and the specs, and run the full suite on both frameworks.
5. The pull request lists the release's GSCNs, the AIs, flags, linters and code lists that changed, and the fixtures that moved.

Who notices a release and how the package ships it is decided in G12: a monthly workflow opens an issue, and the update releases the four packages together.

### Separators and order

Building writes a separator only where the General Specifications require one (§7.8): after every element except the last, unless its AI's first two digits are in the predefined-length table. It never reorders elements. A caller who wants fewer separators puts the variable-length element last, and the docs say so. Reading accepts a redundant separator after a predefined-length element and a trailing one, both of which occur in transmitted data. Whether `TryValidate` reports them is settled against the Syntax Engine's verdict in Phase 3.

## What has to stay true

- The core packages are untouched: `FeatherQR` and `FeatherQR.SkiaSharp` gain no type, no dependency and no notice. An application that does not reference `FeatherQR.Gs1` is unaffected.
- The encoders and decoders are unchanged by this layer. Their benchmarks stay within noise.
- Every element string this layer builds, encoded with FNC1 in first position and default options, carries no ECI header and no Kanji segment.
- The span forms allocate nothing. Checked in Release only, since Debug builds allocate for `stackalloc` initializers.
- No `Try` form throws for any input text. Only a malformed option, such as a stem that is not a URI prefix, throws, as the existing `Try` APIs do.
- The generated table is exactly what the pinned release produces.

## Open decisions

| # | Decision | Recommendation |
|---|---|---|
| G1 | Release | Decided 2026-10-10 (the 2.0.0 plan's D12): this layer ships in the same release as FNC1 encode ([fnc1-support-plan.md](fnc1-support-plan.md) Phases 3-6), not before it and not after it. That is 2.0.0 if both land before the Phase 7 freeze, 2.1.0 otherwise, and it does not block 2.0.0. An encoder that takes any element string without these builders lets a missing separator write a wrong symbol with no error. FNC1 decode ships in 2.0.0 on its own, since reading needs no GS1 layer |
| G2 | Package | Decided 2026-10-12: a fourth package, `FeatherQR.Gs1`, holding the whole data layer (the table, the checks, `(AI)` text and Digital Link in both directions). The maintainer chose it to keep the core's license posture untouched: the generated table is Apache-2.0-derived data, and in its own package the notice (G8) travels with the data and the core packages stay plain MIT. The package depends on `FeatherQR` (Phase 1 found the need: the generator entry points of G4 and `TryRead`'s `Fnc1Mode` parameter), in the direction `FeatherQR.SkiaSharp` already takes, and the core never references it. FNC1 itself stays in the core, because the mode indicator and the `%` rule are bit-stream behaviour of the encoders and decoders, and FNC1 decode already shipped there. The costs the core split record weighed (a fourth package to version in lockstep, validate in `check_package_deps`, list in `PublicAPI.approved.txt`, reference from the Playground and document) are accepted. The size argument that first put the layer in the core (about 6 KB of data, measured in Phase 0) no longer decides anything. The earlier text of this row recommended the core and listed the package as the alternative, with a size threshold and two revisit conditions. License isolation was the third condition, unwritten |
| G3 | Table source and update | Decided 2026-10-12: generated from dictionary release 2026-01-27 by a test, as described above, updated once a year. A hand-written table was asked about and not chosen. The plan's scope table had placed it in the Out column as a recommendation, not a decision, and the Phase 0 record misread that as a rejection. Transcribing the same dictionary by hand produces the same derivative work under the same license, so it saves no notice. Transcribing the General Specifications instead would change the source to GS1's standards terms, but the ISO code lists and the 34 linter rules would then have to be re-sourced from the standard's text, and the 5,656 linter vectors the tests use are the dictionary project's, so the attribution stays either way. With the layer in its own package the notice costs the core nothing, which settled it |
| G4 | Encode entry point | Decided 2026-10-12: `CreateGs1` and `CreateGs1DigitalLink` on `QRCodeGenerator` and `RmQRCodeGenerator`, added by `FeatherQR.Gs1` through extension blocks, with the data, destination and size forms of `Create` (twelve members, and fourteen since 2026-10-13 with the options-only form of `CreateGs1DigitalLink`). They take `(AI)` text or an element string, run every check and set FNC1 themselves, so a caller needs to know nothing of the GS1 format and cannot forget the FNC1 option. The maintainer chose this over the earlier recommendation (compose the conversion with the FNC1 option on `Create`) after a survey of the other libraries: the ones that take an element string behind a flag (ZXing.Net, go-qr, CodeGlyphX's QR encoder) let a missing separator through, and the ones that take `(AI)` text (libzint and zxing-cpp, BWIPP, CodeGlyphX's rMQR encoder and its Code 128 helper) are the ones a caller cannot misuse. The decode side gets `TryRead` on the data layer instead of decoder twins (see the API section) |
| G5 | Check strength per member | Decided 2026-10-12 as recommended: members that build data for a symbol run every check, pairings included. Members that read data from a symbol check only what they need and accept unknown AIs. `TryValidate` runs every check. No switch to turn the pairing check off. Revisit when a caller needs to build data split across carriers, which the pairing check refuses (five of the 21 General Specifications examples in the corpus are such data) |
| G6 | Unknown AIs | Decided 2026-10-12 with the API: read up to the next separator, refused when building. Amended in the review before the pull request the same day: an unknown AI whose first two digits have a predefined length (Table 7-6, frozen) is read by that length, as the Syntax Engine reads it, because a newer AI under prefixes 31 to 36 or 41 is encoded with no separator after it and a reader that ran to the separator would swallow the element after it |
| G7 | Error shape | Decided 2026-10-12: `Gs1Error` with a kind, an offset and a length into the input, so a caller can point at the bad element. The throwing forms throw `Gs1FormatException`, derived from `FormatException` as `UriFormatException` is, carrying the `Gs1Error`, so the one-line forms give the place too and an existing `catch (FormatException)` still works. The earlier recommendation of no new exception type was dropped because it would have forced callers who want the position onto the `Try` forms |
| G8 | License of the generated table | Decided 2026-10-12, with G2. `FeatherQR.Gs1` is MIT like the other packages (`PackageLicenseExpression` `MIT`) and carries a `THIRD-PARTY-NOTICES.md` packed into its nupkg, the way `Directory.Build.props` packs the README. The same file sits at the repository root, since the generated table lives in `src/`. It holds what Apache-2.0 section 4 asks for: the dictionary's three copyright lines (BWIPP project 2020-2021, Zint Project 2020-2021, GS1 AISBL 2021-2025), the release, the commit, the URL, a statement of what is derived (the AI table and the ISO 3166, ISO 4217, media and package type lists are generated from the dictionary and its reference linters, the linter rules are implemented in managed code and copy no C source) and the full Apache-2.0 text, because this repository's own LICENSE is MIT and cannot stand in for it. The dictionary repository has no NOTICE file (checked in Phase 0), so nothing else is owed. The generated source's header names the release, the commit and the notice file. `FeatherQR` and `FeatherQR.SkiaSharp` carry no notice. The Playground compiles the package into its WebAssembly bundle and links the notice from its page (Phase 7). The test fixtures' copy of the dictionary and the linter sources keep their own LICENSE and PROVENANCE.md beside them. The file is created in Phase 2 with the table it describes |
| G9 | Digital Link | Decided 2026-10-12 with the API, the two points left to Phase 5 settled the same day. Uncompressed, URI Syntax 1.7.0. Output in the canonical form. Input with any stem, percent-decoded. A repeated AI, equal values included, is refused as the Syntax Engine refuses it (decided 2026-10-12 after Phase 5, replacing the last-wins rule the Phase 0 note had read into URI Syntax 1.7.0: the engine is GS1's reference and a reader that drops data in silence is the worse default). Non-numeric extension keys are ignored. A compressed URI is refused with its own kind. When an element string holds two primary keys, the first in element-string order leads the URI, as the Syntax Engine does. Phase 0 found that 1.7.0 lists (7041) and (8040) to (8043) as data attributes while dictionary 2026-01-27 does not flag them `?`, and both dictionary-driven oracles refuse them in a URI. Phase 5 decided for the table: the flags are the dictionary's to give, the oracles agree, and the next release the table is regenerated from lifts them. Every other key, qualifier and attribute of 1.7.0 is in the dictionary with the same flags, so 1.7.0 needs no data newer than the pinned release |
| G10 | rMQR | No refusal. The layer does not see the symbol, and readers do read GS1 rMQR. The docs say GS1 does not list rMQR as a carrier (General Specifications Release 26.0), so a GS1 scanner may not accept it |
| G11 | Where the record lives | A "GS1 data" section in [qrcode-symbologies.md](../specs/qrcode-symbologies.md), since shared content lives only there. If it outgrows a section, a `gs1-data.md` design record and a guidelines change, decided with the maintainer then |
| G12 | Dictionary updates: who notices, how it ships | Decided 2026-10-15. Noticing: a scheduled workflow runs monthly, compares the newest tag of gs1/gs1-syntax-dictionary with the pinned release and opens an issue when they differ. The alternative, a checklist item each February after GS1's January release, misses a mid-year release. Shipping: the packages stay versioned in lockstep (G2), so a dictionary update, which changes `FeatherQR.Gs1` alone, releases all four packages at a minor bump. Giving `FeatherQR.Gs1` its own version would have meant changing `check_package_deps` and the release workflow, for a release that comes about once a year. The workflow is built in Phase 7 with the update procedure |
| G13 | Loading a dictionary at run time | Decided 2026-10-12: no. The table stays compiled in, and the dictionary release is the package version's. The maintainer declined a run-time dictionary (a file or text a caller passes) as an attack surface: whoever controls that input controls what the library accepts, builds and reads back, and the layer would gain a file parser that untrusted input reaches. Among the implementations surveyed only the Syntax Engine loads a dictionary at run time, and its linters are compiled code even then. A caller who needs an older dictionary pins the older package |

A lead outside the scope: the scheme and host of a Digital Link URI are case-insensitive, so an uppercase stem would let Alphanumeric mode carry it under `Optimal`. It needs the Digital Link standard's view and phone readers checked first.

## Equivalence classes

- AIs: 2-, 3- and 4-digit AIs. Range AIs (the decimal-point digit of 31nn-36nn). Predefined length, fixed length outside the predefined table (needs a separator), variable length. Optional and multi-component specs (`7007`, `8008`).
- Separators: after a variable-length element not last, after a fixed-length element outside the predefined table, none after a predefined-length element, none after the last. On read, a redundant one after a predefined-length element, a trailing one, two in a row (an empty field, refused).
- Character sets: each of `N`, `X`, `Y`, `Z` at its boundary characters, a character outside it, a non-ASCII character, `Z` padding.
- Each of the 34 linters: valid, the boundary, invalid.
- Dates: day `00` with `yymmd0` and without, 29 February in a leap and a common year across the -49 / +50 window, month 00 and 13.
- Pairings: a single requisite, a compound requisite (`+`), an exclusion, a repeated AI with an equal and an unequal value.
- Unknown AIs: read, refused when building.
- `(AI)` text: an escaped `\(`, an escaped `\\` and a lone `\`, an unbalanced parenthesis, an empty value, a non-digit AI, whitespace.
- Digital Link: each primary key, qualifiers in and out of order, query attributes, an AI the URI cannot carry, percent-encoded reserved characters, a stem with a path, `http` and `https`, a trailing slash, an unknown numeric query key, a non-numeric extension key, a repeated key, a compressed URI, two primary keys in one element string.
- Every `Try` form with a destination one character short and exactly long enough.
- Input length: empty, one element, longer than any symbol holds.
- Symbols: GS1 QR Code in Standard QR and rMQR, a Digital Link URI in plain QR, the corpus photograph.

## Verification

- Fixtures from the oracles, regenerated by hand and committed, as the interop scope decision requires: the Syntax Engine 1.4.1 console app (prebuilt, pinned by version and SHA-256, fetched by a script as qrtool is), CodeGlyphX 2.1.0's validator and Digital Link builder, and libzint's `gs1` option read back through zxing-cpp with `TextMode.Plain`. Each fixture records the verdict, the output and the oracle's dictionary release.
- The input corpus: the General Specifications' and the Digital Link standard's examples, the Syntax Engine's unit-test vectors and the dictionary's linter tests if it has them (imported with provenance, Apache-2.0), one generated valid value per AI, and one mutation per check.
- `(AI)` text: `TryToHri` matches zxing-cpp's `TextMode.HRI` for every valid fixture whose AIs its table knows.
- Separator placement: `TryFromHri` matches libzint's element string for every input both accept.
- Round trips: `(AI)` text → element string → `(AI)` text, and element string → Digital Link → element string for every element string a URI can carry.
- Symbols, after the FNC1 plan's encode phases: zxing-cpp reads this library's GS1 QR Code and rMQR as `]Q3` / `ContentType.GS1` with `(AI)` text equal to `TryToHri`'s. The corpus photograph reads as the expected `(AI)` text in all four rotations.
- Costs: a small benchmark class for the data layer's conversions and `TryValidate` on three typical element strings, recorded and not tuned through Phase 6 (the pairing checks were tuned after it, in the log of 2026-10-14). Allocation tests on the span forms. The trimmed size of a QR-encode-only application, unchanged, and of one that calls `FromHri`, recorded. The AOT analysis project calls every member with no warning. The Playground runs it under WebAssembly.

## Phases

| Phase | Work | Exit |
|---|---|---|
| 0 | Premises and fixtures, no `src/` change. G8 confirmed. The list linters' data source found and pinned. A pinned fetch of the Syntax Engine 1.4.1 console app, with its embedded dictionary release recorded. The input corpus and a `regenerate-gs1` command in `tools/QRInteropFixtures`. Whether Digital Link 1.7.0 depends on data newer than dictionary 2026-01-27. A size estimate of the table | Fixtures committed. Oracle disagreements listed with their cause |
| 1 | The API: names, signatures, XML summaries and the recipes for the three jobs, reviewed by the maintainer before code. The package's namespace and entry type name (G2), G4, G5 and G7 settled | The approved sketch recorded here |
| 2 | The `FeatherQR.Gs1` project and package (lockstep version, the `check_package_deps` graph, its own `PublicAPI.approved.txt`, the notice file of G8), then the table: the generator test, the generated table, the AI lookup, all internal | Drift test green. Size measured. The `FeatherQR` and `FeatherQR.SkiaSharp` packages unchanged apart from the version |
| 3 | Reading and checks: the split, the linters, the pairings, `TryValidate`, `EnumerateElements` | Verdicts match the Syntax Engine fixtures, disagreements listed by cause. No allocation. A fuzz run never throws |
| 4 | `(AI)` text: `TryToHri` and `TryFromHri`, their span forms and length twins | The `(AI)` text and separator checks above. Round trips |
| 5 | Digital Link: both directions, and `TryRead` | The Syntax Engine's Digital Link vectors and fixtures match. CodeGlyphX agrees where URI Syntax 1.6.0 and 1.7.0 agree |
| 6 | Symbols, after the FNC1 plan's Phases 3 and 4: the end-to-end tests, the compliance property, the corpus photograph (the data layer's benchmark class was built before the pull request, 2026-10-12) | zxing-cpp's reads match. Encode and decode benchmarks within noise |
| 7 | Playground (GS1 input as `(AI)` text, the Digital Link builder, the decoded AIs, on top of the FNC1 plan's encode selector), the documents below, the approved API listing, the dictionary update procedure moved to the fetch script and the specs with G12's decision, the monthly workflow G12 chose, then this plan folded and deleted with its research file | The Playground publish green and checked by hand |

Phases 0-5 touch no symbol code and can run beside the FNC1 plan. Only Phase 6 waits for it. This layer and the FNC1 plan's encode phases are released together (G1). If the 2.0.0 freeze comes first, both wait on a branch and merge after 2.0.0 is released.

## Documents to change

- [README.md](../../../README.md): the `FeatherQR.Gs1` install line beside the others, and a GS1 section. Which carrier to use (Digital Link for retail, GS1 QR Code elsewhere), the two-line recipes, leave `EciMode` at its default, rMQR's carrier status. No numbers.
- [qrcode-symbologies.md](../specs/qrcode-symbologies.md): the "GS1 data" section (the table and its update, the check strength per member, the carrier rules), the package topology (its scope row, the package table with `FeatherQR.Gs1` depending on `FeatherQR`, the packing paragraph and "Why three packages" become four, with the license isolation as the reason the fourth exists), the extension-member paragraph (the generators gain a second set, with `Gs1QRCodeGenerator` and `Gs1RmQRCodeGenerator` as the named fallback), scope rows (GS1 data in, compressed Digital Link, titles and scanner strings out, each with a revisit condition) and lessons.
- `tools/check_package_deps.cs`: the fourth package's dependency group per target framework (`FeatherQR` at the shared version).
- [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md): the GS1 oracles, the Syntax Engine under the toolchain policy, the GS1 fixtures and what each oracle's release explains.
- [standardqr-encoder.md](../specs/standardqr-encoder.md), [rmqr-encoder.md](../specs/rmqr-encoder.md) and the two decoder records: a link to the GS1 section where they describe FNC1.
- The third-party notice file (G8) and the XML docs of the new members.
- The dictionary update procedure, which this plan holds until Phase 7: the steps move into the header of `tools/get_gs1_syntax_engine.cs` beside the pins they change, and the cadence, what a release can change and the shipping decision (G12) into the "GS1 data" section of [qrcode-symbologies.md](../specs/qrcode-symbologies.md).

## Risks

- The table goes stale between releases. Tolerant reading keeps decoded data readable, and the yearly update is a checklist item.
- Oracles disagree because of their dictionary releases. Fixtures record the release so a disagreement is explained before it is fixed.
- The pairing check refuses data that is valid across carriers (G5). The revisit condition names it.
- Scope grows into titles, scanner strings or other symbologies. Each is an Out row with its condition.

## Progress log

### Investigation (2026-10-10)

Done: surveyed the GS1 General Specifications Release 26.0, Digital Link URI Syntax 1.7.0, the retail 2D guideline, the Syntax Dictionary 2026-01-27 and the Syntax Engine 1.4.1, and how zxing-cpp, libzint, BWIPP, CodeGlyphX, ZXing.Net, go-qr and qrcode2 handle the layer above FNC1. Split the work with the FNC1 plan: that plan owns the bit stream, this one owns everything above the element string.

Lessons:

- A GS1 plan that stops at FNC1 misses retail. The point-of-sale migration uses Digital Link URIs in plain QR, and GS1 QR Code is not a retail carrier.
- Reading GS1 data needs the AI table as much as checking it does, because predefined-length elements carry no separator. Reading can still accept newer AIs, because the tables that give an AI's length and the predefined lengths are frozen.
- A reference implementation's output shows its own dictionary release, not the standard. zxing-cpp's hand table and libzint's generated one are older than CodeGlyphX's, so a disagreement needs its release named before it is called a defect.
- Pairing rules are defined per item, not per symbol, so a per-symbol check can refuse valid data.

Benchmarks: not applicable, no `src/` change.

### Release order (2026-10-10)

Done: the maintainer decided G1. This layer ships in the same release as FNC1 encode, and FNC1 decode ships in 2.0.0 on its own. [featherqr-2.0.0-plan.md](featherqr-2.0.0-plan.md) (scope row, D12, Phase 6c) and [fnc1-support-plan.md](fnc1-support-plan.md) (phase exits, release paragraph) were updated in the same change.

Lesson: what decides the release is where a caller can go wrong. Reading GS1 data is safe without this layer. Writing it is not, because the encoder cannot tell a missing separator from a long value.

Benchmarks: not applicable, no `src/` change.

### Phase 0: premises and fixtures (2026-10-12)

Done: `get-gs1-syntax-engine.ps1` fetches the Syntax Engine 1.4.1 console app (pinned by SHA-256) and shallow clones of its source and of Syntax Dictionary release 2026-01-27 (pinned by commit). `import-gs1-syntax-dictionary` copies the dictionary, its LICENSE and the seven list-linter sources into `Fixtures/Gs1/syntax-dictionary/` with a provenance note that hashes each file. `regenerate-gs1` writes 1,248 cases (779 of this repository's corpus, 469 imported from the engine's unit tests) and 5,656 linter unit-test vectors, each case carrying the verdict of the Syntax Engine (run with the committed dictionary through `--syndict`), CodeGlyphX, libzint and the zxing-cpp reader. `Gs1FixtureTest` (17 tests, green) pins the dictionary's hash and release, the corpus shapes, one engine-valid case per AI and one engine-refused mutation per linter, the vector counts, and the imported vectors' agreement with their source or a named cause. The oracle disagreements are listed by cause in [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md), and that record's claim that libzint validates check digits was corrected there.

The premises, item by item:

- G8: settled the same day with G2, in the next entry. Phase 0 found that the dictionary repository has no NOTICE file, so the Apache-2.0 attribution is its LICENSE text and the copyright lines of the dictionary header, which the provenance note already carries.
- The list linters' source is the dictionary repository at the same tag (the table section above). The generator of Phase 2 reads the bit fields and the string list from the committed sources, and the drift test can hash them.
- The Syntax Engine 1.4.1 console app embeds dictionary 2026-01-27 (its source copy differs from the release in one copyright-year line). The app also loads a dictionary file on the command line, so the fixtures never depend on the embedded table. The library reports its build date, May 28 2026, as its version string. Its console form is a stdin menu, one process per input, which `regenerate-gs1` drives in parallel in about ten seconds.
- Digital Link 1.7.0 needs no data newer than dictionary 2026-01-27, with the five attribute flags noted in G9.
- The table: 224 entry lines, 541 AIs, 79 distinct components, 70 distinct specifications, 32 distinct `req` and 79 distinct `ex` strings, 34 linters. As data: an AI index of about 1.1 KB, entries 1.1 KB, components 0.9 KB, specifications 0.2 KB, the pairing strings 0.9 KB as text, the ISO 3166, ISO 4217 and media type bit fields 0.35 KB and the package types 1.7 KB, about 6 KB in all, against the 16 KB Kanji table. The linters' code is not in that figure. G2 holds.
- The dictionary names 34 linters. An earlier count of 31 missed the three that sit on optional components (`hyphen`, `mi`, `ss`), whose bracket precedes the comma.

Lessons:

- An oracle's "validates" is a claim about its return value. libzint runs its linters and writes the symbol anyway, reporting a warning the ZXingCpp wrapper drops. The FNC1 corpus never gave it a bad check digit, so the claim stood until this corpus did.
- A reference implementation's unit tests test a layer. 49 of the 469 engine vectors get the opposite verdict from its console app, every one because the test calls the parser below the requisite check or sets a flag the app does not. Keeping the source's expectation beside the binary's verdict made the layer visible.
- The pairing rule, applied per symbol, refuses the General Specifications' own figures. Five of the 21 GenSpecs cases show an attribute without its key, as a second symbol on the same item would, and the engine refuses all five. That is the evidence for G5's revisit condition.
- A clone's working tree is not the upstream file. The first import hashed a CRLF copy that `core.autocrlf` had made, 344 bytes longer than the release.

Benchmarks: not applicable, no `src/` change.

### Package and license (2026-10-12)

Done: the maintainer decided G2, G3 and G8 together. The layer ships as a fourth package, `FeatherQR.Gs1`, so the Apache-2.0 notice for the generated table travels with that package and the core packages keep their plain MIT posture. The table stays generated from the pinned dictionary, and the notice is a `THIRD-PARTY-NOTICES.md` in the package and at the repository root with the attribution and the full license text, created in Phase 2 with the table. The decision rows, the design paragraph on the public surface, the invariant on the core packages, the Phase 1 and 2 rows and the documents list were rewritten in this change. No code changed.

The question that settled it was whether a hand-written table would avoid the notice. It would not: a transcription of the same file is the same derivative work, and re-sourcing from the General Specifications would move the lists and the 34 linter rules back to the standard's text while the tests still use the dictionary project's vectors. Isolating the notice in its own package was the cheaper answer.

Lessons:

- A scope table's Out column reads as a decision. The hand-written table sat there as the investigation's recommendation, the Phase 0 record then called it rejected, and the maintainer had never been asked. A recommendation in a scope row needs the word, or a decision row of its own.
- The size threshold in G2 answered a question nobody was asking. The core split had rejected a package for a saving in kilobytes, so the row argued kilobytes, and the reason that decided the package was the license posture, which the row listed nowhere.

Benchmarks: not applicable, no `src/` change.

### Phase 1, first review round (2026-10-12)

Done: the API proposal replaced the sketch (the "API" section above). The maintainer decided three of its points on code examples: the entry type is `Gs1ElementString`, each span form gets an exact-length `TryGet…Length` twin, and `TryToHri` escapes a literal parenthesis. The name was decided after a compile probe of a type named `Gs1` in four caller namespaces, recorded in the names paragraph. The maintainer's comments on the rest (G4, G5, G7, the `Try` shapes, the error kinds) are pending, so the heading keeps "proposal". No code changed.

Lessons:

- A namespace collision is narrower than "the type is hidden". The compile probe showed the simple name `Gs1` binding to the namespace only for callers inside another `FeatherQR` namespace, which is this repository and almost no consumer. The decision still went to the longer name, since the cost was one word, but the first description of the problem had overstated who it reaches.
- A sizing helper that returns the exact length subsumes the other two shapes. A caller who wants the formula can still use the documented bound, and a caller who wants exactness gets it. The decoders' `GetMaxDecodedLength` exists because their bound is not a function of the input, which is not the case here.

Benchmarks: not applicable, no `src/` change.

### Phase 1, second review round (2026-10-12)

Done: the maintainer decided G4, G5 and G7 and the decode side. G4 reversed the recommendation: the generators get `CreateGs1` and `CreateGs1DigitalLink` from the package, which therefore depends on `FeatherQR`. G5 stands as recommended. G7 gains `Gs1FormatException`. The decode side gets `TryRead(text, fnc1)` on `Gs1ElementString` rather than twins on the sixteen decode entry points, after a comparison of the decode surfaces of zxing-cpp, ZXing.Net, CodeGlyphX and go-qr, none of which gives its decoder a GS1 entry. The API section, the decision rows, the recipes and the documents list carry the result. Still open from the review: the `Try` shapes and the `Gs1ErrorKind` members, which the maintainer asked to have explained and has not yet ruled on. No code changed.

Lessons:

- "No dependency" was a simplification that became an argument. The package needed no dependency only while every member worked on text, and the plan's G2 row stated it as a property. The costs that were real (overload twins, a named fallback class for older compilers) had a precedent in the rendering package and were paid there already.
- The encode and decode sides are not symmetric in any library, and for a reason. A writer owes the separators, so the encoder is where a GS1 entry prevents a wrong symbol. A reader owes only the flag, and every library stops there. The one-line read belongs to the data layer, where it does not multiply with the decode entry points.

Benchmarks: not applicable, no `src/` change.

### Phase 2: the package and the table (2026-10-12)

Done: `src/FeatherQR.Gs1` exists as the fourth package (`FeatherQR.Gs1`, the four target frameworks of the core, lockstep version, package validation, AOT analyzers, SBOM, `InternalsVisibleTo` the tests, a `ProjectReference` to `FeatherQR` and nothing else) and packs with `THIRD-PARTY-NOTICES.md` (G8: the attribution lines, the release and commit, what is derived, and the Apache-2.0 text copied verbatim from the committed LICENSE), beside the README every package carries. `tools/check_package_deps.cs` expects four packages and passes on a local pack: `FeatherQR.Gs1` depends on `FeatherQR` on every framework, the core packages' groups are unchanged. The table: `Gs1AiTable` (lookup by AI over 224 entries covering 541 AIs, each with its components, linters, `req` groups, `ex` patterns and Digital Link qualifier sequences, plus Tables 7-5 and 7-6 hand-coded from the specification text) and `Gs1CodeLists` (ISO 3166 numeric and alpha-2, ISO 4217, media types as bit fields, 431 package types as a sorted list), all internal, all byte span literals with no allocation and no static constructor. `Gs1AiTableGeneratorTest` renders both generated files from the committed dictionary and linter sources, fails on drift and rewrites outside CI, refuses a dictionary that names a linter `Gs1Linter` lacks, and holds the enum to the 34 names the release uses. `Gs1AiTableTest` reads the table back on the plan's AI shapes and pins that the dictionary's `*` flags and AI lengths agree with Tables 7-6 and 7-5 on every entry. `Gs1CodeListsTest` checks every well-formed value of the reference linters' own unit tests (1,000 numeric country codes, 676 alpha-2, 1,000 currency codes, 100 media types, 442 package type vectors) against the lists. `Gs1AssemblyDependencyTest` reads the dependency rule from the assembly. The 218 GS1 tests pass on net8.0 and net10.0.

Size: the generated data is 7,485 bytes (entries 3,584, components 1,111, attributes 718, the four bit fields 348, package types 1,724), against the Phase 0 estimate of about 6 KB. The assembly is 24 KB on net10.0, 25 KB on net8.0, 31 KB on netstandard2.1 and 34 KB on netstandard2.0 (the netstandard builds carry PolySharp's polyfills). The core packages are unchanged.

Deferred to Phase 3, where the first public type arrives: the package's `PublicAPI.approved.txt` and the two CI lines that check it and trim its XML documentation. Both tools (`check_public_api.cs`, `filter_public_docs.cs`) refuse an assembly with no exported surface, by design, so they cannot be wired for a package that is still all internal.

Lessons:

- The reference's unit tests enumerate a list, so they are the list's oracle. Each list linter's tests name every code of its range, and a test that compares the generated bit field with those verdicts needs no second transcription of ISO 3166 or ISO 4217.
- A range entry is one line and many prefixes. The first test of Table 7-5 took each entry's first AI, missed that `91-99` is one line, and reported prefix 92 as unassigned.
- A tool's refusal of an empty surface is a feature that bites a new package. The API listing and the documentation filter were written to stop a broken read from emptying a file, and a package with no public type yet reads as exactly that.

Benchmarks: not applicable, no change to the core.

### Phase 3: reading and checks (2026-10-12)

Done: the first public surface of `FeatherQR.Gs1`, five types: `Gs1ElementString` with `TryValidate` and `EnumerateElements`, `Gs1Error`, `Gs1ErrorKind` (21 members), `Gs1Element` and `Gs1ElementEnumerator`. Behind them, all internal: the split (`Gs1Splitter`, one rule for every reader and check), the four character sets (`Gs1Charsets`), the 34 linters in managed code from the rules of the General Specifications and the standards they cite (`Gs1Linters`, the two coupon formats in their own file), and the validator (`Gs1Validator`: components, then the rules between elements). The package's `PublicAPI.approved.txt` and the two CI lines deferred from Phase 2 are in place. The first listing of a `ref struct` showed the two API tools printing the compiler's compatibility `[Obsolete]` on it and no `ref`, so both now render `ref struct` and ignore that attribute on by-ref-like types, and the two older listings are unchanged by the fix. The tests, 3,788 on the two frameworks: every linter against its imported unit tests (5,656 vectors, the two GCP linters by explicit cases since their vectors are built in code), the character sets against theirs, `TryValidate` against the Syntax Engine's verdict on every comparable fixture (962 element strings, the per-AI and imported vectors among them, with the error class compared where the input was an element string), `EnumerateElements` against the engine's own split of every accepted string (676), the plan's equivalence classes as explicit cases, the error type, zero allocation in Release on the span paths, and a fuzz of 12 mutations per corpus input plus 5,000 random strings that never throws.

The verdicts match the engine on every comparable case. Three rules had to be read off the oracle rather than the specification: a fixed-length AI that is not of predefined length is read to the separator and its length checked after (so `811112345` is "too long", not an unknown trailing AI), the engine reports an exclusion before a missing requisite, and an IBAN's country code must be an ISO 3166 alpha-2 code. Two kinds of refused bracketed fixtures cannot be compared through a test-side join and are skipped by a read-back check: a value of the wrong length for a predefined AI splits into other elements once joined. Two-digit years resolve through the General Specifications' window around the current year; the reference linter compiles a fixed pivot year instead, and the two agree on every vector until 2050.

Lessons:

- The reference's unit tests are the cheapest complete oracle a linter can have. 32 of 34 linters needed no case of their own: the imported vectors enumerate the rule's edges, and the first run found the exact rules of the no-zero-prefix, IBAN and CSET 64 padding checks that the specification text leaves open.
- A join of bracketed text is not the element string the engine refused. Where the engine refused a value's length for a predefined AI, the join reparses into other elements and the validator rightly accepts them, so the comparison has to check that the join reads back into the same AIs before it asserts anything.
- "Fixed length" has two meanings in GS1 data. The table's format can be fixed (8005 is N6) while the element string is not of predefined length (8005 is not in Table 7-6), and a reader must take the separator's word over the format for the second kind, or a missing separator goes unreported.

Benchmarks: not applicable, no change to the core. The GS1 benchmark class comes with Phase 6.

### Phase 1 complete (2026-10-12)

Done: the maintainer approved the two remaining points as written, the `Try` shapes and the 21 members of `Gs1ErrorKind`, and then removed the throwing `FromHri` and `ToDigitalLink` from the data layer, since the generator entry points take `(AI)` text directly and a `Try` form serves every other caller. `Gs1ElementString` now never throws for its input, and `Gs1FormatException` is thrown by `CreateGs1` and `CreateGs1DigitalLink` only. The API section is the approved listing and Phase 1 is complete. Every open decision but G10 and G11 is now decided. Phase 2 builds the `FeatherQR.Gs1` project and the table from this listing. No code changed.

Benchmarks: not applicable, no `src/` change.

### Phase 4: the (AI) text (2026-10-12)

Done: the six `(AI)` text members of `Gs1ElementString` from the approved listing, `TryToHri` and `TryFromHri` in string and span forms with `TryGetToHriLength` and `TryGetFromHriLength`, behind one internal file (`Gs1Hri`). The text is `(AI)value` pairs with nothing between them and no blank after the closing parenthesis (GS1's HRI style allows one and the Syntax Engine prints one), a literal `(` in a value written `\(`. Formatting runs the split and nothing else, so an element string `TryValidate` refuses can still be shown. Building reads the pairs, looks each AI up, copies the value with its escapes resolved and checks it against its entry as it is copied, writes a separator after every value whose AI is not of predefined length when another follows, then runs the pairing rules over the element string built. Errors point into the text the caller gave, escapes included: a value's error is mapped through the escapes and a pairing error names the whole pair, parentheses included. The checks run before the destination is measured, so a short span reports the data's fault first and `DestinationTooSmall` only for data that passes. The span forms and length twins take a scratch buffer from the stack below 512 characters and rent one above it. The validator's value check took a value and its offset apart from the text for this, and the pairing check became internal. The approved listing gained the six members and is otherwise unchanged.

Tests, 5,044 per framework in `Gs1HriTest`: `TryFromHri` builds the Syntax Engine's element string from every bracketed fixture it accepted (652) and refuses every one it refused (250) with the error inside the text, `TryToHri` formats every accepted element string as the engine's HRI lines joined (676, the engine's blank after the AI removed) and every symbol's element string as zxing-cpp's `TextMode.HRI` text where that reader produced (AI) text (927 of 951), separator placement equals libzint's on every bracketed input both accept (646, the six AI 235 cases apart), both round trips over the corpus, the text's equivalence classes (escape, unbalanced parenthesis, empty or non-digit or five-digit AI, leading blank, blank after the AI, raw separator in a value) as explicit cases with their offsets, and the span forms, their length twins and the one-short destination. The fuzz test now runs both directions on every mutated and random string, the Release-only allocation test covers the four span forms, and the full suite is 52,362 tests on the two frameworks with none failing.

Lessons:

- zxing-cpp's `TextMode.HRI` is not always (AI) text. Where its table could not split the element string it returns the raw text, and where a value came up short before a separator it keeps the separator raw inside the (AI) text, so the comparison skips a text that does not start with `(` or that holds a separator (24 of 951).
- Check a built value before the join, not after. Validating the joined element string and mapping every offset back would report a value that overflows a predefined-length AI as an unknown AI at the overflow, because the joined string re-splits there. Checking each value as it is copied gives an error relative to the value, which maps to the text through the escapes, and leaves only the pairing errors to map, which name an element and are found by walking the pairs again with the same separator rule.

Benchmarks: not applicable, no change to the core. The GS1 benchmark class comes with Phase 6.

### Phase 5: Digital Link and the read (2026-10-12)

Done: the nine remaining data-layer members of the approved listing, `TryToDigitalLink` and `TryFromDigitalLink` in string and span forms with their length twins, and `TryRead` with its span form and `TryGetReadLength`, behind two internal files (`Gs1DigitalLink`, `Gs1Reader`). Building runs every check, takes the first element whose AI is a primary key, chooses the key's qualifier sequence that matches the most elements among those that leave only data attributes for the query (failing any such, the first that matches the most, as the engine does), writes the key and its present qualifiers in the sequence's order, then the other AIs as query parameters in the lexical order of the AIs with each AI once, every character outside the unreserved set percent-encoded as UTF-8. The stem is checked once per call and a bad one throws `ArgumentException` from every form. Reading follows the engine's parser: a case-insensitive `http` or `https` scheme, a host, the path walked backwards in `/AI/value` pairs to the first primary key so any stem may precede it, the qualifiers after the key held to one of the key's sequences, numeric query keys taken as AIs (an unknown one refused, a non-numeric one ignored), an AI the path or the query already holds refused even with equal values, a key that belongs in the path refused, `+` a blank in the query only, a fragment ignored. Values are read as they are except what the element string needs to split back into the same elements: a predefined-length AI's value must have its length, and an escape may not decode to a control character. `TryRead` formats the element string under FNC1 in first position and otherwise reads the text as a URI into a scratch element string and formats that, so its errors point into the text. The approved listing gained the nine members and is otherwise unchanged.

G9's two open points are settled above (the table wins on (7041) and (8040) to (8043), the first primary key leads). Four deliberate differences from the engine, each held by a test as a listed exception and recorded in the fixture spec (a fifth, the last-wins rule for a repeated query key, was built first and then dropped at the maintainer's decision the same day, since the engine refuses any repeat and a reader that drops data in silence is the worse default): the scheme is case-insensitive (RFC 3986, the engine takes `HtTp://` for plain data), a 13-digit GTIN in the query is not padded to 14 (URI Syntax 1.7.0 section 4.1, the engine pads), a malformed escape is refused (the engine keeps it as text), and (01)(235)(10) builds as `/01/…/235/…?10=…` where the engine strands (235) by taking the first longest sequence.

Tests, 1,965 per framework in `Gs1DigitalLinkTest` and `Gs1ReadTest`: `TryFromDigitalLink` on every URI of the corpus (186: the engine's element string on the 112 it read, a refusal or data `TryValidate` refuses on the 70 it refused, `NotAUri` on the 4 it took for plain data, the listed exceptions apart), `TryToDigitalLink` on every element string the engine built from or refused to (628 URIs equal to the engine's with its query sorted, which is CodeGlyphX's URI on all 618 it built, and 48 refusals with the engine's cause), both round trips, the equivalence classes with their offsets (stems, schemes, hosts, illegal characters, the compressed form, each path and query rule, each escape fault), stems that throw, the span forms, their length twins and the one-short destination, and `TryRead` on the engine's `]Q1` and `]Q3` scan-data vectors (16) and on every element string and URI it read (134, the two listed read exceptions apart). The fuzz test runs every conversion and the read on every mutated and random string, and checks that a URI that reads gives an element string that splits and that a built URI reads back. The Release-only allocation test covers the seven new span forms. The full suite is 56,292 tests on the two frameworks with none failing.

Lessons:

- The engine's generator and parser disagree on (01)(235)(10). The parser accepts `/01/…/235/…?10=…`, the generator strands (235) because it picks the first sequence with the most matches before asking what is left over. Preferring a sequence that leaves only data attributes behind makes the two directions agree, at one more pass over the elements.
- A reader that converts must keep the split. Reading a URI value as it is would let `/01/123` through, and the element string `01123` splits as (01) with a value cut short, so `TryRead` could fail after `TryFromDigitalLink` succeeded, with an offset into a string the caller never saw. Two value rules belong to reading for that reason, the predefined length and the control characters, and nothing else about a value.
- The standard does not say what a compressed URI looks like to a reader that does not decompress. The legacy compression is frozen and retail forbids it, so the kind is named by a heuristic on the final path segment (ten or more base64url characters with both letter cases), and only after no key was found, so no valid URI can hit it.

Benchmarks: not applicable, no change to the core. The GS1 benchmark class comes with Phase 6.

### Review before the pull request (2026-10-12)

Done: an adversarial review of the branch against the Syntax Engine's source (four reviewers over the split and checks, the conversions, the table and surface, the tests and documents, then three independent verifiers per claim). Eight implementation findings survived and are fixed, each but the scheme fold with a test that failed on the code before the fix (the fold's fault shows only on netstandard2.0, which the test project does not target):

- A digital signature (8030) beside an unserialised (253), (255) or (8003) was accepted whenever another requisite alternative held (`01+21`, `00`). The engine's serial rule is a validation of its own, not a reading of the dictionary's `+` alternatives, and is now a fourth pairing pass, reported at the key as the engine reports it. The requisite check is the plain one again.
- The pairing passes now run in the engine's order, exclusions then requisites then repeats then the serial rule. Only the reported kind changed where several rules fail at once.
- An IBAN of exactly ten characters passed (the reference refuses ten or fewer).
- The coupon's expiry and start dates were ordered through the two-digit year window. The reference compares the six characters, and so does the library now, so a pair straddling the window gives the reference's verdict.
- A separator inside a predefined-length window became part of the value and was then refused as a bad character, and `TryToHri` copied it into the (AI) text. The value is now cut short at the separator, as the engine reads it.
- An unknown AI under a predefined-length prefix (31 to 36, 41) ran to the next separator and swallowed the element after it. It takes the predefined length now (G6 amended).
- The scheme test used `OrdinalIgnoreCase` on spans, which allocates on netstandard2.0 once it meets a non-ASCII character. It is an ASCII fold by hand now.
- An empty query key (`?=x`) was ignored. The engine counts it as a numeric key and refuses it as an unknown AI, and so does the library now.

Also from the review: tests for every rented-buffer path (inputs over 512 characters), the escape mapping of a value error in (AI) text, the remaining percent-decoding branches, a query qualifier of a partly matched sequence, the splitter's AI-shape branches, the engine's refusal message classes on every refused URI, a pinned count of the validation cases that pass through the split under test, and an error-message test that can fail. One low finding was decided by the maintainer after the round: a value ending in `\` followed by another element formatted as text that read back as one element, since only `(` was escaped, and a decoded symbol with non-GS1 data shown by `TryRead` and re-encoded from that text would have changed in silence. `TryToHri` now writes a backslash as `\\` and `TryFromHri` reads it back, which changes no text of valid data (no GS1 character set holds a backslash) and makes the re-encoding of such a symbol fail on the backslash instead. The document corrections of the review are in the specs and in this plan's earlier entries.

Two additions after the review, from checking the design against the dictionary updates described under "Updating the dictionary": `Gs1ElementString.DictionaryRelease` (a static property, not a constant, so it is read from the package in use rather than inlined into the caller) tells a caller which release the library knows, which an `UnknownAi` refusal on building did not. And the tests' "unknown AI" examples (7050, 234, 3106, 418), which a release can assign, moved into one `Gs1UnassignedAi` holder with a test that reads the pinned dictionary and fails in one place when one of them is assigned, instead of in a dozen test rows. The check found the rest of the design closed to a dictionary release: new AIs, flags, pairings, qualifier sequences and code lists land in generated data, a new or renamed linter and a changed file syntax stop the generator or the parser rather than pass in silence, the imported reference vectors make a changed linter rule red before the fix, and nothing in the public surface names an AI.

From the pull request's review (2026-10-12): the `RepeatedAi` message said "with different values" although a Digital Link URI is refused for a repeat with equal values too, and now names both cases. A stem's authority is checked as a host with an optional port (a bracketed IPv6 address, or letters, digits, dots and hyphens, then nothing or a colon and digits), since `https://:` and `https://[]` passed the character deny-list although the contract promises a host. The reader keeps the Syntax Engine's deny-list, which is the parity rule for reading. And the Playground's API page, which `tools/public_api.cs` renders from the shipped assemblies and nothing in CI checks, now includes the GS1 package and was regenerated.

Benchmarks: not applicable, no change to the core.

### Benchmarks of the data layer (2026-10-12)

Done: the benchmark class the Verification section asks for, `Gs1DataLayer` in `src/FeatherQR.Benchmark`, built before the pull request rather than in Phase 6 at the maintainer's request. Three element strings, retail (01)(17)(10), logistics (00)(02)(11)(37)(10) and weighed (01)(3103)(3922)(17), the Digital Link standard's variable-measure example, each through `TryValidate`, the (AI) text and the URI in both directions and `TryRead` of the URI, all span forms into buffers sized by the length twins. CodeGlyphX 2.1.0's `Gs1Validator.Validate` on the same data as (AI) text stands beside `TryValidate` for scale, not as a baseline, since it also builds its element list. Recorded, not tuned. ShortRun, .NET 10.0.9, Ryzen 9 7950X3D, 2026-10-12:

| Operation | retail | logistics | weighed | Allocated |
|---|---|---|---|---|
| `TryValidate` | 0.63 µs | 1.55 µs | 1.21 µs | 0 |
| CodeGlyphX `Gs1Validator.Validate` | 0.74 µs | 1.27 µs | 0.68 µs | 1,456 to 2,184 B |
| `TryToHri` | 0.16 µs | 0.27 µs | 0.16 µs | 0 |
| `TryFromHri` | 0.65 µs | 1.73 µs | 1.16 µs | 0 |
| `TryToDigitalLink` | 1.60 µs | 3.14 µs | 2.17 µs | 0 |
| `TryFromDigitalLink` | 0.36 µs | 0.80 µs | 0.49 µs | 0 |
| `TryRead` of the URI | 0.42 µs | 0.94 µs | 0.58 µs | 0 |

What the numbers say: every span form allocates nothing, as the Release test holds. The readers (`TryToHri`, `TryFromDigitalLink`, `TryRead`) cost well under a microsecond. The members that run every check cost one to three microseconds, and `TryToDigitalLink` is the dearest because it validates and then chooses the key's qualifier sequence and sorts the attributes by re-splitting the string per candidate instead of keeping a list. The pairing checks are quadratic in the element count by design (no allocation), which is why the five-element logistics string costs more than the four-element weighed one. Against a symbol encode or decode, which costs tens of microseconds and more, none of this registers. Nothing was tuned, and the figures are the record a later change is measured against.

Correction (2026-10-14): the retail cell of `TryFromDigitalLink` is the time of a refusal, not of a conversion. Each buffer was as long as the setup's own text of the form the call writes, the element string, its (AI) text or its URI, and not, as the paragraph above the table says, the length its twin gives. For four of the five calls that write into a buffer the two are the same on these labels. For `TryFromDigitalLink` they are not: the element string read back from the retail URI has (10) before (17), the URI's order, so (10) takes a separator and the string is 33 characters where the original is 32. That call answered `false` for a destination that is too small, and the row timed it. The other twenty cells are of calls that succeeded. Each call now has its own buffer of the length its twin gives, and the setup makes each call once and fails for one that answers `false`. With the buffers as they were, that setup failed on the retail label at this call and passed on the other two labels. The cell reads 0.37 µs in the run of the corrected benchmark on 2026-10-14, with nothing allocated.


### Phase 6: symbols (2026-10-13)

Done: the twelve generator members of the approved listing and `Gs1FormatException`, which completes the listing. `Gs1QRCodeGenerator` and `Gs1RmQRCodeGenerator` each declare one extension block with `CreateGs1` in the data and destination forms, `TryGetRequiredBufferSizeGs1`, and the same three for `CreateGs1DigitalLink`. One internal file, `Gs1SymbolData`, turns the caller's text into the checked element string or into its URI, and each member is that call followed by the core's public `Create` or `TryGetRequiredBufferSize` with the caller's options. The package's approved listing went from 5 exported types to 8, and the Playground's API page was regenerated (67 types to 70). No file under `src/FeatherQR` or `src/FeatherQR.SkiaSharp` changed, so the encoders and decoders are the code they were and their benchmarks have nothing to move.

What the listing left to the build, each held by a test:

- The first character alone tells the form. Seven fixtures of the bracketed kind do not start with `(`. Six are refused under either reading. The seventh, an element string given where bracketed text was asked for (`010952012345678810ABC`), is refused by the Syntax Engine as bracketed text and taken by the entry points as the element string it is. The corpus test lists it as the one difference of form.
- An element string is checked and written as given, redundant separators included. (AI) text gets its separators placed, as `TryFromHri` places them.
- A wrong option is reported before wrong data, the order `Create` keeps between an option and content that does not fit. The entry points see the data first, so on the failing path they size a one-digit text under the caller's options before they throw for the data or answer `false`. On the other path `Create` checks the options itself.
- `CreateGs1` takes `Fnc1` at its default or first position and refuses second position with an `ArgumentException` that names `options`. `CreateGs1DigitalLink` refuses any FNC1 mode the same way, and a stem that is not an http or https prefix as the data layer does.
- An error's place is in the caller's text. When a URI cannot carry data given as (AI) text, the builder's place in the element string is mapped back to the pair, and a missing primary key covers the whole text.
- Two-digit years resolve from the clock, as in `TryValidate`. The corpus tests pin 2026 through the internal resolver and leave a case whose verdict the clock changes to the data layer's tests. As first written, tests saw the clock on the three data forms the corpus calls and on no other member. Since the review of 2026-10-13 every member is held to the data layer's verdict on 29 February of year 00, the one date whose validity shows the century.
- The conversions run in stack buffers of 512 characters and in pooled buffers past that. The URI's buffer is sized from the lengths of the text and the stem (the stem, three characters a character of text, two more), so the URI is built in one pass where the string form measures first.
- `Gs1FormatException` has a public constructor that takes the `Gs1Error`, as `Gs1Error` has one. The sketch lists neither.

Tests: `Gs1SymbolTest` holds 2,048 tests per framework. For both symbologies, from (AI) text and from an element string: the round trip through the decoder and `TryRead`, the symbol equal module for module to the one `Create` writes for the element string under FNC1 or for the URI, the named class and the extension as one method, the span and size forms against the data form, the other options passed through, each refusal of an option on each form, twelve refused texts with the data layer's error, a wrong option before wrong data, content that does not fit, URIs under stems, data a URI cannot carry with its place in the text, and text past the stack buffers (864 characters of (AI) text whose URI is more than twice as long, every value character a slash that is written `%2F`). The corpus goes through both entry points, 998 cases of which they take 677. `CreateGs1` takes what the Syntax Engine takes (the one difference of form apart) and writes the engine's element string, and each symbol holds the compliance property under `Single` and `Optimal`, in Standard QR and in rMQR: FNC1 in first position as the only header, so no ECI header, and Numeric, Alphanumeric and Byte segments only, so no Kanji segment. `CreateGs1DigitalLink` writes the data layer's URI for 632 of the 677, the three (01)(235)(10) cases of Phase 5 among them, and refuses the other 45 with the data layer's error at a place inside the text. The corpus photograph reads in the four rotations as its (AI) text, `CreateGs1` writes that text back into a symbol that decodes to the same data, and `CreateGs1DigitalLink` refuses it for (8200) with the place of that pair. The fuzz test gives every mutated and random text to the four `Create` forms and the four size forms: the size forms throw nothing, each `Create` form throws the data layer's error or what `Create` throws for content that does not fit and nothing else, each succeeds exactly where its size form says yes, and they take exactly the data the data layer takes. No text of the fuzz is longer than the stack buffers and no text it takes has a URI bound past them, so it writes nothing into a pooled buffer, and the pooled buffers and the bound on the URI are held by the tests of long text. The Release-only allocation test covers the destination and size forms of both symbologies on (AI) text and an element string, the size forms on refused data, where a destination form throws, and, for Standard QR, text past the stack buffers.

Planted faults: 27 single faults in the three files (5 in `Gs1SymbolData`, 11 in each generator file: the bound at two characters a character of text where it needs three, the form told one character late, an unchecked element string, each error mapping, the mode not set, each refusal of an option weakened, each option check on the failing path removed, an allocation in each span form, the size forms ignoring the stem or the options), run by `tools/mutation_check.cs` against the symbol, allocation and fuzz classes in Release. The first run caught 25. The two it missed were both in the rMQR Digital Link size form, the option check on refused data and the ignored stem, and the rMQR tests were extended until both failed a test.

Verified:

- zxing-cpp (`spot-check-gs1`, new in QRInteropFixtures): of the 984 distinct non-empty bracketed and element string inputs of the fixtures the entry points take 670. `CreateGs1` wrote each as a Standard QR and an rMQR symbol under `Single` and `Optimal` at level M, and zxing-cpp read all 2,680 with `]Q3`, GS1 content, no ECI header, the element string and, in `TextMode.HRI`, the (AI) text `TryToHri` gives, compared without its `\(` escape, which zxing-cpp does not write. `CreateGs1DigitalLink` wrote the 625 a URI can carry in both symbologies, and zxing-cpp read all 1,250 with `]Q1`, text content and the URI. With the Standard QR entry point's mode planted wrong the run reported 1,340 failures, every Standard QR GS1 symbol and no other.
- NativeAOT: the AOT analysis project now roots `FeatherQR.Gs1` beside the two core assemblies and its smoke runs `CreateGs1` on Standard QR and rMQR and `CreateGs1DigitalLink` on Standard QR. The publish, where any trim or AOT warning is an error, passed, and the native binary printed the smoke's line.
- The full suite on the two frameworks: 61,752 tests in Debug with none failing and 798 skipped, and 61,986 in Release, where the allocation tests run, with none failing and 796 skipped. The approved API listings of `FeatherQR` and `FeatherQR.Gs1` match the built assemblies on the four target frameworks.

Costs, recorded and not tuned (`Gs1SymbolEncode` in the benchmark project, the three labels of `Gs1DataLayer`, span forms into one buffer, level M). ShortRun, .NET 10.0.9, Ryzen 9 7950X3D, 2026-10-13:

| Operation | retail | logistics | weighed | Allocated |
|---|---|---|---|---|
| `Create`, the element string under FNC1 | 0.92 µs | 1.15 µs | 1.10 µs | 0 |
| `CreateGs1` from (AI) text | 1.51 µs | 2.60 µs | 2.21 µs | 0 |
| `CreateGs1` from an element string | 1.55 µs | 2.52 µs | 2.18 µs | 0 |
| `Create`, the URI | 1.14 µs | 1.34 µs | 1.36 µs | 0 |
| `CreateGs1DigitalLink` from (AI) text | 3.44 µs | 4.66 µs | 4.43 µs | 0 |
| rMQR `Create`, the element string under FNC1 | 0.35 µs | 0.49 µs | 0.40 µs | 0 |
| rMQR `CreateGs1` from (AI) text | 0.97 µs | 1.82 µs | 1.49 µs | 0 |

What the numbers say: an entry point costs its `Create` plus the data layer's work for the text and nothing else. `CreateGs1` adds 0.6 to 1.5 µs, which is `TryFromHri` or `TryValidate` on the same label in the table of 2026-10-12. `CreateGs1DigitalLink` adds 2.3 to 3.3 µs, the checks on the element string and then the builder, which runs them again before it chooses the path. Nothing was tuned.

Lessons:

- A fixture's kind names the mode its oracle was run in, not what the text is. The corpus test first expected every bracketed fixture to be read as (AI) text, and the one that holds an element string failed it. The expectation follows the entry points' own rule, and the difference is listed by id.
- A file derived from its twin needs the twin's tests in full. The rMQR members were generated from the Standard QR file, the rMQR tests were written shorter on the same reasoning, and both faults the planted run missed were in the rMQR Digital Link size form. The rMQR tests called that form with valid data, with data it refuses and under a narrowed version, but never with refused data under a wrong option, and of the two stems they passed it, one was a character shorter than the canonical one and gave the same symbol with and without it, and the other was followed only by a check that the call succeeds and allocates nothing. So neither the option check on refused data nor the stem was held. The test now asserts that its stem changes the version.
- The order of two errors is behavior. `Create` reports an option before content, and a layer that checks data first reverses that unless it asks for the option check on the path where `Create` never runs.

Left for Phase 7: the Playground, the README's GS1 section and the other documents of the list above, and folding this plan.


### Review of Phase 6 (2026-10-13)

Done: an adversarial review of the Phase 6 commit (four reviewers over correctness, the API and its cost, the tests, and the documents and comments, then three independent verifiers a claim). No reviewer found a wrong symbol, a wrong verdict on data, a wrong error for data or a `Try` form that throws for its text. 38 claims and parts of claims went to the verifiers. One was removed with two votes of three against it (that the spec's "the size forms throw for nothing but an option" leaves out the stem, which the invariant above counts among the options). The others stood, some narrowed, and are fixed:

- `CreateGs1DigitalLink(text, level, default)`, and the same call with a target-typed `new()`, bound to the destination form: an error about a destination that was never passed, or a compile error that names `Span<byte>`. With the stem after the options, the data form and the destination form both need a default filled in for such a call, and the destination, taken by value, then wins. The maintainer chose an options-only form, `CreateGs1DigitalLink(text, level, in options)`, in both classes. It needs no default, so it wins under every compiler, the named classes under C# 12 included. The approved listing above has it, and the surface is fourteen members.
- A destination that is too small was reported in `Create`'s words, which name `Create`'s sizing member. That member sizes the caller's text as plain text and so answers for another symbol: for `(01)09506000134352` at level M it says 1,089 bytes, where the GS1 symbol needs 841 and the Digital Link symbol 1,369. The destination forms now report it themselves, with the size needed and the name of their own size form.
- The two `TryGetRequiredBufferSizeGs1` forms ran the data layer's checks on text no symbol holds, and those cost the square of the number of elements: `TryValidate` takes 0.4 s on 20,000 characters of valid data and 3.6 s on 80,000 (one call each, Release, .NET 10). The two forms now answer `false` from the length, after the option checks, for an element string longer than the symbology's capacity in digits and for (AI) text longer than twice that, and return in under 0.1 ms on the same texts. The `Create` forms and the Digital Link forms keep the inherited cost, and the spec says why a length cannot answer for them.
- An element string longer than the stack buffer had an element buffer rented for it that nothing read or wrote. It is no longer rented.
- In documents only: text that starts with neither `(` nor a digit is read as an element string and refused as one, which the class remarks now say. `Gs1FormatException` is not serializable, which on .NET Framework means it does not cross an application domain boundary, recorded in the spec as a decision. The destination and size forms allocate once they ask the shared pool for an array larger than it holds, on .NET Framework more than 1,048,576 characters. The data and destination forms of `CreateGs1` ask for that on (AI) text of that length, and the Digital Link forms at about a third of it, since their URI buffer is three times the text. The spec's sentence on allocation now says so.

Tests: the reviewers planted faults the first 27 did not include, and these passed every test: the level ignored in each rMQR member, the options ignored in the Standard QR Digital Link destination form, the FNC1 refusal moved behind the data check in all twelve members, the year read a century late in nine, and the stem's term dropped from the URI bound. `Gs1SymbolMemberTest` (48 tests a framework) puts the fourteen members behind one signature and holds each to the level asked for, to an option's or a level's error ahead of the data's, and to the data layer's verdict on 29 February of year 00. It also holds which form a bare third argument selects, the destination forms' error, a stem that takes the URI past the stack buffer, and the two capacities against the core. `Gs1SymbolTest` (2,049 a framework) gained an option that changes the Digital Link symbol, with that premise asserted, the sixth member in the test of a wrong option, a text of two pairs with no primary key, the rMQR destination forms on text past the stack buffers, a URI longer than its text that fits an rMQR symbol, and in the corpus the place of a refusal held by its shape, where before it was compared with the resolver the entry point itself calls.

Planted faults: 96, generated member by member from the kinds the reviewers' faults were. For each of the twelve members with a body: the level at the final call and on the failing path, the options dropped, the year, and the FNC1 refusal behind the data check. For each of the eight `Create` forms among them, the option check on refused data removed, and for a destination form its buffers. Then eight faults in the shared resolver and seven a file in the code this round added. The first run caught 93. The three it missed were in the round's own code, the options-only form ignoring the level in both classes and the capacity of an element string halved, and the tests were extended until each failed one.

`spot-check-gs1` now reads the empty text as well and holds the two Standard QR size forms to the data layer's and the core's verdicts: valid data for every input, and a URI for the inputs taken. With the Standard QR size form planted to refuse every text it reports 670 verdicts that differ and exits 1, where before it printed four lines of "0 failures" and exited 0. Rerun: 985 inputs, 670 taken, 315 refused, 625 with a URI, no verdict that differs, and zxing-cpp read all 2,680 GS1 symbols and all 1,250 Digital Link symbols as before.

Verified: the full suite on the two frameworks, 61,850 tests in Debug with none failing and 798 skipped, and 62,084 in Release with none failing and 796 skipped. The approved API listings of the three libraries match the built assemblies on the four target frameworks. The NativeAOT publish passed again and the native binary printed the smoke's line. The costs after the changes, the benchmark and machine of the table above:

| Operation | retail | logistics | weighed | Allocated |
|---|---|---|---|---|
| `Create`, the element string under FNC1 | 0.91 µs | 1.14 µs | 1.10 µs | 0 |
| `CreateGs1` from (AI) text | 1.54 µs | 2.59 µs | 2.27 µs | 0 |
| `CreateGs1` from an element string | 1.50 µs | 2.45 µs | 2.16 µs | 0 |
| `Create`, the URI | 1.15 µs | 1.34 µs | 1.33 µs | 0 |
| `CreateGs1DigitalLink` from (AI) text | 3.21 µs | 4.67 µs | 4.48 µs | 0 |
| rMQR `Create`, the element string under FNC1 | 0.35 µs | 0.49 µs | 0.40 µs | 0 |
| rMQR `CreateGs1` from (AI) text | 0.94 µs | 1.81 µs | 1.43 µs | 0 |

No row moved by more than 7 % from the first table, and the largest move is a row that got faster, the retail Digital Link row, whose first measurement had the widest error. That is inside a ShortRun's spread.

Second round, over what the first round changed: three reviewers (the library changes, the tests, the documents) and then three verifiers a claim. The library reviewer found nothing. Of 19 claims and parts of claims two were removed. One was that no test holds the members other than the two size forms to not answering from the length: two votes of three put it outside the changes, and a test of it was added all the same, since the spec states it as a decision. The other was that the first paragraph of the Phase 6 record is out of date, which three of three refused, since a dated record speaks of its commit. Fifteen of the seventeen that stood were about what the first round had added. The other two were about text of the Phase 6 commit beside it: a count of members the new form had made stale, and a reference to this plan in a summary the round had edited. What stood, and what was done:

- The size in the destination forms' error was held at level M with default options only, so the sizing inside the handler could ignore the level, the options or FNC1 with every test passing. The test then ran each destination form at M, at H and without a quiet zone, on data whose size each of those and FNC1 changes, with those premises asserted.
- Nothing held the filter on that handler. Without it, content that does not fit was reported as a short destination with "0 bytes required". A test now holds a destination form's exception for such content to the data form's.
- The option check beside the length's answer was held by the level alone. A quiet zone below zero on text past the capacity is now a case of that test.
- No test read what the options-only form writes, only its size. The test of the binding decodes it now.
- The corpus's shape check took any whole pair as the place of a refusal. It now takes the refused AI from the data layer's own builder through the public members, which holds where the place starts in the six corpus cases whose refused pair is in the middle of the text.
- `spot-check-gs1` still counted an rMQR size form's refusal as "fits no symbol" and compared the URI verdict for the inputs taken only. It then compared the answer of every size form, in both symbologies, with the data layer's verdict and the core's fit for every input: 4,925 answers, none differing, with the symbols read as before. With both rMQR size forms planted to refuse every text it reported 1,965 answers that differ, and with the Standard QR Digital Link size form planted to answer `true` for every text it should refuse 360, each with exit code 1.
- Ten claims were about sentences in comments and in this record. Eight were false or said more than was true: what the fuzz reaches (twelve of its texts do rent a URI buffer, into which nothing is written), what the rMQR tests had called at the first planted run, where the pool's limit is met, what "no reviewer found" covered, and others. The other two are the stale count and the reference to this plan. All are corrected in place.

24 faults of the kinds the second round's reviewers planted were then run against the four classes, and each failed a test at the first run. `Gs1SymbolMemberTest` holds 50 tests a framework. The full suite on the two frameworks is 61,854 tests in Debug with none failing and 798 skipped, and 62,088 in Release with none failing and 796 skipped, and the three approved API listings match. No library code changed in this round, one comment apart.

Third round (2026-10-14), over what the second round changed: two reviewers (the tests and the tool, the documents) and three verifiers a claim. Nine claims stood and none was removed. The verifiers rated every one low, and none was about the library:

- The size in the destination forms' error was still held one byte short only, where a size read off the destination passes, and among the options for the quiet zone only. The test now gives each form an empty destination too, adds a pinned version, and holds the whole text the entry point writes into the message.
- The corpus's check of a refusal's place held where the place starts and not where it ends. The end is now held to the end of that pair, found by a scan of the text.
- The test that the other members do not answer from the length called seven of the twelve. It is now a case of the member list and runs for all fourteen.
- `spot-check-gs1` compared the size forms' answers where the fit decided none of them, since every valid input of the fixtures fits, and it discarded their sizes. It also stopped on an exception when the Standard QR size form of `CreateGs1` took refused data. It now asks each size form a second time under the smallest version, compares the size as well as the answer, and writes a symbol only for an answer that agrees: 9,850 answers, none differing, with the symbols read as before. Six planted faults each end it with exit code 1: both rMQR size forms refusing every text (2,004 answers differ), the Standard QR Digital Link size form answering `true` for every text it should refuse (720), the four size forms ignoring the fit (2,754), the Standard QR size forms sizing at level H (1,686), the Standard QR size form of `CreateGs1` taking text that is not GS1 data (630), and the Standard QR `CreateGs1` members under FNC1 in second position (17 answers differ, and all 1,320 symbols then written fail the read).
- Four sentences the second round wrote, one in the fixture spec and three in this record: the planted fault behind the figure 360, what the member list holds, "all" and "every" said of the seventeen, and the stems the rMQR tests had passed. They are corrected in place.

42 faults, the 24 of the second round and 18 of the kinds the third round's reviewers planted, were run against the four classes, and each failed a test at the first run. `Gs1SymbolMemberTest` holds 63 tests a framework. The full suite on the two frameworks is 61,880 tests in Debug with none failing and 798 skipped, and 62,114 in Release with none failing and 796 skipped, and the three approved API listings match. No library code changed in this round.

Lessons:

- An optional parameter after the options changes what a bare `default` selects. `Create(text, level, default)` reaches the options because they are its last parameter, so the form with a destination is the only one that needs a default filled in. One more optional parameter behind them makes both forms need one, and the tie goes to the by-value `Span<byte>`.
- The level was held on some members and not on others, the century on three of twelve, and the order of the FNC1 refusal and the data check on none. With the members behind one signature, one test of each of those three holds all fourteen members. The options are held member by member.
- A sentence about what a fuzz test holds needs the shape of its inputs. This one said no buffer sized from the text was too small, and no text it takes was long enough for anything to be written into one.
- A length can answer for a member only where the answer does not depend on the data. That is two of the fourteen here.
- A fix is new code. Fifteen of the seventeen claims that stood in the second round, and all nine of the third, were about something the round before had added or rewritten: a handler, a branch, a member, a test, a check in a tool, a sentence.

Left open by this review: the data layer's pairing checks cost the square of the number of elements (Phase 3 keeps no list), and the entry points make that reachable from the generators for text of any length. Making those passes linear in the data layer was not part of this phase. It was done the day after, in the next entry.

### Pairing checks in linear time (2026-10-14)

Done: the rules between elements, which the review of Phase 6 left open. The exclusions, the requisites and the repeated AIs each scanned the text for each element, so the checks cost the square of the number of elements. The serialised key rule was one scan and one pass. The checks now take time in proportion to the length of the text, still allocate nothing, and give every public member's verdict and error as before. The review of 2026-10-15 below changed the repeat check, the set and the tests in two rounds, and this entry describes them as they now are.

What the checks do now:

- A set of the AIs present answers the exclusions and the requisites. It has one bit for each number of two, three and four digits, assigned or not (11,100 bits, which is 174 words), and three words more with a bit for each of those words that holds an AI, 1,416 bytes on the stack in all. For an element string the pass that checks the components fills it: `TryValidate`, and through it `TryToDigitalLink` and the entry points given an element string. (AI) text has each value checked as its element string is built, so a pass of its own fills the set. A rule on one AI reads one word of the set. A rule on a pattern reads the words of a range of keys, since the wildcards of every pattern in the dictionary are its last digits: `31nn` is 3100 to 3199. `Gs1AiTableTest` holds that property of the table, so a dictionary release that breaks it fails there and not in a verdict.
- The exclusions and the requisites share one pass. An exclusion anywhere in the text is reported before a missing requisite, so the first element that misses a requisite is noted, and reported after the last element has been asked for its exclusions.
- The set says that an excluded AI is present and not where. The refusal is placed at the later of the two elements, so one scan of the text finds it, on the refusing path only.
- An AI that repeats must repeat its value, and the refusal is for the AI whose first element comes first, at its first element with another value. The check runs only when an AI stands twice, which the first pass knows, and it takes one pass. Each AI's first value sits in a table indexed by the AI's rank in the set, the number of the text's AIs below it. The table has a slot of eight bytes for each AI of the text, and a rank is one count of the bits in one word, with the count below each word that holds an AI taken first (348 bytes). The set's last three words name those words, so a text of few AIs counts few words.
- `TryValidate` clears the set only once the first element has passed its own checks, so text whose first element fails them does not pay for it. By the paired timing below, on .NET 10, a retail label refused at its first element takes 0.65 times the old time, and refused at its expiry, the second element, 0.75 times. On .NET 8 and .NET Framework a refusal at the second element costs more than before (left as it is, below).
- The serialised key rule runs only when the set holds (8030).
- Given text that does not split, the internal `CheckPairings` gives the splitter's error. The old passes asked each element's exclusions before they read on to the fault, so they could report an exclusion there. No library path gives it such text: its one caller in the library, the (AI) text builder, passes the element string it built, which always splits, and `TryValidate` refuses the fault before it asks any rule between elements.

Decisions:

- The table of first values is indexed by rank. Indexed by the AI, it would need a slot for each of the set's 11,100 numbers. Indexed by the table entry and the AI's place in its range, it would need 2,240 slots of eight bytes, about 18 KB of stack where the set takes 1.4 KB and the package's text buffers 1 KB, or a new table in the generated file to number the 541 AIs. This entry first took passes of 32 AIs each with a small table instead, and the review found their cost: on text of a set of 198 AIs that may stand together, in turn, they cost 224 ns an element against 60 ns with 30 AIs, 3.7 times as much. One pass costs 59 ns an element with 30 AIs and 85 ns with the 198, where their first round alone, with no repeat to compare, costs 60 ns (Release, .NET 10, the review's probe, on the final code).
- The set is indexed by the AI's digits and not by its table entry. A pattern names a range of keys and not a range of entries: `310n` is 3100 to 3109 where the entry is 3100 to 3105, and `31nn` spans seven entries. An index by entry would cost a search of the table for each rule.
- The old passes stay as the reference, in the test project. `Gs1PairingReference` is the rules as they were. It keeps no state between elements, so its answer and the place of its error follow from the order of its scans alone.

Tests:

- `Checks_OnManyElements_TakeUnderFiveSeconds` runs `TryValidate`, `TryFromHri` and `TryToDigitalLink` on valid data in five shapes: one AI repeating, a key with its batch, a weighed label whose price requires a measure by pattern, and the batch with its key only at the very end, each of 40,000 elements, then one AI after another, 80,000 elements in all, a key and then two AIs. The last two are where a scan meets what it looks for late, for a requisite and for a first value. A scan for a first value meets it late only for the second AI, so the last shape is twice as long, which gives both scans the same 1.6 billion reads. The old checks already take 3.7 to 15.4 s a call on 20,000 elements in Release (the table below). `CheckPairings_OnManyAisInTurn_TakesUnderFiveSeconds` runs the internal check on 159,984 elements, 808 rounds of a set of 198 AIs that may stand together. Both tests run apart from the other tests of their process, and CI runs one test process at a time.
- `Gs1PairingParityTest` (17 tests a framework) holds the library to the reference, answer and place, on every text that splits, and to the splitter's error on text that does not. Its texts: the 2,052 inputs and element strings of the corpus, of which 1,127 do not split, each of the 541 AIs alone and all 292,681 ordered pairs of them, 85,256 texts that put an AI whose rule has a pattern beside each AI in and near the pattern's range, 40,000 random element strings of one to sixteen drawn elements, after (00) and (01) in half of them, 18 texts around the serialised key, and 139 texts of a set of 198 AIs that may stand together, which is what it takes to reach the repeat check with many AIs. On those it holds the place of a repeat for every third AI and the last, the AI that comes first when two repeat with another value, and 24 shuffled texts, 18 of them with several such repeats. Three more texts hold the error the library gives on text that stops splitting after an excluded pair. The corpus tests pass with no expectation changed.
- `TryValidate_ErrorPointsAtTheFault` holds the place of a repeat that comes before another element, and of an AI the table does not know when it comes first, with a value the checks of (00) would pass, and when it stands between known elements. `TryValidate_TwoDigitYear_IsReadAgainstTheYearGiven` reads 29 February of year 00 as a date of 2000 in 2026 and of 2100 in 2051, two answers that a year taken from the clock cannot both give, with the date as the first element and as the second.
- `TryGetRequiredBufferSizeGs1_AiTextPastTwiceTheCapacity_IsAnsweredWithoutTheChecks` counts the arrays the shared pool hands out to the calling thread, by the pool's rent events, while each size form takes (AI) text one character past twice its capacity, which must rent none, and one character shorter, which the checks then refuse after they rent the buffer they build its element string in. An element string is checked where it is and rents nothing, so whether the length answers for it is not observable this way. `IsPastCapacity` has a test of its own.

Planted faults: 54 small faults were run against the GS1 classes, on the final code: 22 of the first list, 18 in the one-pass repeat check, the deferred clear, the requisite confirmed by a scan that the review planted and the size forms' answer from the length, five on the lines the second round found loosely tested (a repeat noted at the last element only, an unknown AI refused only first or only after the first, its error as long as the element, and the clock's year for the year given), two more in `TryValidate` (the first element's value unchecked, the end of the text one character early), and seven in the three words that name the words holding an AI, as they are written and read. 51 fail a test. The three that pass leave the behaviour as it is: an element counted as its own requisite, which no rule of this dictionary release can show, and in each size form an answer from the length for (AI) text only, which an element string, checked where it is, cannot show. A count of bits one too high in the lowest pair of a word passed the round before, since every rank stayed distinct and inside the table, and fails a test now that the same count finds the words that hold an AI. The first value found by a scan takes 22.6 to 22.9 s a call on the shape of 80,000 elements, and the requisite confirmed by a scan 26.8 to 29.4 s on the key last, against the five seconds. The third round found one more that passed, the first element's year taken from the clock at the call that checks that element alone. The year test now puts its date first as well, and fails it.

The full suite passes on .NET 8 and .NET 10: 61,948 tests in Debug with 798 skipped, and 62,182 in Release with 796 skipped. The Release run includes the data layer's allocation tests, whose refused text repeats an AI with another value and so runs the repeat check.

Figures, Release, .NET 10.0.9, Ryzen 9 7950X3D, on the final code of the review of 2026-10-15. Valid data of 20,000 elements, the old checks and these, one call of the old (they take seconds) and the fastest of three of these:

| Shape | `TryValidate` | `TryFromHri` | `TryToDigitalLink` |
|---|---|---|---|
| (01), then (91) 19,999 times | 4.0 s, 1.3 ms | 4.0 s, 1.7 ms | 3.9 s, 5.4 ms |
| (01)(10), 10,000 times | 11.4 s, 4.1 ms | 11.5 s, 4.6 ms | 11.5 s, 7.9 ms |
| (01)(3103)(3922)(17), 5,000 times | 15.4 s, 3.1 ms | 15.4 s, 3.6 ms | 15.4 s, 7.8 ms |
| (01), then (91) to (99) in turn | 3.7 s, 1.3 ms | 3.7 s, 1.7 ms | 3.7 s, 8.8 ms |

A typical label, as the paired timing reads it: the benchmark's three labels, one call and one label a process, the two builds adjacent in time and alternating which goes first, each process on the first eight cores, five rounds. A cell is the median before and after in µs, then the median of the rounds' ratios. Every call that runs the checks is faster, by 15 to 70 percent. The five calls that run no pairing check are the control and read 0.97 to 1.02:

| Call | retail | logistics | weighed |
|---|---|---|---|
| `TryValidate` | 0.59, 0.26 (0.45) | 1.36, 0.42 (0.31) | 1.10, 0.33 (0.30) |
| `TryFromHri` | 0.60, 0.35 (0.58) | 1.39, 0.56 (0.40) | 1.11, 0.43 (0.38) |
| `TryToDigitalLink` | 1.50, 1.19 (0.79) | 2.15, 1.19 (0.56) | 2.12, 1.34 (0.63) |
| `CreateGs1` from (AI) text | 1.74, 1.48 (0.85) | 2.82, 1.99 (0.70) | 2.51, 1.83 (0.73) |
| `CreateGs1` from an element string | 1.71, 1.41 (0.83) | 2.77, 1.87 (0.67) | 2.49, 1.73 (0.70) |
| `CreateGs1DigitalLink` from (AI) text | 3.52, 2.97 (0.84) | 5.17, 3.40 (0.66) | 4.94, 3.47 (0.70) |
| rMQR `CreateGs1` from (AI) text | 1.02, 0.78 (0.76) | 1.97, 1.14 (0.58) | 1.60, 0.91 (0.57) |
| `TryToHri`, control | 0.14, 0.14 (1.00) | 0.24, 0.24 (1.01) | 0.19, 0.19 (1.00) |
| `TryFromDigitalLink`, control | 0.40, 0.39 (0.97) | 0.59, 0.59 (1.00) | 0.53, 0.54 (1.02) |
| `TryRead` of the URI, control | 0.44, 0.44 (0.98) | 0.71, 0.70 (0.97) | 0.61, 0.60 (1.00) |
| `Create` on the element string, control | 1.06, 1.06 (0.99) | 1.35, 1.35 (1.01) | 1.31, 1.30 (1.00) |
| `Create` on the URI, control | 1.30, 1.31 (0.99) | 1.54, 1.53 (0.99) | 1.53, 1.54 (1.00) |

The benchmark project's own run of the same code (`Gs1DataLayer` and `Gs1SymbolEncode`, ShortRun, 2026-10-15) reads 0.25 to 0.40 µs for `TryValidate`, 0.34 to 0.52 µs for `TryFromHri`, 1.09 to 1.22 µs for `TryToDigitalLink` and 1.34 to 1.86 µs for `CreateGs1` from (AI) text, with nothing allocated. CodeGlyphX's validator reads 0.43 to 0.79 µs on the same labels and allocates 1,456 to 2,184 B. The paired timing is the measure, since rows of unchanged code moved by more than 10 percent, the most the project lets a benchmark's mean grow, between the benchmark's runs of 2026-10-14.

Left as it is:

- `TryToDigitalLink` still scans the text once for each distinct AI it writes into the query, to write them in order. That is time in proportion to the length of the text for a given set of AIs (5.4 ms for 20,000 elements of one such AI and 8.8 ms of nine), and the dictionary's AIs bound it.
- An exclusion refused in (AI) text pays more than before: the whole text is split into the set before the first exclusion is asked. (AI) text of (02), (01), (11), (37) and (10), in which (02) and (01) exclude each other, takes 1.25 times the old time by the paired timing.
- A refusal at the second element pays for clearing the set, which the old checks did not have, and costs more than before on .NET 8 and .NET Framework 4.8. The same harness built for those runtimes, seven rounds, reads 1.13 and 1.19 times the old time for a retail label refused at its expiry, and 1.13 and 1.18 with an unknown AI as its second element, where the valid label takes 0.46 and 0.40 times and a refusal at the first element 1.05 and 1.08. Leaving the set uncleared took back part of the difference in the review's runs, but the library keeps its stack buffers zero-initialized, as [standardqr-encoder.md](../specs/standardqr-encoder.md) decides for the encoder.

Lessons:

- A bound on the clock has to be measured on both sides in the build the suite runs in. The first bound was one second for 20,000 elements. The new checks took 0.25 to 0.35 s there in a Debug build for the URI, and 0.9 to 1.6 s when the other GS1 classes ran beside them (the review's runs), so the test now runs apart from the other tests of its process, on 40,000 elements, with five seconds.
- A test that shows by the clock that slow code did not run stops showing it when the code gets fast. The test of the size forms held their answer from the length by the seconds the checks took. Its next form read an allocation count on two texts of millions of characters, and held the answer only past 1.5 million. It now reads the pool's rent events at each member's own capacity.
- A bound on the clock holds only for the shapes it runs. Every shape had the key every requisite names first, and at most four AIs, so a requisite found by a scan and a repeat check that settled 32 AIs a pass without noting them both passed. A shape is needed for each place a scan could meet what it looks for late.
- The layout of the set cannot be wrong in a way a text shows. Any layout that gives each AI its own bit behaves the same, so the faults planted on it were of another kind: bits that ignore the AI's length, a range one bit short, a range read in its first word only.
- No requisite of this dictionary release names its own AI, where 368 AIs are matched by an exclusion pattern of their own. The rule that an element is not its own requisite is kept from the old passes, and no text reaches it.
- A probe that runs several versions of a class in one process compiles the methods they share once, with one profile, and with the JIT's default settings its readings of one version moved between processes: the old code's refusal at the first element read 72 ns in one run and 39 ns in the next. A claim about how the JIT compiles a method, and the fix it leads to, are timed with a process for each version. The second review round made a fix on such a reading and undid it (the entry below).
- The paired harness refused a row the benchmark project runs: for the retail label, `TryFromDigitalLink` into a buffer as long as the original element string answers `false`, since the element string read back from the URI has its elements in the URI's order and needs one separator more. The benchmark's row for that label timed the refusal. It was corrected the same day, as the correction under the table of 2026-10-12 says.

### Review of the linear checks (2026-10-15)

Done: an adversarial review of the two commits of 2026-10-14, the linear checks and the benchmark's correction (three reviewers over the code and its cost, the tests and the benchmark, and the documents and comments, then three independent verifiers a claim). 23 claims went to the verifiers. One was removed with three votes against it: that "the day after" contradicts the review's own dates, where the sentence's antecedent is the phase, dated 2026-10-13. The others stood, and are fixed but for the second half of one:

- The repeat check took a pass over the text for each 32 AIs, so on text of many AIs that may stand together the cost per element grew with their number. Deleting the one line that noted the AIs a pass had settled made it quadratic, and no test failed, since the time-bound test's shapes held two to four AIs. The check now takes one pass with a table indexed by rank, and a test runs 159,984 elements of a set of 198 AIs in turn.
- A requisite confirmed by a scan passed the time-bound test, since the key every requisite named was the first element of every shape. Two shapes were added in which a scan meets what it looks for late.
- Refusals at the first element took up to 1.85 times as long as before the change, in the verifiers' readings of the public member. Part of it was the clear of the set before the first element was read, and `TryValidate` now clears it once the first element has passed. The claim's second half, an exclusion refused in (AI) text at 1.24 to 1.27 times the old time, is left as it is (above).
- On text that does not split, the internal `CheckPairings` gave the splitter's error where the old passes gave an exclusion, and two test comments said the library and the reference agree on every text. The reviewers' fuzzing, millions of texts in all, found no public member that differs. The contract is now in the member's summary, and the tests expect the splitter's error there.
- The reworked test of the size forms held the answer from the length only past 1.5 million characters: the Standard QR capacity in the rMQR form, and an answer past a million characters only, both passed it. It now reads the pool's rent events at each member's own capacity.
- A guard of the benchmark's setup could not fire. It is gone.
- Fifteen claims were about sentences of this plan, the spec and the comments: what the benchmark's old buffers were, three counts (the elements of the random texts, the faults the first run missed, and the set's bits against the AIs a text can name), a reason that said a rule names single AIs of a range, two figures set beside figures of another run, the rules that were quadratic, which pass fills the set, the AI whose requisite names a pattern, what running alone means, an allocation said to be none, algorithm details in the spec, a plan sentence the tuning made stale, and four statements that nothing kept could check. They are corrected in place, or rest on a run of the review that says so.

### Second review round of the linear checks (2026-10-15)

Done: an adversarial review of the first round's fixes (three reviewers over the code and its cost, the tests and the benchmark, and the documents and comments, then three independent verifiers a claim). 21 claims went to the verifiers. One was removed with three votes against it: that the parity tests expect the library's own loop on text that does not split, where the member's summary names the splitter's error and the reference gives the same error on each of the corpus's 1,127 such texts. The others stood:

- Labels in which an AI repeats took 1.10 to 1.26 times as long as the committed code's in the reviewer's timing, on .NET 8 and .NET 10 with the JIT's profile on and off, since the counts below the words read all 174 words of the set and the count of bits was a call. Three words more now name the words that hold an AI, and the counts read only those. The count of bits is inlined, as is the addition to the set, which the note of its word had taken past the size the JIT inlines on its own. By the paired timing, two such labels take 0.98 to 0.99 times the committed code's, and the benchmark's three labels 0.98.
- Refusals at the first element read 1.06 to 1.50 times the old time with the JIT's default settings, in the reviewer's timing, which ran the versions side by side in one process. The cause given was that the JIT compiles a method that holds both a stack buffer and a loop once, at full optimisation and without the profile it gathers for other methods. The fix moved the elements after the first into a method of their own. Timed with a process for each version, that fix made `TryValidate` take 1.05 to 1.12 times as long on the benchmark's three labels as the code it fixed, and 1.11 times as long on a refusal at the first element. It is undone, and the refusal it was for takes 0.65 times the old time (the entry above).
- The mark of a repeat in `TryValidate` could be overwritten by the next element's, the test of an unknown AI could be limited to one place and its error lengthened, and the year given could be replaced by the clock's, each with no test failing. Tests now cover each, and the planted faults above fail them.
- Under the first value found by a scan, the shape of the time-bound test meant for it took 5.4 to 5.8 s a call on .NET 10, just past the five seconds. The shape now has 80,000 elements, and the fault takes 22.6 to 22.9 s a call (the planted faults above).
- A test asserted that more than half of the corpus's texts do not split, which a new release of the corpus could change with no fault in the library. It now asserts more than 100.
- Thirteen claims were about sentences of this plan, the spec and the comments: the text of the (AI)-text exclusion's measurement, the count of faults from the first list, the shuffled texts said to repeat with another value, a spec figure the plan did not hold, a ratio given for one label that was the range of four, a cause that explained part of a reading, the elements of the many-AI test, a gate the plan did not define, the slot size of the table not taken, a claim said to be fixed that was fixed in part, the 198 AIs called all that may stand together, a test summary that misstated its premise, and the callers of the internal check. They are corrected in place.

### Third review round of the linear checks (2026-10-15)

Done: an adversarial review of the change with the second round's fixes (three reviewers over the code and its cost, the tests and the benchmark, and the documents and comments, then three independent verifiers a claim). 10 claims went to the verifiers, and none was removed:

- A refusal at the second element costs more than before on .NET 8 and .NET Framework 4.8, 1.13 to 1.19 times the old time by the paired timing, where this plan gave only the .NET 10 figure. The entry of 2026-10-14 now gives the .NET 8 and .NET Framework figures too, and leaves the cost as it is.
- The first element's year could be taken from the clock with no test failing, since the year test put its date second and the first element is checked by a call of its own. The test now puts the date first as well.
- Eight claims were about sentences of this plan and of the comments: three test comments that still said what the second round corrected in this plan (the callers of the internal check, the shuffled texts and the 198 AIs), the range and the cause of a reading the second round's entry records, the count of a shape's elements, a comment with no antecedent, and a test summary whose conclusion did not follow. They are corrected in place.

### Fourth review round of the linear checks (2026-10-15)

Done: a review of the third round's fixes (two reviewers, over the code and the tests and over the documents and comments, then three independent verifiers a claim). It found no defect in the code or the tests. Six claims stood, all on wording: the reach of the refusal cost on .NET 8 and .NET Framework, the share an uncleared set took back, where the third round's entry said the figures are, a test's premise, a comment with no antecedent, and a parameter named for the year that held the day's offset. They are corrected in place.

### G12 decided (2026-10-15)

Done: the maintainer decided G12. A monthly scheduled workflow notices a new dictionary release and opens an issue, and the packages stay in lockstep, so a dictionary update releases all four at a minor bump. The G12 row, the update procedure's last line and the Phase 7 row say so. The workflow itself is Phase 7's. No code changed.

### Phase 7, first part: the Playground (2026-10-15)

Done: the Playground writes and reads GS1 data, on top of the FNC1 selector of the FNC1 plan's Phase 5. A "GS1 data" field beside the content chooses plain text, a GS1 QR Code or a GS1 Digital Link, and the content is then (AI) text or an element string, given to `CreateGs1` or `CreateGs1DigitalLink` on Standard QR or rMQR. A Digital Link takes an optional stem. The page shows the text the symbol holds, the element string or the URI, and the self-check reads the symbol back to that text and mode. Data the checks refuse is reported with the characters at fault, from the place `Gs1FormatException` carries. Both benchmark modes run the GS1 entry points, the span forms for encode, on the same text every iteration, since a suffix would make the data invalid. Micro QR has neither FNC1 nor a GS1 carrier, so the page hides the field and the interop refuses it there. The decode panel reads GS1 data from a symbol, an element string under FNC1 in first position or a Digital Link URI, as (AI) text with a row for each element, and shows the checks' verdict on the element string. Text in first position that does not read as GS1 data reports why. The page's About section links the third-party notice (G8). The package adds 60 KB to the published bundle, 24 KB with Brotli.

Verified by hand on a published Debug build and on the Release build CI publishes (no AOT, trimmed): the retail example as (AI) text and as an element string in a GS1 QR Code, and GS1 data in rMQR, which read back as GS1. The same data as a Digital Link under the canonical stem and under another domain with a path. A stem with another scheme refused, (8200) refused for a URI with its place, and a wrong check digit and a date that does not exist refused with the characters at fault. The decode panel on each carrier, on an element string with a wrong check digit, which reads with the checks' error, and on text in first position that is not GS1 data. Both benchmark modes under each carrier, a share link that restores the carrier and the stem, Micro QR with the field hidden, and the FNC1 modes beside it unchanged. The console stayed clear.

Lessons:

- Reading is not checking. `TryRead` gives (AI) text for an element string whose check digit is wrong, as a reader that shows what a symbol says should, so a view that presents the data as GS1 runs `TryValidate` beside it.

Left for Phase 7: the documents, the update procedure moved to the fetch script, the monthly workflow, and folding this plan.

Benchmarks: none. Only the Playground changed.

### The Playground's data format (2026-10-16)

Done: the maintainer asked for GS1 and FNC1 as one control. The Playground's "GS1 data" field and the FNC1 mode the FNC1 plan's Phase 5 put among the QR parameters are now one "Data format" field beside the content: plain text, GS1 QR Code, GS1 Digital Link, or an AIM format, which is FNC1 in second position with the application indicator beside it. First position is written only as a GS1 QR Code, through `CreateGs1`, so a symbol the page marks as GS1 data has passed the checks. The separate FNC1 field let any text be written under first position with no check. A share link holds the format as one value.

Verified by hand on the Release build CI publishes (no AOT, trimmed): each format on Standard QR, GS1 QR Code on rMQR, Micro QR with the field hidden and a GS1 format left selected, which writes plain text, the stem and the indicator shown for their own format alone, and a share link that restores an AIM format with its indicator and a separator in the content. The console stayed clear.

Benchmarks: none. Only the Playground changed.

### Phase 7, second part: the documents (2026-10-16)

Done: the documents of the list above.

- README: a GS1 section after the decoders. It says which carrier serves where, gives the two methods with `(AI)` text and with a stem, the error a failed check throws, the read after a decode and why `TryRead` is not a check, and the conversions on their own. It asks for `EciMode` at its default, says the default quiet zone is GS1's 4X, that GS1 does not list rMQR and excludes Micro QR, names the classes C# 12 and 13 call, and says what `DictionaryRelease` is for. The API overview and the FNC1 paragraph link it, and the license section names the third-party notice. Every code block was compiled and run as written, and the outputs its comments quote are the ones it printed.
- [qrcode-symbologies.md](../specs/qrcode-symbologies.md): four packages from three assemblies, the namespaces, "Why four packages" with the license as the fourth package's reason, the cadence and what a release can change, and G12's shipping decision. The noticing goes in with the workflow.
- The four symbology records link the "GS1 data" section where they describe FNC1, and the decoder records name `TryRead`.
- [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md): what the corpus checks, with `Gs1SymbolTest`'s figures (998 inputs, 677 taken, 632 URIs, 45 refused for a URI, one difference of form) and `Gs1PairingParityTest`.
- The approved listing above gained the constructors of `Gs1Error` and `Gs1FormatException` and the summary `Gs1ErrorKind` shipped with.
- The update procedure moved into the header of the fetch tool. At the maintainer's request the tool is now a C# file-based app, `tools/get_gs1_syntax_engine.cs`, beside the repository's other file-based tools, in place of `get-gs1-syntax-engine.ps1`. It keeps the pins and the checks. It runs where PowerShell is not installed, and there it fetches and checks the Windows console app without starting it. It starts the app in the app's own folder, where the app reads the dictionary file the zip carries instead of warning that the file is missing. Step 1 of the procedure in this plan said the import copies the linter vectors. It does not, `regenerate-gs1` imports them, and the step now says so.

Verified: the app against the oracles already fetched, and in an empty tree, where it downloaded and cloned every part and each file matched the one fetched before (the console app's folder, the dictionary clone and the 471 files of the engine source). A zip with a byte appended was refused and removed, and a wrong commit pin was refused. `import-gs1-syntax-dictionary` rewrote `PROVENANCE.md` with only the line that names the app changed, and `Gs1FixtureTest`, `Gs1AiTableGeneratorTest` and `Gs1LinterTest` pass on both frameworks.

Left for Phase 7: the monthly workflow, with the spec's sentence on noticing, and folding this plan.

Benchmarks: none. No library code changed.

### Phase 7, third part: the monthly workflow (2026-10-16)

Done: `.github/workflows/gs1-dictionary.yaml`, the noticing half of G12. On the 1st of every month and on demand it reads the release the generated table names, the one the package ships, which a test holds to the committed dictionary, and the newest date tag of gs1/gs1-syntax-dictionary. When the two differ it opens an issue titled with the newest release, which links the release's CHANGES file and the diff between the two tags and points to the update steps in the fetch app's header. A release with an issue already, open or closed, gets no second one, so an issue closed without an update is not reopened each month. A pin missing or written twice, or a repository with no date tag, fails the run instead of passing in silence. The "GS1 data" section of the symbologies record says how a release is noticed and that GitHub stops scheduled workflows after 60 days without activity in a public repository, and the fetch app's header points to the workflow.

Verified: actionlint and shellcheck pass. The two scripts, taken from the workflow as bash receives them, ran through each case. On this repository both releases are 2026-01-27 and no issue step runs. A generated file with no pin and one with two fail with an error. An older pin in a CRLF file gives the two releases. With a stand-in for the issue search, no issue and only a similar title each print the issue it would open, and the exact title among others opens none. The real search found an open and a closed issue of this repository by their exact titles. Every link in the body resolves. No issue was opened and the workflow has not run on GitHub, since a schedule runs only from the default branch.

Left for Phase 7: folding this plan.

Benchmarks: none. No library code changed.
