# Data Capacity Reference

The actual capacity depends on the QR code type, encoding mode, ECC level, and version. Below are **practical capacities** including overhead, measured through the generators with 'あ' as the test character for UTF-8 multi-byte text and for Kanji mode.

## Standard QR Code (Versions 1-40)

### Quick Reference (Version 10, Common Use Cases)

| ECC Level | Numeric | Alphanumeric | Byte (ASCII) | Byte (UTF-8 Multi-byte*) | Kanji |
|-----------|---------|--------------|--------------|--------------------------|----------------------|
| L | 652 | 395 | ~270 | 90 | 167 |
| M | 513 | 311 | ~210 | 70 | 131 |
| Q | 364 | 221 | ~150 | 50 | 93 |
| H | 288 | 174 | ~117 | 39 | 74 |

> UTF-8 multi-byte: Japanese hiragana 'あ' (3 bytes/char). For ASCII text (1 byte/char), the capacity is approximately 3× the values shown.
>
> Kanji: the same 'あ' in Kanji mode, which the generators write only with `AllowKanji` (13 bits a character and no ECI header, about 1.85× the UTF-8 column). It applies to text whose every character has a Kanji-mode cell (JIS X 0208, kana and kanji included); see [Kanji mode on request](migration.md#kanji-mode-on-request-allowkanji).

**Full capacity tables** for all Standard QR Code versions (1-40) and ECC levels are available in the [Data Capacity Tables](#data-capacity-tables) section below.

### Data Capacity Tables

Full capacity tables for all Standard QR Code versions and ECC levels.

**Test Characters:**
- Numeric: `'1'` (digit)
- Alphanumeric: `'A'` (uppercase letter)
- Byte (ASCII): `'a'` (lowercase, 1 byte)
- Byte (UTF-8): `'あ'` (hiragana, 3 bytes)
- Kanji: `'あ'` (one JIS X 0208 cell, 13 bits), with `AllowKanji`

**Important Notes:**
- **Numeric**: Pure digit count (0-9)
- **Alphanumeric**: Uppercase letters, digits, and symbols (45 character set)
- **Byte**: UTF-8 encoded Japanese characters (ひらがな 'あ' = 3 bytes per character)
  - For ASCII characters (1 byte each), multiply the byte value by ~3
  - For theoretical byte capacity, refer to ISO/IEC 18004 Table 7
- **Kanji**: characters in Kanji mode, written only with `AllowKanji`; the column equals ISO/IEC 18004 Table 7's Kanji column

#### ECC Level: L

<details><summary>Click to expand full capacity tables</summary>

| Version | Numeric | Alphanumeric | Byte (UTF-8 Multi-byte*) | Kanji |
|---------|---------|--------------|------|------|
|  1 |      41 |           25 |    5 |    10 |
|  2 |      77 |           47 |   10 |    20 |
|  3 |     127 |           77 |   17 |    32 |
|  4 |     187 |          114 |   25 |    48 |
|  5 |     255 |          154 |   35 |    65 |
|  6 |     322 |          195 |   44 |    82 |
|  7 |     370 |          224 |   51 |    95 |
|  8 |     461 |          279 |   63 |   118 |
|  9 |     552 |          335 |   76 |   141 |
| 10 |     652 |          395 |   90 |   167 |
| 11 |     772 |          468 |  106 |   198 |
| 12 |     883 |          535 |  122 |   226 |
| 13 |    1022 |          619 |  141 |   262 |
| 14 |    1101 |          667 |  152 |   282 |
| 15 |    1250 |          758 |  173 |   320 |
| 16 |    1408 |          854 |  195 |   361 |
| 17 |    1548 |          938 |  214 |   397 |
| 18 |    1725 |         1046 |  239 |   442 |
| 19 |    1903 |         1153 |  263 |   488 |
| 20 |    2061 |         1249 |  285 |   528 |
| 21 |    2232 |         1352 |  309 |   572 |
| 22 |    2409 |         1460 |  334 |   618 |
| 23 |    2620 |         1588 |  363 |   672 |
| 24 |    2812 |         1704 |  390 |   721 |
| 25 |    3057 |         1853 |  424 |   784 |
| 26 |    3283 |         1990 |  455 |   842 |
| 27 |    3517 |         2132 |  488 |   902 |
| 28 |    3669 |         2223 |  509 |   940 |
| 29 |    3909 |         2369 |  542 |  1002 |
| 30 |    4158 |         2520 |  577 |  1066 |
| 31 |    4417 |         2677 |  613 |  1132 |
| 32 |    4686 |         2840 |  650 |  1201 |
| 33 |    4965 |         3009 |  689 |  1273 |
| 34 |    5253 |         3183 |  729 |  1347 |
| 35 |    5529 |         3351 |  767 |  1417 |
| 36 |    5836 |         3537 |  810 |  1496 |
| 37 |    6153 |         3729 |  854 |  1577 |
| 38 |    6479 |         3927 |  899 |  1661 |
| 39 |    6743 |         4087 |  936 |  1729 |
| 40 |    7089 |         4296 |  984 |  1817 |

</details>

#### ECC Level: M

<details>
<summary>Click to expand full capacity tables</summary>

| Version | Numeric | Alphanumeric | Byte (UTF-8 Multi-byte*) | Kanji |
|---------|---------|--------------|------|------|
|  1 |      34 |           20 |    4 |     8 |
|  2 |      63 |           38 |    8 |    16 |
|  3 |     101 |           61 |   13 |    26 |
|  4 |     149 |           90 |   20 |    38 |
|  5 |     202 |          122 |   27 |    52 |
|  6 |     255 |          154 |   35 |    65 |
|  7 |     293 |          178 |   40 |    75 |
|  8 |     365 |          221 |   50 |    93 |
|  9 |     432 |          262 |   59 |   111 |
| 10 |     513 |          311 |   70 |   131 |
| 11 |     604 |          366 |   83 |   155 |
| 12 |     691 |          419 |   95 |   177 |
| 13 |     796 |          483 |  110 |   204 |
| 14 |     871 |          528 |  120 |   223 |
| 15 |     991 |          600 |  137 |   254 |
| 16 |    1082 |          656 |  149 |   277 |
| 17 |    1212 |          734 |  167 |   310 |
| 18 |    1346 |          816 |  186 |   345 |
| 19 |    1500 |          909 |  207 |   384 |
| 20 |    1600 |          970 |  221 |   410 |
| 21 |    1708 |         1035 |  236 |   438 |
| 22 |    1872 |         1134 |  259 |   480 |
| 23 |    2059 |         1248 |  285 |   528 |
| 24 |    2188 |         1326 |  303 |   561 |
| 25 |    2395 |         1451 |  332 |   614 |
| 26 |    2544 |         1542 |  352 |   652 |
| 27 |    2701 |         1637 |  374 |   692 |
| 28 |    2857 |         1732 |  396 |   732 |
| 29 |    3035 |         1839 |  421 |   778 |
| 30 |    3289 |         1994 |  456 |   843 |
| 31 |    3486 |         2113 |  483 |   894 |
| 32 |    3693 |         2238 |  512 |   947 |
| 33 |    3909 |         2369 |  542 |  1002 |
| 34 |    4134 |         2506 |  573 |  1060 |
| 35 |    4343 |         2632 |  602 |  1113 |
| 36 |    4588 |         2780 |  636 |  1176 |
| 37 |    4775 |         2894 |  662 |  1224 |
| 38 |    5039 |         3054 |  699 |  1292 |
| 39 |    5313 |         3220 |  737 |  1362 |
| 40 |    5596 |         3391 |  776 |  1435 |

</details>

#### ECC Level: Q

<details>
<summary>Click to expand full capacity tables</summary>

| Version | Numeric | Alphanumeric | Byte (UTF-8 Multi-byte*) | Kanji |
|---------|---------|--------------|------|------|
|  1 |      27 |           16 |    3 |     7 |
|  2 |      48 |           29 |    6 |    12 |
|  3 |      77 |           47 |   10 |    20 |
|  4 |     111 |           67 |   15 |    28 |
|  5 |     144 |           87 |   19 |    37 |
|  6 |     178 |          108 |   24 |    45 |
|  7 |     207 |          125 |   28 |    53 |
|  8 |     259 |          157 |   35 |    66 |
|  9 |     312 |          189 |   43 |    80 |
| 10 |     364 |          221 |   50 |    93 |
| 11 |     427 |          259 |   58 |   109 |
| 12 |     489 |          296 |   67 |   125 |
| 13 |     580 |          352 |   80 |   149 |
| 14 |     621 |          376 |   85 |   159 |
| 15 |     703 |          426 |   97 |   180 |
| 16 |     775 |          470 |  107 |   198 |
| 17 |     876 |          531 |  121 |   224 |
| 18 |     948 |          574 |  131 |   243 |
| 19 |    1063 |          644 |  147 |   272 |
| 20 |    1159 |          702 |  160 |   297 |
| 21 |    1224 |          742 |  169 |   314 |
| 22 |    1358 |          823 |  188 |   348 |
| 23 |    1468 |          890 |  203 |   376 |
| 24 |    1588 |          963 |  220 |   407 |
| 25 |    1718 |         1041 |  238 |   440 |
| 26 |    1804 |         1094 |  250 |   462 |
| 27 |    1933 |         1172 |  268 |   496 |
| 28 |    2085 |         1263 |  289 |   534 |
| 29 |    2181 |         1322 |  302 |   559 |
| 30 |    2358 |         1429 |  327 |   604 |
| 31 |    2473 |         1499 |  343 |   634 |
| 32 |    2670 |         1618 |  370 |   684 |
| 33 |    2805 |         1700 |  389 |   719 |
| 34 |    2949 |         1787 |  409 |   756 |
| 35 |    3081 |         1867 |  427 |   790 |
| 36 |    3244 |         1966 |  450 |   832 |
| 37 |    3417 |         2071 |  474 |   876 |
| 38 |    3599 |         2181 |  499 |   923 |
| 39 |    3791 |         2298 |  526 |   972 |
| 40 |    3993 |         2420 |  554 |  1024 |

</details>

#### ECC Level: H

<details>
<summary>Click to expand full capacity tables</summary>

| Version | Numeric | Alphanumeric | Byte (UTF-8 Multi-byte*) | Kanji |
|---------|---------|--------------|------|------|
|  1 |      17 |           10 |    2 |     4 |
|  2 |      34 |           20 |    4 |     8 |
|  3 |      58 |           35 |    7 |    15 |
|  4 |      82 |           50 |   11 |    21 |
|  5 |     106 |           64 |   14 |    27 |
|  6 |     139 |           84 |   19 |    36 |
|  7 |     154 |           93 |   21 |    39 |
|  8 |     202 |          122 |   27 |    52 |
|  9 |     235 |          143 |   32 |    60 |
| 10 |     288 |          174 |   39 |    74 |
| 11 |     331 |          200 |   45 |    85 |
| 12 |     374 |          227 |   51 |    96 |
| 13 |     427 |          259 |   58 |   109 |
| 14 |     468 |          283 |   64 |   120 |
| 15 |     530 |          321 |   73 |   136 |
| 16 |     602 |          365 |   83 |   154 |
| 17 |     674 |          408 |   93 |   173 |
| 18 |     746 |          452 |  103 |   191 |
| 19 |     813 |          493 |  112 |   208 |
| 20 |     919 |          557 |  127 |   235 |
| 21 |     969 |          587 |  134 |   248 |
| 22 |    1056 |          640 |  146 |   270 |
| 23 |    1108 |          672 |  153 |   284 |
| 24 |    1228 |          744 |  170 |   315 |
| 25 |    1286 |          779 |  178 |   330 |
| 26 |    1425 |          864 |  197 |   365 |
| 27 |    1501 |          910 |  208 |   385 |
| 28 |    1581 |          958 |  219 |   405 |
| 29 |    1677 |         1016 |  232 |   430 |
| 30 |    1782 |         1080 |  247 |   457 |
| 31 |    1897 |         1150 |  263 |   486 |
| 32 |    2022 |         1226 |  280 |   518 |
| 33 |    2157 |         1307 |  299 |   553 |
| 34 |    2301 |         1394 |  319 |   590 |
| 35 |    2361 |         1431 |  327 |   605 |
| 36 |    2524 |         1530 |  350 |   647 |
| 37 |    2625 |         1591 |  364 |   673 |
| 38 |    2735 |         1658 |  379 |   701 |
| 39 |    2927 |         1774 |  406 |   750 |
| 40 |    3057 |         1852 |  424 |   784 |

</details>

## Micro QR Code (M1-M4)

Character capacities per version and ECC level (ISO/IEC 18004 Table 7; `—` = mode or ECC level not available on that version):

| Version | ECC | Numeric | Alphanumeric | Byte | Kanji |
|---------|-----|---------|--------------|------|-------|
| M1 (11×11) | Detection only | 5 | — | — | — |
| M2 (13×13) | L | 10 | 6 | — | — |
| M2 (13×13) | M | 8 | 5 | — | — |
| M3 (15×15) | L | 23 | 14 | 9 | 6 |
| M3 (15×15) | M | 18 | 11 | 7 | 4 |
| M4 (17×17) | L | 35 | 21 | 15 | 9 |
| M4 (17×17) | M | 30 | 18 | 13 | 8 |
| M4 (17×17) | Q | 21 | 13 | 9 | 5 |

Byte capacities are encoded byte counts: ISO-8859-1 text costs 1 byte per character, UTF-8 multi-byte text costs its UTF-8 length (e.g. 'あ' = 3 bytes). Micro QR has no ECI, so its UTF-8 text is bare bytes a reader has to recognise. Kanji capacities are characters with a Kanji-mode cell, written only with `AllowKanji`, from M3.

## rMQR Code (R7x43-R17x139)

Character capacities per version and ECC level (ISO/IEC 23941, verified against external encoders; `R{height}x{width}` in modules, quiet zone excluded). rMQR supports ECC levels M and H only. Kanji capacities are characters with a Kanji-mode cell, written only with `AllowKanji`, with no ECI header.

| Version | M: Numeric | M: Alphanumeric | M: Byte | M: Kanji | H: Numeric | H: Alphanumeric | H: Byte | H: Kanji |
|---------|-----------:|----------------:|--------:|---------:|-----------:|----------------:|--------:|---------:|
| R7x43 | 12 | 7 | 5 | 3 | 5 | 3 | 2 | 1 |
| R7x59 | 26 | 16 | 11 | 6 | 14 | 8 | 6 | 3 |
| R7x77 | 45 | 27 | 19 | 11 | 21 | 13 | 9 | 5 |
| R7x99 | 64 | 39 | 27 | 16 | 30 | 18 | 13 | 8 |
| R7x139 | 102 | 62 | 42 | 26 | 54 | 33 | 22 | 14 |
| R9x43 | 26 | 16 | 11 | 6 | 14 | 8 | 6 | 3 |
| R9x59 | 47 | 29 | 20 | 12 | 23 | 14 | 10 | 6 |
| R9x77 | 71 | 43 | 30 | 18 | 37 | 23 | 16 | 9 |
| R9x99 | 97 | 59 | 40 | 25 | 49 | 30 | 20 | 12 |
| R9x139 | 147 | 89 | 61 | 38 | 75 | 46 | 31 | 19 |
| R11x27 | 14 | 8 | 6 | 3 | 9 | 6 | 4 | 2 |
| R11x43 | 42 | 26 | 18 | 11 | 23 | 14 | 10 | 6 |
| R11x59 | 71 | 43 | 30 | 18 | 33 | 20 | 14 | 8 |
| R11x77 | 100 | 60 | 41 | 25 | 52 | 31 | 21 | 13 |
| R11x99 | 133 | 81 | 55 | 34 | 66 | 40 | 27 | 17 |
| R11x139 | 198 | 120 | 82 | 51 | 97 | 59 | 40 | 25 |
| R13x27 | 26 | 16 | 11 | 6 | 14 | 8 | 6 | 3 |
| R13x43 | 62 | 37 | 26 | 16 | 28 | 17 | 12 | 7 |
| R13x59 | 88 | 53 | 36 | 22 | 45 | 27 | 18 | 11 |
| R13x77 | 124 | 75 | 51 | 31 | 66 | 40 | 27 | 17 |
| R13x99 | 171 | 104 | 71 | 44 | 80 | 49 | 33 | 20 |
| R13x139 | 251 | 152 | 104 | 64 | 126 | 76 | 52 | 32 |
| R15x43 | 76 | 46 | 31 | 19 | 33 | 20 | 13 | 8 |
| R15x59 | 112 | 68 | 46 | 28 | 59 | 36 | 24 | 15 |
| R15x77 | 157 | 95 | 65 | 40 | 71 | 43 | 29 | 18 |
| R15x99 | 207 | 126 | 86 | 53 | 111 | 68 | 46 | 28 |
| R15x139 | 301 | 182 | 125 | 77 | 162 | 98 | 67 | 41 |
| R17x43 | 90 | 55 | 37 | 23 | 47 | 28 | 19 | 12 |
| R17x59 | 131 | 79 | 54 | 33 | 63 | 38 | 26 | 16 |
| R17x77 | 183 | 111 | 76 | 47 | 87 | 53 | 36 | 22 |
| R17x99 | 236 | 143 | 98 | 60 | 131 | 79 | 54 | 33 |
| R17x139 | 361 | 219 | 150 | 92 | 178 | 108 | 74 | 46 |

Byte capacities are encoded byte counts (ISO-8859-1 text 1 byte per character, UTF-8 multi-byte text its UTF-8 length). Automatic version selection picks the fewest-modules symbol by default (`RmQRFitStrategy.MinimizeArea`, the same choice other encoders make), which can be taller and narrower than the flattest fit; use `RmQRFitStrategy.MinimizeHeight` or a fixed `RmQRHeight` for label lanes.

The table above is per single mode, which is what the default `RmQRSegmentation.Single` encodes. Content that mixes modes is not bound by any single row: `RmQRSegmentation.Optimal` splits it into the runs that cost the fewest bits, so `https://example.com/p/1234567890123456` (38 characters, over the 37 Byte-mode characters R17x43-M holds) fits R15x43 as a Byte run plus a Numeric run. Mixed content therefore has no single capacity number, only a bit budget: `8 × data codewords`, spent as `3 + count indicator + payload` per run plus an 11-bit ECI prefix when one is emitted.
