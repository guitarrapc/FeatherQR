# GS1 support: research

Research behind [gs1-support-plan.md](../gs1-support-plan.md), so a later session can continue without surveying again. It is deleted with the plan. The bit stream below it (FNC1 mode indicators, the `%` rule, the oracle probe of FNC1 writers) is in [fnc1-support-research.md](fnc1-support-research.md) and is not repeated here. Code references are at `main` f4b74de (2026-10-10). Anything marked UNCONFIRMED was not read in a primary source.

## Sources

| Source | What was read |
|---|---|
| GS1 General Specifications, Release 26.0 (ratified January 2026) | Read through GS1 Portugal's copy (https://gs1pt.org/wp-content/uploads/2026/02/Gen-Specs-2026-compressed.pdf, 579 pages), because the official PDF at https://ref.gs1.org/standards/genspecs/ is too large for the fetch tool. Identity of the two files is UNCONFIRMED. Section numbers below are Release 26.0's. The next release is planned for January 2027 |
| GSCN index, 2013-2026 (https://ref.gs1.org/standards/genspecs/gscn/) and GSCN 22-309 (2D in retail, Digital Link) | Searched for rMQR, Micro QR and Digital Link entries |
| GS1 Digital Link URI Syntax, release 1.7.0 (ratified August 2026, https://ref.gs1.org/standards/digital-link/uri-syntax/) | §1, §3, §4.1-§4.12, §6.1.2. Earlier releases: 1.6.0 (February 2025), 1.5.0 (June 2024) |
| GS1 Digital Link URI: Compression Technical Standard for EPC binary strings, release 1.0.0 (July 2025) | Its existence and that it supersedes the legacy compression |
| 2D Barcodes at Retail POS Implementation Guideline, release 1.1.0 (December 2025, https://ref.gs1.org/guidelines/2d-in-retail/) | §2, §4.1, §4.1.2 |
| GS1 Barcode Syntax Dictionary (https://github.com/gs1/gs1-syntax-dictionary), release 2026-01-27 | The format header, the entry lines, the linter names, the license file |
| GS1 Barcode Syntax Engine (https://github.com/gs1/gs1-syntax-engine), release 1.4.1 (2026-05-28) | Bindings, release assets, `gs1encoders.h`, `scandata.c`, `dl.c` |
| Local clones in `.references/` | zxing-cpp v3.1.1+90, CodeGlyphX v3.0.0+1, go-qr v2.6.0, qrcode-rust2 (crate `qrcode2`) v0.18.0+65, zxing.net 0.16.7+217 |
| zint 2.16.0 (2025-12-19), BWIPP release 2026-10-14 | `backend/gs1.c`, `backend/gs1_lint.h`, `backend/qr.c`, `docs/manual.txt`, BWIPP `gs1process.ps.src`, `gs1qrcode.ps.src`, `gs1dlqrcode.ps.src`, `qrcode.ps.src` |
| ISO/IEC 18004 | Not read for this layer. The symbology identifiers below are corroborated by the 2006 edition's Annex F (see the FNC1 research) |

## GS1 carriers in the QR family

- GS1 defines two QR carriers. GS1 QR Code (§5.7) carries GS1 element strings behind FNC1 in first position and is transmitted as `]Q3`. QR Code (§5.10) is "only used by the GS1 system to encode the GS1 Digital Link URI syntax" and is transmitted as `]Q1` (Table 5-2). A Digital Link URI is therefore plain QR with no FNC1. GenSpecs has no sentence that says "shall not use FNC1". The rule follows from `]Q1` and from the Digital Link standard's §1 note, which defines "QR Code symbology" as ISO/IEC 18004 "excluding the GS1 QR Code that recognises the FNC1 character".
- GS1 DataMatrix (§5.6) and Data Matrix with a Digital Link URI (§5.9) are the same pair for Data Matrix.
- Micro QR is "not supported in the GS1 system" (§5.7.1, again in §5.7.2).
- rMQR (ISO/IEC 23941) is not a GS1 carrier. GenSpecs 26.0 has no occurrence of "rMQR", "23941" or "Rectangular Micro", and the GSCN index 2013-2026 has no rMQR or Micro QR entry. Whether a non-public work request exists is UNCONFIRMED (the work request page returned 403).
- GenSpecs cites ISO/IEC 18004:2015, which ISO lists as revised by 18004:2024.

## GS1 QR Code rules (GenSpecs 26.0)

- FNC1 in first position is mandatory. §5.7.4.3 says `]Q3` identifies "GS1 system compliant symbols that have a leading FNC1", with the example `]Q30110012345678902`: the leading FNC1 is not transmitted, separators go out as GS (0x1D).
- FNC1 in second position appears nowhere in GenSpecs. It is AIM application-indicator data (`]Q5`), not GS1.
- ECI and Structured Append are "Not supported for the GS1 system" (§5.7.3). Kanji mode is not supported either (§5.7.2). Byte mode defaults to ISO/IEC 8859-1 (§5.7.2). By contrast, GS1 DataMatrix lists ECI as available (§5.6.2).
- The separator in QR shall be GS or `%` and is always transmitted as GS (§7.8.4).
- Versions 1-40 are all allowed (Table 5-35). "Permissible data is specified by the application standards" (§5.7.4.1). No error correction level is mandated, with one exception: symbol specification table 12 (EU tobacco, Regulation 2018/574), where level H "shall be presumed to fulfil" the about 30 % recovery requirement (Table 5-58 footnote).
- The quiet zone is 4X on every side (§5.7.4.2 and every QR row of the symbol specification tables), the library's default.
- Print quality is graded per ISO/IEC 15415, with an aperture normally 80 % of the minimum X-dimension (§5.7.4.5).
- X-dimension ranges (mm, minimum / target / maximum) that name QR: SST 1 addendum 1, AI 8200 (Table 5-45): 0.396 / 0.495 / 0.743. SST 1 addendum 2, QR Code with a Digital Link URI at retail POS (Table 5-46): 0.396 / 0.495 / 0.990. SST 3 addendum 1, retail POS and general distribution (Table 5-49): 0.743 / 0.990 / 0.990. SST 12, tobacco unit packs: 0.380 to 0.990. SST 13, long-distance scanning: 0.495 to 3.50. QR also appears in SSTs 2, 4, 5, 7, 8, 9, 10 and 11.
- Trap: Table 5-36 prints version 7-M data codewords as "24" where ISO/IEC 18004 gives 124.

## Character sets, separators, check digits, dates

- Character sets (§7.11): CSET 82 (Table 7-20, a subset of printable ASCII) for every AI but two. CSET 39 (Table 7-21) for AI 8010 (CPID). CSET 64, base64url with `=` padding (Table 7-22), for AI 8030 (DigSig). AIs 4300-4320 may carry RFC 3986 percent-encoding. All three sets are ASCII, so a GS1 element string never needs UTF-8 or an ECI header.
- AI length (2, 3 or 4 digits) follows from the first two digits (Table 7-5).
- Separators (§7.8.3-§7.8.6): a separator follows every element except the last, unless the AI's first two digits are in the predefined-length table (Table 7-6), which GS1 says "will remain unchanged". Every other AI, fixed length or not, needs a separator unless it is last. The table's lengths count the AI's characters too, and bracketed prefixes are reserved, not assigned:

  | Prefixes | Length |
  |---|---|
  | 00 | 20 |
  | 01, 02, 03, 41 | 16 |
  | (04) | 18 |
  | 11, 12, 13, (14), 15, 16, 17, (18), (19) | 8 |
  | 20 | 4 |
  | 31-36 | 10 |
- Mod-10 check digit (§7.9.1, Table 7-8, weights 3 and 1 from the right, check digit last): AIs 00, 01, 02, 03, 253, 255, 402, 410-417, 8003 (over the N13 after its leading `0`), 8006, 8017, 8018 and 8026. AI 401 has none.
- Check character pair (§7.9.5, Tables 7-18 and 7-19): mod 1021,32 with prime weights from the right, over a 32-character set without 0, 1, I and O. Used by AIs 8013 (GMN) and 8014.
- Dates: `00` as the day is allowed for AIs 11, 12, 13, 15, 16, 17, 4324 and 4325 (linter `yymmd0`). A real day is required for 4326, 7003, 7006, 7007, 7011 and 8008. AIs 7250 and 7251 use `yyyymmdd`. Two-digit years resolve in a window of -49 to +50 years from the current year (§7.12, Figure 7-17).

## Human-readable interpretation (§4.14)

- AIs are shown in parentheses (rule 2c). Separators are not shown (rule 2b). Spaces shall not be encoded but may be shown (rules 2a-iv, 2a-v). An element string is never split across lines (rule 1e). Data titles (`GTIN`, `BEST BEFORE`) are non-HRI text and optional (rule 4). Showing a Digital Link URI as text is the brand owner's choice (rule 3a).
- Parenthesised or bracketed input is not a GS1 encoding input standard. Table 1-22 says the parentheses "are not part of the syntax". It is a tool convention: the Syntax Engine takes `(01)…(10)…` through `setAIdataStr` and needs a literal `(` in data escaped as `\(`, and takes unbracketed data with `^` for FNC1 through `setDataStr`.

## Digital Link URI (release 1.7.0)

- Path: one primary key, then its key qualifiers in a fixed order. A reversed order is invalid (§4.9). GTIN is always 14 digits, and convenience aliases such as `/gtin/` were removed in 1.3.0 (§4.1).
- Query: data attributes as `key=value` (§4.10). Batch/lot (AI 10) may also go in the query with SSCC or CONTENT. AIs 8200, 03 and 8014 cannot appear. Extension keys must be non-numeric, and `linkType` and `context` are reserved (§4.10.1). If a key repeats, the last value wins (§3).
- Reserved characters are percent-encoded (§4.2). Any scheme, host and path prefix may come before the GS1 part (the stem, §4.11). The canonical form uses `https://id.gs1.org` with query keys sorted lexically (§4.12). No maximum length is set.
- Primary keys and qualifiers, from the dictionary's `dlpkey` attribute: 00, 253, 255, 401, 402, 8003, 8013 take none. 01 takes 22, 10, 21 in that order, or 235. 8006 takes 22, 10, 21. 414 takes 254 or 7040. 415 takes 8020. 417 and 8004 take 7040. 8010 takes 8011. 8017 and 8018 take 8019.
- Compression: the legacy algorithm is frozen at Digital Link 1.1.4 (§6.1.2) and superseded by the separate EPC binary compression standard 1.0.0. GSCN 22-309 (November 2022) adopted the uncompressed form only for retail 2D, and SST 1 addendum 2 and SST 3 addendum 1 say the URI "SHALL use the uncompressed form".
- Retail POS: GS1 Application Standard 1 (§8.2, Table 8-1) lists GS1 DataMatrix, Data Matrix with a Digital Link URI and QR Code with a Digital Link URI as the future 2D carriers, conformant once POS reads all three GS1 syntaxes (plain, element string, Digital Link). The retail guideline sets an end-2027 goal, not a mandate, keeps the linear barcode until 90 % of POS solutions read 2D (§4.1), and mentions scanners that convert a Digital Link URI to an element string (§4.1.2). GS1 QR Code with element strings is not a retail POS option. It is used in healthcare, logistics, assets, AI 8200, tobacco, direct part marking and internal applications.

## Symbology identifiers

`]Q0` Model 1, `]Q1` Model 2 without ECI, `]Q2` with ECI, `]Q3` / `]Q4` FNC1 in first position without / with ECI, `]Q5` / `]Q6` FNC1 in second position without / with ECI. GenSpecs Table 5-2 uses `]Q3` for GS1 QR Code and `]Q1` for QR Code with a Digital Link URI. The Syntax Engine's `scandata.c` reads `]Q1` as plain data and `]Q3` as GS1 data and keeps a trailing GS. Under GS1's rules a GS1 QR Code has no ECI, so `]Q4` is not a GS1 transmission.

## The Syntax Dictionary

- Apache License 2.0 (`LICENSE` in the repository). Whether the repository has a NOTICE file is UNCONFIRMED.
- Releases are date tags, about one a year aligned with GenSpecs, each change citing its GSCN: 2026-01-27 (latest), 2025-01-30, 2024-06-10, 2023-12-11, 2023-09-22, 2023-07-04, 2023-03-22, 2022-11-24, 2022-08-10.
- 224 entry lines (a range such as `3100-3105` is one line), about 541 AIs once ranges are expanded.
- Line format: `AIs [Flags] Spec [Attributes] [# Title]`. Flag `*`: predefined length, no separator needed (72 lines). Flag `?`: may be a Digital Link data attribute (every line except 03, 21, 22, 235, 254, 7040, 7041, 8011, 8014, 8019, 8020, 8040-8043 and 8200).
- Spec: one or more components, each a type (`N` digits, `X` CSET 82, `Y` CSET 39, `Z` base64url) with a fixed (`N6`) or variable (`X..20`) length. Only the last component may vary. Bracketed components are optional and no mandatory component follows an optional one. Each component may name linters.
- Attributes: `req=` (alternatives with `,`, compound groups with `+`) and `ex=` give requisite and exclusive AI pairings. The header says they should be evaluated "over the combined data received from all GS1 carriers marking a physical item". `dlpkey[=…]` marks a Digital Link primary key and its qualifier sequences (`|` between alternatives). The title follows `#`.
- The 34 linters named by 2026-01-27: `csum`, `csumalpha`, `gcppos1`, `gcppos2`, `yymmd0`, `yymmdd`, `yyyymmdd`, `hhmi`, `hh`, `mi`, `ss`, `iso3166`, `iso3166alpha2`, `iso3166999`, `iso4217`, `iso5218`, `pcenc`, `latitude`, `longitude`, `yesno`, `hyphen`, `importeridx`, `packagetype`, `mediatype`, `posinseqslash`, `nonzero`, `winding`, `zero`, `nozeroprefix`, `hasnondigit`, `iban`, `couponcode`, `couponposoffer`, `pieceoftotal`. The ISO lists and the media and package type lists are not in the dictionary file. Their source is the linters' own code (UNCONFIRMED where each list lives).

## The Syntax Engine

- Apache License 2.0. A C library with C++, C# (P/Invoke, `src/dotnet-lib/GS1Encoder.cs`, net8.0), Java and Kotlin, Swift and JS/WASM (npm `gs1encoder` 1.4.1) bindings.
- No NuGet package exists. Release 1.4.1 ships prebuilt assets: `gs1encoders-windows-console-app.zip`, `gs1encoders-linux-app.tgz`, `gs1encoders-windows-dotnet-lib.zip` (with x86 and x64 native DLLs), `gs1encoders-wasm-app.zip`, `gs1encoders-jsonly-app.zip`.
- It parses bracketed and unbracketed input and Digital Link URIs, checks lengths, character sets, linters, exclusive pairs, requisites (on by default, can be turned off) and repeated AIs, generates HRI (with optional data titles), Digital Link URIs (`getDLuri(stem)`) and scan data (`]Q3…`), and can add check digits and permit unknown AIs. Its symbology list has no rMQR, but the scan-data and data-string paths are independent of the symbol.
- No official conformance vector set was found (UNCONFIRMED absence). Its unit tests (for example about 95 checks in `dl.c`) and fuzzers are the nearest thing.

## Other libraries

| Library (version read) | AI validation | Check digits | `(AI)` output | Bracketed input | Digital Link | GS1 in rMQR |
|---|---|---|---|---|---|---|
| zxing-cpp reader 3.1.1 | splits by length only, tolerates GS after any element | no | yes (`TextMode.HRI`, the default) | n/a | read as plain text | reads `]Q3` |
| zxing-cpp writer through libzint 2.16.0 (ZXingCpp NuGet 0.5.2 / 0.5.3) | libzint's built-in checks | yes | n/a | `[AI]` and `(AI)` | accepted unchecked | yes |
| libzint 2.16.0 | linters generated from the dictionary, the Syntax Engine optional (`GS1SYNTAXENGINE_MODE`) | yes | n/a | `[AI]`, or `(AI)` with `GS1PARENS_MODE` | checked only with the Syntax Engine | yes, not Micro QR |
| BWIPP 2026-10-14 | table extracted from the dictionary, plus `req` / `ex` | yes | yes | `(AI)` | `gs1dlqrcode` validates, encodes without FNC1 | raw FNC1 yes, `gs1qrcode` with rMQR UNCONFIRMED |
| CodeGlyphX 2.1.0 / 3.0.0 | generated from dictionary 2026-01-27, plus `req` / `ex` | yes | `(AI)data` | `(AI)` only | validate, parse, build (uncompressed, URI Syntax 1.6.0) | yes, validated. Its Standard QR `EncodeGs1` does not validate |
| ZXing.Net 0.16.11 | none for QR | no | no | no | no | no rMQR |
| go-qr 2.6.0 | none (its CLI has a fixed-length prefix table) | no | no | CLI only | no | no rMQR |
| qrcode2 0.18.0 | none, FNC1 bits only | no | no | no | no | FNC1 bits |
| Syntax Engine 1.4.1 | full | yes, can add | yes | yes | full | data layer only |

- zxing-cpp's table (`core/src/HRI.cpp`) is hand-maintained and cites dictionary 2025-12-08. For an unknown AI or short data it returns nothing and `TextMode.HRI` falls back to escaped text (`<GS>`).
- libzint's `gs1_lint.h` is generated from the dictionary by `backend/tools/gen_gs1_lint.php`. No `req` / `ex` check was found in its built-in path (UNCONFIRMED beyond grep). In GS1 mode it returns `ZINT_WARN_NONCOMPLIANT` for ECI or Structured Append.
- ZXingCpp's `CreatorOptions(format, "gs1")` sets `GS1_MODE`, and also `GS1PARENS_MODE` when the input does not start with `[`. It has no option for the Syntax Engine or for skipping checks. It refuses raw GS (error 251) and ignores `gs1` for Micro QR (see the FNC1 research).
- CodeGlyphX generates `Gs1ApplicationIdentifierCatalog.Generated.cs` from dictionary 2026-01-27 with `Build/Generate-Gs1Catalog.ps1`. Its `Gs1/` folder is the same in 2.1.0 (pinned here) and 3.0.0 apart from two one-line edits. Square-bracket input is UNCONFIRMED. Its validator refuses a raw GS after a fixed-length AI.
- Other .NET packages: Solidsoft.Reply.Parsers.Gs1Ai 2.0.0 (Apache-2.0, validating parser, raw or `(AI)` input), Solidsoft.Reply.Gs1DigitalLinkLib 1.1.5 (Apache-2.0, element string to and from Digital Link with compression), BarcodeParserBuilder 0.3.0 (LGPL-2.1, GS1, HIBC and PPN, reads AIM identifiers), Buskunov.ParserGS1_128 (MIT, 2021), SimpleSoft.Gs1Parser (MIT, 2023). Commercial: Aspose.BarCode has `EncodeTypes.GS1QR` and an rMQR GS1 separator option (UNCONFIRMED), IronBarcode has no GS1 QR type, Dynamsoft's code parser has a GS1 AI type based on AI definitions v24.0.

## Oracles available under the toolchain policy

| Oracle | How it is acquired | What it can check | Dictionary |
|---|---|---|---|
| CodeGlyphX 2.1.0 `Gs1Validator`, `Gs1DigitalLink` | Pinned NuGet, already referenced | validation verdicts including `req` / `ex`, `(AI)` text, Digital Link parse and build (URI Syntax 1.6.0) | 2026-01-27, the release this plan pins |
| libzint 2.16.0 through ZXingCpp 0.5.2 `gs1` | Pinned NuGet, already referenced | accept / refuse per element string, separator placement (read back through zxing-cpp with `TextMode.Plain`), GS1 QR and rMQR symbols | generated from the dictionary at its release (version UNCONFIRMED) |
| zxing-cpp 3.1.0 reader through ZXingCpp 0.5.2 | Pinned NuGet, already referenced | `(AI)` text of a symbol (`TextMode.HRI`), `]Q3`, `ContentType.GS1` | hand table citing 2025-12-08 |
| Syntax Engine 1.4.1 console app | Prebuilt release asset, pinned by version and SHA-256, fetched by a script as qrtool is | everything: verdicts, `(AI)` text, Digital Link in both directions, scan data | embedded (release UNCONFIRMED) |

## Repository facts

- The real-image corpus holds one GS1 symbol, `tests/FeatherQR.Tests/Fixtures/RealImages/zxing-cpp-samples/qrcode-2/gs1-figure-4.15.1-2.png`. Its `.txt` holds `01 09504000059101 21 12345678p901 <GS> 10 1234567p <GS> 17 141120 8200 http://www.gs1.org/demo/` (spaces added here). The GTIN's check digit is valid, 8200 has its requisite 01, and the expected `(AI)` text is `(01)09504000059101(21)12345678p901(10)1234567p(17)141120(8200)http://www.gs1.org/demo/`.
- The core is MIT. A table generated from the dictionary is a derivative of Apache-2.0 data, so the package and the repository need its license text and attribution. No third-party notice file exists in the repository today.
- Precedents for a generated table: `ShiftJisKanjiTable` (16 KB of RVA data, no allocation, no static constructor) and `SimdTiersDocTest`, which fails on drift and rewrites the output outside CI.
- The core split record measured trimmed sizes per consumer profile (QR encode only 95 KB) and rejected packages that save untrimmed consumers 100-130 KB. Unused static classes trim away, which is why each symbology has its own entry type.
- The public API growth rule: no public type on anticipation. The maintainer's request for GS1 support (2026-10-10) is the request this plan answers.
