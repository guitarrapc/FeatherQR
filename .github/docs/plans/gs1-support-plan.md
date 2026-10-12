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

An element string cannot be split without lengths, because a predefined-length element carries no separator. So formatting a decoded symbol as `(AI)` text needs the table, as the 2.0.0 plan said. A reader can still accept AIs newer than its table. The AI's own length follows from its first two digits (Table 7-5), and the predefined-length prefixes (Table 7-6) "will remain unchanged", so an unknown AI can be read as variable length up to the next separator. The Syntax Engine offers the same as "permit unknown AIs". When building, an unknown AI is more likely a typo than a new AI, so building should refuse it.

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
| `(AI)` text in both directions, with the `\(` escape for a literal parenthesis | Compressed Digital Link URIs, resolvers and `linkType` |
| Digital Link URIs in both directions: uncompressed, URI Syntax 1.7.0, any stem on input, the canonical form on output | GS1 DataMatrix, GS1-128 and DataBar, which this library does not draw |
| Documentation: which carrier to use, the two-line recipes, GS1's no-ECI rule | Print guidance (X-dimension, print grade). The README says only that the default quiet zone is GS1's 4X |
| The Playground's GS1 input and decoded-AI view | A GS1 overload on the generators (G4) |
| | GS1 on Micro QR, which has no FNC1 and is excluded by GS1 |

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
    /// <summary>Builds an element string from (AI) text such as <c>(01)09506000134352(17)201225(10)ABC123</c>, a literal parenthesis in a value written <c>\(</c>. Every check runs, pairings included: <c>false</c> with the error when the text is not valid GS1 data.</summary>
    public static bool TryFromHri(ReadOnlySpan<char> hri, out string elementString, out Gs1Error error);
    /// <summary>The span form of <see cref="TryFromHri"/>: writes into the caller's buffer and allocates nothing. <see cref="TryGetFromHriLength"/> gives the exact length, and the result is never longer than the text. A short destination fails with <see cref="Gs1ErrorKind.DestinationTooSmall"/>.</summary>
    public static bool TryFromHri(ReadOnlySpan<char> hri, Span<char> destination, out int charsWritten, out Gs1Error error);
    /// <summary>The length <see cref="TryFromHri"/> would write for this text, after the same checks: <c>false</c> with the same error where the conversion would fail.</summary>
    public static bool TryGetFromHriLength(ReadOnlySpan<char> hri, out int length, out Gs1Error error);

    /// <summary>Formats an element string as (AI) text, <c>(01)09506000134352(17)201225(10)ABC123</c>, a literal parenthesis in a value written <c>\(</c> so that <see cref="TryFromHri"/> reads the text back. Runs what splitting needs and no more: an unknown AI is read as variable length up to the next separator, a redundant or trailing separator is accepted.</summary>
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

    /// <summary>Reads the GS1 data out of a Digital Link URI with any stem: the path's key and qualifiers, then the numeric query keys in URI order, percent-decoded, a repeated key resolved to its last value, non-numeric keys ignored. Runs what reading needs: the URI's structure. A compressed URI is refused.</summary>
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
    public Gs1ErrorKind Kind { get; }
    public int Offset { get; }
    public int Length { get; }
    public override string ToString();
}

/// <summary>Thrown by the generator entry points (<c>CreateGs1</c>, <c>CreateGs1DigitalLink</c>) when the data is not valid GS1 data. Nothing on <see cref="Gs1ElementString"/> throws for its input. A <see cref="FormatException"/>, as <see cref="UriFormatException"/> is, so an existing <c>catch (FormatException)</c> still catches it, with the <see cref="Error"/> for callers that want the kind and the place.</summary>
public sealed class Gs1FormatException : FormatException
{
    public Gs1Error Error { get; }
}

/// <summary>The kinds of <see cref="Gs1Error"/>. Later phases may add members at the end.</summary>
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
    /// <summary>The same AI twice with different values, or a key repeated between path and query.</summary>
    RepeatedAi,
}

/// <summary>One element of an element string as spans into it: the AI, its value and the value's offset. The AI is 2 to 4 digits, the value excludes the separator.</summary>
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
        public static int CreateGs1DigitalLink(ReadOnlySpan<char> text, QREccLevel eccLevel, Span<byte> destination, in QRCodeGeneratorOptions options = null, string? stem = null);
        public static bool TryGetRequiredBufferSizeGs1DigitalLink(ReadOnlySpan<char> text, QREccLevel eccLevel, out QRCodeCalculatedSize size, in QRCodeGeneratorOptions options = null, string? stem = null);
    }
}

/// <summary>The same six members on <c>RmQRCodeGenerator</c> with its level, options and size types. The docs say GS1 does not list rMQR as a carrier (G10).</summary>
public static class Gs1RmQRCodeGenerator
{
    extension(RmQRCodeGenerator) { /* CreateGs1 ×2, TryGetRequiredBufferSizeGs1, CreateGs1DigitalLink ×2, TryGetRequiredBufferSizeGs1DigitalLink */ }
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
- `TryToHri` escapes a literal `(` in a value as `\(`, so `TryFromHri` reads its output back (decided 2026-10-12). zxing-cpp's `TextMode.HRI` does not escape, and the fixtures' comparison in Phase 4 unescapes before comparing.
- Readers tolerate a redundant separator after a predefined-length element and a trailing one, as the Syntax Engine does and as 7.8.6.3 asks of a processing routine, and refuse a separator at the start or two in a row. Whether `TryValidate` reports a tolerated separator is settled in Phase 3 against the fixtures (the engine accepts both).
- The generators get GS1 entry points from this package through C# 14 extension blocks (G4, decided 2026-10-12): `CreateGs1` for the GS1 QR Code carrier and `CreateGs1DigitalLink` for the retail carrier, each with the data, destination and size forms of `Create`, on Standard QR and rMQR, twelve members in two enclosing classes. The decode side gets no twin on the decoders: `TryRead` on `Gs1ElementString` takes any decoder's text and `Fnc1Mode` and gives the `(AI)` text for either carrier, so the sixteen decode entry points (eight per symbology, two of them in the rendering package) stay as they are. No other library gives its decoder a GS1 entry: zxing-cpp formats HRI in a text mode, the rest return a flag and the element string.
- Both entry points take `(AI)` text or an element string in one parameter, told apart by the first character, which is unambiguous: `(AI)` text starts with `(` and an element string with a digit.

### The table

- Source: the dictionary file of the pinned release, committed with its license and a provenance note under the test data. The generator lives in the test project, as `SimdTiersDocTest` does: it fails when the generated source differs from what the pinned release produces and rewrites it outside CI.
- Output: RVA data and a lookup by AI, with no allocation and no static constructor, like `ShiftJisKanjiTable`. Titles are not stored.
- The list linters (`iso3166`, `iso3166alpha2`, `iso3166999`, `iso4217`, `iso5218`, `mediatype`, `packagetype`) need data the dictionary does not hold. Phase 0 found it in the same repository at the same tag, `src/lint_*.c`: bit fields for the ISO 3166 numeric, alpha-2 and ISO 4217 codes and the media types, a 431-entry string list for the package types, and the `iso5218` and `iso3166999` rules in code. The seven files are committed beside the dictionary with the same provenance note.
- The generator refuses a dictionary that names a linter the code does not implement, so a release that adds one cannot be pinned without it.
- Update: once a year, after GS1's January release, as its own pull request that lists the release's GSCNs and the fixtures it changes.

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
| G4 | Encode entry point | Decided 2026-10-12: `CreateGs1` and `CreateGs1DigitalLink` on `QRCodeGenerator` and `RmQRCodeGenerator`, added by `FeatherQR.Gs1` through extension blocks, with the data, destination and size forms of `Create` (twelve members). They take `(AI)` text or an element string, run every check and set FNC1 themselves, so a caller needs to know nothing of the GS1 format and cannot forget the FNC1 option. The maintainer chose this over the earlier recommendation (compose the conversion with the FNC1 option on `Create`) after a survey of the other libraries: the ones that take an element string behind a flag (ZXing.Net, go-qr, CodeGlyphX's QR encoder) let a missing separator through, and the ones that take `(AI)` text (libzint and zxing-cpp, BWIPP, CodeGlyphX's rMQR encoder and its Code 128 helper) are the ones a caller cannot misuse. The decode side gets `TryRead` on the data layer instead of decoder twins (see the API section) |
| G5 | Check strength per member | Decided 2026-10-12 as recommended: members that build data for a symbol run every check, pairings included. Members that read data from a symbol check only what they need and accept unknown AIs. `TryValidate` runs every check. No switch to turn the pairing check off. Revisit when a caller needs to build data split across carriers, which the pairing check refuses (five of the 21 General Specifications examples in the corpus are such data) |
| G6 | Unknown AIs | Decided 2026-10-12 with the API: read as variable length up to the next separator. Refused when building |
| G7 | Error shape | Decided 2026-10-12: `Gs1Error` with a kind, an offset and a length into the input, so a caller can point at the bad element. The throwing forms throw `Gs1FormatException`, derived from `FormatException` as `UriFormatException` is, carrying the `Gs1Error`, so the one-line forms give the place too and an existing `catch (FormatException)` still works. The earlier recommendation of no new exception type was dropped because it would have forced callers who want the position onto the `Try` forms |
| G8 | License of the generated table | Decided 2026-10-12, with G2. `FeatherQR.Gs1` is MIT like the other packages (`PackageLicenseExpression` `MIT`) and carries a `THIRD-PARTY-NOTICES.md` packed into its nupkg, the way `Directory.Build.props` packs the README. The same file sits at the repository root, since the generated table lives in `src/`. It holds what Apache-2.0 section 4 asks for: the dictionary's three copyright lines (BWIPP project 2020-2021, Zint Project 2020-2021, GS1 AISBL 2021-2025), the release, the commit, the URL, a statement of what is derived (the AI table and the ISO 3166, ISO 4217, media and package type lists are generated from the dictionary and its reference linters, the linter rules are implemented in managed code and copy no C source) and the full Apache-2.0 text, because this repository's own LICENSE is MIT and cannot stand in for it. The dictionary repository has no NOTICE file (checked in Phase 0), so nothing else is owed. The generated source's header names the release, the commit and the notice file. `FeatherQR` and `FeatherQR.SkiaSharp` carry no notice. The Playground compiles the package into its WebAssembly bundle and links the notice from its page (Phase 7). The test fixtures' copy of the dictionary and the linter sources keep their own LICENSE and PROVENANCE.md beside them. The file is created in Phase 2 with the table it describes |
| G9 | Digital Link | Decided 2026-10-12 with the API, the two points left to Phase 5 settled the same day. Uncompressed, URI Syntax 1.7.0. Output in the canonical form. Input with any stem, percent-decoded, a repeated query key resolved last-wins. Non-numeric extension keys are ignored. A compressed URI is refused with its own kind. When an element string holds two primary keys, the first in element-string order leads the URI, as the Syntax Engine does. Phase 0 found that 1.7.0 lists (7041) and (8040) to (8043) as data attributes while dictionary 2026-01-27 does not flag them `?`, and both dictionary-driven oracles refuse them in a URI. Phase 5 decided for the table: the flags are the dictionary's to give, the oracles agree, and the next release the table is regenerated from lifts them. Every other key, qualifier and attribute of 1.7.0 is in the dictionary with the same flags, so 1.7.0 needs no data newer than the pinned release |
| G10 | rMQR | No refusal. The layer does not see the symbol, and readers do read GS1 rMQR. The docs say GS1 does not list rMQR as a carrier (General Specifications Release 26.0), so a GS1 scanner may not accept it |
| G11 | Where the record lives | A "GS1 data" section in [qrcode-symbologies.md](../specs/qrcode-symbologies.md), since shared content lives only there. If it outgrows a section, a `gs1-data.md` design record and a guidelines change, decided with the maintainer then |

A lead outside the scope: the scheme and host of a Digital Link URI are case-insensitive, so an uppercase stem would let Alphanumeric mode carry it under `Optimal`. It needs the Digital Link standard's view and phone readers checked first.

## Equivalence classes

- AIs: 2-, 3- and 4-digit AIs. Range AIs (the decimal-point digit of 31nn-36nn). Predefined length, fixed length outside the predefined table (needs a separator), variable length. Optional and multi-component specs (`7007`, `8008`).
- Separators: after a variable-length element not last, after a fixed-length element outside the predefined table, none after a predefined-length element, none after the last. On read, a redundant one after a predefined-length element, a trailing one, two in a row (an empty field, refused).
- Character sets: each of `N`, `X`, `Y`, `Z` at its boundary characters, a character outside it, a non-ASCII character, `Z` padding.
- Each of the 34 linters: valid, the boundary, invalid.
- Dates: day `00` with `yymmd0` and without, 29 February in a leap and a common year across the -49 / +50 window, month 00 and 13.
- Pairings: a single requisite, a compound requisite (`+`), an exclusion, a repeated AI with an equal and an unequal value.
- Unknown AIs: read, refused when building.
- `(AI)` text: an escaped `\(`, an unbalanced parenthesis, an empty value, a non-digit AI, whitespace.
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
- Costs: a small benchmark class for the data layer's conversions and `TryValidate` on three typical element strings, recorded, not tuned. Allocation tests on the span forms. The trimmed size of a QR-encode-only application, unchanged, and of one that calls `FromHri`, recorded. The AOT analysis project calls every member with no warning. The Playground runs it under WebAssembly.

## Phases

| Phase | Work | Exit |
|---|---|---|
| 0 | Premises and fixtures, no `src/` change. G8 confirmed. The list linters' data source found and pinned. A pinned fetch of the Syntax Engine 1.4.1 console app, with its embedded dictionary release recorded. The input corpus and a `regenerate-gs1` command in `tools/QRInteropFixtures`. Whether Digital Link 1.7.0 depends on data newer than dictionary 2026-01-27. A size estimate of the table | Fixtures committed. Oracle disagreements listed with their cause |
| 1 | The API: names, signatures, XML summaries and the recipes for the three jobs, reviewed by the maintainer before code. The package's namespace and entry type name (G2), G4, G5 and G7 settled | The approved sketch recorded here |
| 2 | The `FeatherQR.Gs1` project and package (lockstep version, the `check_package_deps` graph, its own `PublicAPI.approved.txt`, the notice file of G8), then the table: the generator test, the generated table, the AI lookup, all internal | Drift test green. Size measured. The `FeatherQR` and `FeatherQR.SkiaSharp` packages unchanged apart from the version |
| 3 | Reading and checks: the split, the linters, the pairings, `TryValidate`, `EnumerateElements` | Verdicts match the Syntax Engine fixtures, disagreements listed by cause. No allocation. A fuzz run never throws |
| 4 | `(AI)` text: `TryToHri` and `TryFromHri`, their span forms and length twins | The `(AI)` text and separator checks above. Round trips |
| 5 | Digital Link: both directions, and `TryRead` | The Syntax Engine's Digital Link vectors and fixtures match. CodeGlyphX agrees where URI Syntax 1.6.0 and 1.7.0 agree |
| 6 | Symbols, after the FNC1 plan's Phases 3 and 4: the end-to-end tests, the compliance property, the corpus photograph, the benchmark class | zxing-cpp's reads match. Encode and decode benchmarks within noise |
| 7 | Playground (GS1 input as `(AI)` text, the Digital Link builder, the decoded AIs, on top of the FNC1 plan's encode selector), the documents below, the approved API listing, then this plan folded and deleted with its research file | The Playground publish green and checked by hand |

Phases 0-5 touch no symbol code and can run beside the FNC1 plan. Only Phase 6 waits for it. This layer and the FNC1 plan's encode phases are released together (G1). If the 2.0.0 freeze comes first, both wait on a branch and merge after 2.0.0 is released.

## Documents to change

- [README.md](../../../README.md): the `FeatherQR.Gs1` install line beside the others, and a GS1 section. Which carrier to use (Digital Link for retail, GS1 QR Code elsewhere), the two-line recipes, leave `EciMode` at its default, rMQR's carrier status. No numbers.
- [qrcode-symbologies.md](../specs/qrcode-symbologies.md): the "GS1 data" section (the table and its update, the check strength per member, the carrier rules), the package topology (its scope row, the package table with `FeatherQR.Gs1` depending on `FeatherQR`, the packing paragraph and "Why three packages" become four, with the license isolation as the reason the fourth exists), the extension-member paragraph (the generators gain a second set, with `Gs1QRCodeGenerator` and `Gs1RmQRCodeGenerator` as the named fallback), scope rows (GS1 data in, compressed Digital Link, titles and scanner strings out, each with a revisit condition) and lessons.
- `tools/check_package_deps.cs`: the fourth package's dependency group per target framework (`FeatherQR` at the shared version).
- [qrcode-test-fixtures.md](../specs/qrcode-test-fixtures.md): the GS1 oracles, the Syntax Engine under the toolchain policy, the GS1 fixtures and what each oracle's release explains.
- [standardqr-encoder.md](../specs/standardqr-encoder.md), [rmqr-encoder.md](../specs/rmqr-encoder.md) and the two decoder records: a link to the GS1 section where they describe FNC1.
- The third-party notice file (G8) and the XML docs of the new members.

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

Done: the first public surface of `FeatherQR.Gs1`, five types: `Gs1ElementString` with `TryValidate` and `EnumerateElements`, `Gs1Error`, `Gs1ErrorKind` (21 members), `Gs1Element` and `Gs1ElementEnumerator`. Behind them, all internal: the split (`Gs1Splitter`, one rule for every reader and check), the four character sets (`Gs1Charsets`), the 34 linters in managed code from the rules of the General Specifications and the standards they cite (`Gs1Linters`, the two coupon formats in their own file), and the validator (`Gs1Validator`: components, then the rules between elements). The package's `PublicAPI.approved.txt` and the two CI lines deferred from Phase 2 are in place. The first listing of a `ref struct` showed the two API tools printing the compiler's compatibility `[Obsolete]` on it and no `ref`, so both now render `ref struct` and ignore that attribute on by-ref-like types, and the two older listings are unchanged by the fix. The tests, 3,788 on the two frameworks: every linter against its imported unit tests (5,656 vectors, the two GCP linters by explicit cases since their vectors are built in code), the character sets against theirs, `TryValidate` against the Syntax Engine's verdict on every comparable fixture (993 element strings, the per-AI and imported vectors among them, with the error class compared where the input was an element string), `EnumerateElements` against the engine's own split of every accepted string (680), the plan's equivalence classes as explicit cases, the error type, zero allocation in Release on the span paths, and a fuzz of 12 mutations per corpus input plus 5,000 random strings that never throws.

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

Done: the nine remaining data-layer members of the approved listing, `TryToDigitalLink` and `TryFromDigitalLink` in string and span forms with their length twins, and `TryRead` with its span form and `TryGetReadLength`, behind two internal files (`Gs1DigitalLink`, `Gs1Reader`). Building runs every check, takes the first element whose AI is a primary key, chooses the key's qualifier sequence that matches the most elements among those that leave only data attributes for the query (failing any such, the first that matches the most, as the engine does), writes the key and its present qualifiers in the sequence's order, then the other AIs as query parameters in the lexical order of the AIs with each AI once, every character outside the unreserved set percent-encoded as UTF-8. The stem is checked once per call and a bad one throws `ArgumentException` from every form. Reading follows the engine's parser: a case-insensitive `http` or `https` scheme, a host, the path walked backwards in `/AI/value` pairs to the first primary key so any stem may precede it, the qualifiers after the key held to one of the key's sequences, numeric query keys taken as AIs (an unknown one refused, a non-numeric one ignored, a repeated one resolved to its last value), a key the path holds or that belongs in the path refused, `+` a blank in the query only, a fragment ignored. Values are read as they are except what the element string needs to split back into the same elements: a predefined-length AI's value must have its length, and an escape may not decode to a control character. `TryRead` formats the element string under FNC1 in first position and otherwise reads the text as a URI into a scratch element string and formats that, so its errors point into the text. The approved listing gained the nine members and is otherwise unchanged.

G9's two open points are settled above (the table wins on (7041) and (8040) to (8043), the first primary key leads). Five deliberate differences from the engine, each held by a test as a listed exception and recorded in the fixture spec: a repeated query key resolves to its last value (G9, the engine refuses any repeat, equal values included), the scheme is case-insensitive (RFC 3986, the engine takes `HtTp://` for plain data), a 13-digit GTIN in the query is not padded to 14 (URI Syntax 1.7.0 section 4.1, the engine pads), a malformed escape is refused (the engine keeps it as text), and (01)(235)(10) builds as `/01/…/235/…?10=…` where the engine strands (235) by taking the first longest sequence.

Tests, 1,964 per framework in `Gs1DigitalLinkTest` and `Gs1ReadTest`: `TryFromDigitalLink` on every URI of the corpus (186: the engine's element string on the 112 it read, a refusal or data `TryValidate` refuses on the 70 it refused, `NotAUri` on the 4 it took for plain data), `TryToDigitalLink` on every element string the engine built from or refused to (628 URIs equal to the engine's with its query sorted, which is CodeGlyphX's URI on all 618 it built, and 48 refusals with the engine's cause), both round trips, the equivalence classes with their offsets (stems, schemes, hosts, illegal characters, the compressed form, each path and query rule, each escape fault), stems that throw, the span forms, their length twins and the one-short destination, and `TryRead` on the engine's `]Q1` and `]Q3` scan-data vectors (16) and on every element string and URI it read (136). The fuzz test runs every conversion and the read on every mutated and random string, and checks that a URI that reads gives an element string that splits and that a built URI reads back. The Release-only allocation test covers the seven new span forms. The full suite is 56,290 tests on the two frameworks with none failing.

Lessons:

- The engine's generator and parser disagree on (01)(235)(10). The parser accepts `/01/…/235/…?10=…`, the generator strands (235) because it picks the first sequence with the most matches before asking what is left over. Preferring a sequence that leaves only data attributes behind makes the two directions agree, at one more pass over the elements.
- A reader that converts must keep the split. Reading a URI value as it is would let `/01/123` through, and the element string `01123` splits as (01) with a value cut short, so `TryRead` could fail after `TryFromDigitalLink` succeeded, with an offset into a string the caller never saw. Two value rules belong to reading for that reason, the predefined length and the control characters, and nothing else about a value.
- The standard does not say what a compressed URI looks like to a reader that does not decompress. The legacy compression is frozen and retail forbids it, so the kind is named by a heuristic on the final path segment (ten or more base64url characters with both letter cases), and only after no key was found, so no valid URI can hit it.

Benchmarks: not applicable, no change to the core. The GS1 benchmark class comes with Phase 6.
