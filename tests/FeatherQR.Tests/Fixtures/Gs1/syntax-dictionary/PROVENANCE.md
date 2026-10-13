# Provenance

The GS1 Barcode Syntax Dictionary, release 2026-01-27, copied unmodified from https://github.com/gs1/gs1-syntax-dictionary at tag `2026-01-27` (commit `8ad003f83a4ba6c2179ce15d578fae27d6580909`), Apache License 2.0 (see LICENSE beside this file; the repository has no NOTICE file). Copyright (c) 2020-2021 BWIPP project, (c) 2020-2021 Zint Project, (c) 2021-2025 GS1 AISBL.

`gs1-syntax-dictionary.txt` is the AI table this library's GS1 layer is generated from (qrcode-symbologies.md, "GS1 data"). The `lint_*.c` files are the reference linters whose code lists are not in the dictionary file: ISO 3166-1 numeric and alpha-2, ISO 3166-1 numeric or 999, ISO 4217 numeric, ISO/IEC 5218, the AIDC media types of AI (7241) and the package types of AI (7041). The ISO lists and the media types are bit fields indexed by code, the package types a sorted string list, and each file's unit tests enumerate its list. `regenerate-gs1` imports those unit tests into `linters/`. The other linters (check digits, dates, positions) hold rules, not lists, and are not copied.

Fetched by `pwsh tools/QRInteropFixtures/get-gs1-syntax-engine.ps1` and copied by `dotnet run --project tools/QRInteropFixtures -- import-gs1-syntax-dictionary`. The dictionary's own header says: "Entries are maintained in lexical order of their AI." It holds 224 entry lines covering 541 AIs and names 34 linters.

| File | Bytes | SHA-256 |
|---|---|---|
| `gs1-syntax-dictionary.txt` | 30280 | `6461ece7c03420fb27a3f18163ef9a08694aa4bb2491dea25f63bcdf22451a99` |
| `LICENSE` | 10173 | `a6cba85bc92e0cff7a450b1d873c0eaa2e9fc96bf472df0247a26bec77bf3ff9` |
| `lint_iso3166.c` | 74550 | `cd908040850a2e18a94e18c8946a4db3828f07c6d9792cad01affc441339103f` |
| `lint_iso3166alpha2.c` | 55143 | `3bd7a68e9873443d66abbc634030e281714147619d603d7fc13454d61d918917` |
| `lint_iso3166999.c` | 3528 | `9d883ee2d508173c58fc73db62da241c9ab91ef4d3b52227ece312861dd04310` |
| `lint_iso4217.c` | 76400 | `6a12ed0b0176562514826c86a81c6734730578193c34a592ae81da2d4b9e1987` |
| `lint_iso5218.c` | 3522 | `77683da45006810dbecc55a8d9d50e8eee72268c07e2ba9265b053e2edef6eaf` |
| `lint_mediatype.c` | 12571 | `bc07427212a584830ff80de45d15eb194f176a8b9a012a19831c34ca98be94ae` |
| `lint_packagetype.c` | 27185 | `84f689b1fdae2d0bcb0a28e3758407d7a1080c39f823723a850935513bef5106` |
