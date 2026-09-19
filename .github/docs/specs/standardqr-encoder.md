# Standard QR Encoder

Design record for the Standard QR encode feature (`QRCodeGenerator`): what it does, why the pipeline is structured this way, and what was learned while making the implementation spec-compatible and fast. Normative details and implementation locations are indexed in the [spec-to-code map](standardqr-spec-map.md). The inverse pipeline is documented in [Standard QR Decoder](standardqr-decoder.md).

---

## What

`QRCodeGenerator` converts text into a Standard QR module matrix through the complete ISO/IEC 18004 encoding pipeline:

```
Text
  -> mode / ECI analysis
  -> version selection
  -> data bit stream and padding
  -> Reed-Solomon ECC per block
  -> data / ECC interleaving
  -> function-pattern and data placement
  -> best-of-8 mask selection
  -> format and version information
  -> QRCodeData or byte-per-module matrix
```

### Public entry points

The encoder exposes two output models.

#### `QRCodeData`

`Create(ReadOnlySpan<char>, ...)` returns a `QRCodeData` object (a `string` converts implicitly, except on the netstandard2.0 asset below C# 14).

- The core matrix is stored bit-packed, one bit per module.
- The quiet zone is virtual: it changes the public coordinate space but consumes no payload storage.
- The matrix is deterministic for the same text and options.
- This is the convenient object model used by renderers and serialization.

#### Caller-provided matrix buffer

`Create(ReadOnlySpan<char>, ..., Span<byte> destination, ...)` writes:

- one byte per module;
- `0` for light and `1` for dark;
- flat row-major order;
- quiet zone included.

`TryGetRequiredBufferSize` returns the required matrix side, byte count, and selected version, and returns `false` when the content exceeds the capacity of every version in range; argument errors (a negative or overflowing quiet zone) still throw, so `false` means "does not fit" and nothing else. It is the only sizing method on the surface; the throwing `GetRequiredBufferSize` that 1.1.1 released was deprecated in 1.2.0 and removed in 2.0.0. The rationale for that split, and why it is a `Try` rather than a dedicated exception type, is recorded once in [rmqr-encoder.md](rmqr-encoder.md) — Standard QR follows the same rule so the three symbologies present one surface. The encoder overwrites every byte of the written region (the core comes from a per-version template copy; only a non-zero quiet zone is cleared), accepts a dirty pooled destination, and leaves any tail beyond the returned byte count untouched. After JIT and pool warm-up, the span path is allocation-free in Release builds.

### Supported

| Area | Coverage |
|---|---|
| Symbology | Standard QR |
| Versions | 1–40 |
| ECC levels | L, M, Q, H |
| Data modes | Numeric, Alphanumeric, Byte |
| ECI | Default/no header, ISO-8859-1 (assignment 3), UTF-8 (assignment 26) |
| UTF-8 BOM | Optional in UTF-8 Byte mode |
| Version selection | Automatic minimum-fit, caller-requested version, or a version range |
| ECC boost | Optional: the requested level becomes the minimum and is raised as far as the chosen version's capacity allows, never changing the version |
| Segmentation | One segment in one mode by default; opt-in mixed-mode segmentation (`QRSegmentation.Optimal`) splits the content into the minimal-bit Numeric / Alphanumeric / Byte runs |
| Quiet zone | Configurable non-negative size; span sizing/output rejects dimensions that cannot fit an `int`-sized matrix |
| Structured Append | `CreateStructuredAppend` splits text across the fewest symbols (2 to 16) whose version stays within `QRCodeGeneratorOptions.Version`, all at one version, balanced so the fullest symbol is as empty as it can be; text that fits one symbol in the range returns the plain symbol. See [Structured Append](#structured-append) |
| Output | Bit-packed `QRCodeData` or byte-per-module `Span<byte>` |

### Not implemented

- Kanji mode
- FNC1
- Arbitrary ECI assignment numbers
- Arbitrary binary payload input
- Micro QR and rMQR

By default the encoder analyzes the complete input once and emits one data segment; `QRSegmentation.Optimal` opts into the globally minimal mixed-mode split instead (see [Mixed-mode segmentation](#mixed-mode-segmentation)).

### Structured Append

ISO/IEC 18004 lets one message span up to sixteen symbols, each carrying a 20-bit header (mode `0011`, 4-bit position, 4-bit count minus one, 8-bit parity) ahead of its segments. `CreateStructuredAppend` writes such a set; a reader reassembles it by the rules on `QRStructuredAppend`.

**What decides the symbol count is the version cap the caller already has.** "Fewest symbols" has no answer without a bound (version 40 holds 2,953 bytes, so the unbounded answer is one symbol), and the bound a caller has is the largest symbol they can print, which `QRCodeGeneratorOptions.Version` already expresses. No count parameter exists; `QRVersionRange.AtMost(n)` is the request. Text that fits one symbol in the range returns exactly what `Create` returns, with no header: a set of one costs 20 bits and tells a reader nothing. The planner prices every chunk with the set header, so asked alone it split a text in the last 20 bits of a symbol in two, and refused a single character too wide for a symbol with the header although `Create` holds it. Whenever it splits or refuses a text that could be one symbol, the question is therefore asked again the way `Create` asks it, the quiet zone left out. How many chunks the planner said does not narrow that: at version 1-H the six bytes of "a", a kana and "aa" are one symbol to the bit and three chunks of a set. What does narrow it is the measure the planner's search ran on, which it has before it counts anything: the whole text's plan, or where no plan can pay the closed form of its single-mode stream. No stream of the text costs less, so one that does not fit the largest symbol without the header is not asked about, which is nearly every set. Asked of every text shorter than a symbol's best case (ten bits on three characters), the question was a run of the segmentation program that `Create` then repeated: a fifth more time on a two-symbol label-sized set of order lines, a tenth on prose. The questions are counted, and a label that is plainly a set is held to asking none. The refusal's advice asks the same two questions, so what it says would work is what the caller gets. A text the planner holds in one chunk goes straight to `Create`, so the common case pays for one plan, not two.

**The split is balanced, in three searches over the symbol count.** Each symbol's budget is its version's data capacity less the 20-bit header and, when the set declares a charset, the 12-bit ECI header it repeats. A chunk's cost is its single-mode stream, or under `QRSegmentation.Optimal` the cheaper of that and the minimal mixed plan; cost is monotone in the chunk's length, so the longest chunk that fits a budget is well defined, and a greedy walk at a budget gives the count that budget needs. That chunk end is found without pricing whole prefixes: a prefix's mode changes at most twice along the text (Numeric, then Alphanumeric, then Byte), and within a mode the single-mode cost is a closed form of the length, so the end is a few arithmetic steps plus, for a UTF-8 Byte run, one pass over the run's own bytes; under `Optimal` the mixed plan holds the single-mode plan among its candidates, so one forward pass of the segmentation program, which yields every prefix's optimum as it goes and stops at the first over budget, is the answer, skipped inside an all-digit run (no split improves one) and narrowed to the leading alphanumeric run behind a byte order mark (only a Byte-mode chunk carries one, and carrying one forces its single-mode stream). The first cut priced each probe of a binary search with a full analysis of the prefix, and under `Optimal` a full cost run, over a prefix that could reach the end of the text; the reference walk that prices one character at a time with the cost definition is what `StructuredAppendPlannerTest` holds the fast path to, on every budget the answer can turn on. The planner finds the fewest symbols at the largest version, then the smallest version in the range that still holds that count, then the smallest per-symbol budget at that version that still holds it, and splits at that budget. Every symbol therefore shares one version, and no symbol is fuller than it has to be; a greedy fill would leave a set of three full symbols and one nearly empty, which on one label reads as a defect. Splits fall on `char` boundaries and never inside a surrogate pair. More than sixteen symbols at the largest version, or a single character that fits no symbol there, is `ArgumentException` on the options.

**A run no mode but Byte encodes is one addition per character, not a step of the state machine.** The program carries seven states, but a character outside the alphanumeric alphabet can only extend a Byte run, so after one such character every other state is unreachable and stays that way until an alphanumeric one arrives. The loop notices that and adds the character's bytes to the Byte state directly, writing the same predecessor the state machine would have written. It is where the characters are: 86 % of them in Japanese behind a UTF-8 declaration, 55 % in English prose, 24 % in the digit-and-word mixture. Shared by all three symbologies, so `ModeSegmenterByteRunParityTest` holds the program to an independent one that always walks all seven, on cost, final state and the reconstructed plan, across the bands, the charsets and the mode-availability flags Micro QR passes.

**The program's step is arithmetic on six registers, not a relaxation through memory.** Each target state has two kinds of candidate, continue the run or open it from the cheapest state, so its cost is one add and one min from the six costs of the previous character; the first form of the program relaxed every state from every state through two stack arrays, with a bounds-checked write per relaxation, a 28-byte copy per character and, on the prefix walk, a scan of the six states per character, and that memory traffic was six times the cost of the arithmetic (13 ns per character against 2). The common shapes have their own loop, a Latin charset with Byte allowed for costs, for costs with parents and for the prefix walk, so the constant byte cost and the mode flags are not re-decided per character; UTF-8 and the Micro QR versions without a mode share one general loop per entry point. Where parents are recorded, the candidates are tried in state order and a later one replaces an earlier one only when strictly cheaper, which is the tie-break the relaxation had, so the plan the writer emits is byte for byte the same plan; on a Latin symbol the six parents of a character are one 8-byte store. `ModeSegmenterPlanParityTest` holds every loop to the all-states relaxation with its predecessor rule, on cost, final state, the reconstructed runs and the walk's stopping point at every budget it can turn on, across the bands, three charsets, the four mode-availability combinations and seeded random mixes of every class. Measured on the digit-and-word mixture, the walk went from 12.8 to 2.0 ns per character and the per-symbol plan from 11.2 to 3.0; the shapes that were tried and lost are recorded with the reasons: a vector form of the step (the broadcast and horizontal minimum sit on the serial chain), a branch-free minimum (which candidate wins is decided by the structure of the costs, not the content, and the predictor learns it), and a factored recurrence that shortens a chain the code does not have, since the minimums compile to branches.

**The searches only consult the segmentation program about content it could help.** A mixed plan is cheaper than one run only when some run of it is in a mode denser than the whole content's, and that run has to repay at least the mode and count indicators it adds, which at the narrowest band takes four digits or six characters of the alphanumeric alphabet. One pass over the whole text for the longest run of each answers it for every chunk at once, since no chunk's runs are longer than the text's, and when the answer is no the three searches run as `QRSegmentation.Single`: the two costs are the same number, so the split is the same split. English prose and Japanese hold neither run, and each of their sixteen passes over the text becomes none. The per-symbol writer still builds its plan where a tie is possible, because when the minimum is a tie the plan it reconstructs is not always the single run and skipping it would change which of two equally priced streams a symbol carries; that is content whose longest run of digits is exactly three, and everything below it is settled without the program (see "only where a plan can differ from one run" below). `PlanCouldWinTest` holds the rule the only direction that matters: over runs of every length near the thresholds, at the start, the middle and the end of Byte-mode content, across the bands and both charsets, a no from the predicate must be a no from the program.

**The budget search is bracketed by construction, not by capacity.** Its probes are what the split costs: a probe walks the text into chunks and, under `QRSegmentation.Optimal`, each chunk costs a forward pass of the segmentation program, so the search pays one pass per bisection step and the step count is the log of the bracket. Starting from the rate bound below and the version's capacity above leaves that bracket thousands of bits wide around an answer tens of bits from one end, so the floor is computed instead, from one pass over the text: the minimal plan for the whole text divided by the symbol count, plus the headers every symbol pays, because the plans of a split concatenate into one plan for the whole and therefore cost at least it. Only content whose probes are passes pays for it: `QRSegmentation.Single` and content no plan can beat keep the rate bounds, where a probe is arithmetic. `StructuredAppendPlannerTest` holds the bracket by requiring the budget to hold the count and one bit below it not to.

**One walk near that floor settles the count, the version and the ceiling.** Measured over digit-and-word mixtures, alphanumeric, Latin-1 and UTF-8 content, content whose density changes along the text and seeded random runs of every class, across three version ranges and two levels, the balanced budget sits 3 to 22 bits above the plan's floor: what separates them is one chunk's rounding and a run header or two at the cuts, not the content. The first form of the ceiling, the fullest chunk of the text cut into equal character counts, is within 55 bits of the floor only on periodic content; on content whose density varies it is hundreds to thousands of bits over and usually past the capacity, so it bracketed nothing exactly where real content lives (eight to ten probes instead of five), and the benchmark's repeated lines had hidden that. So under `Optimal` the planner takes the whole text's plan first, which gives the fewest symbols any split can use (its share of the capacity, rounded up), and walks once at the floor plus 31 bits at the largest version, limited to that count. If the walk holds it, the count is that bound, since it cannot be fewer, and the walk at the capacity is not made; a scan candidate in the same count indicator band whose capacity is at least the walk's budget holds that very split, since a chunk's cost depends on the version only through the band, so it is not walked either; and the walk's budget is the ceiling of the budget search, 31 bits above its floor, with its split kept as the settled one. Where the answer's band has no such walk (the scan went down a band, or the walk was refused) the search opens from the floor in widening steps, 31, 31 again, then tripling, each failure raising the floor: a surrogate pair is 32 bits that cannot be cut, so content made of them sits 32 to 47 bits above its floor, inside the second step. The first walk is attempted only when the bound's count leaves each symbol two margins of slack; with less, packing losses usually put that count out of reach (small symbols at a high level lose up to a pair's width each) and the walk at the capacity counts as it always did. A text that fits the largest version as one single-mode stream is answered by the walk's closed form before any plan is taken, as it was. Every shortcut is a lower bound or a split that exists, so the plan is the one the three searches alone would find; `StructuredAppendPlannerTest` holds the count, the version and the budget against a walk that prices one character at a time, on periodic content, on content whose density changes, on ranges that cross a band (9/10, 26/27) and on the pair-heavy content that fails the first margin. Counted in passes of the program per plan: the digit-and-word mixture 8.9 to 7.0, digits followed by that mixture 8.0 to 5.0, random runs 10.9 to 7.0, and no shape more than before. Pricing the even cut's chunks as eight lanes of one vector program was measured at 4.6 times the scalar pricing and not kept, since the design that replaced the even cut has no cut to price.

**The walks of the budget search go together, eight budgets at a time.** After the walk near the floor, a plan under `Optimal` was the whole text's plan, that walk, and five bisection probes inside its 31-bit bracket: seven passes of the segmentation program, five of them the same walk at budgets a few bits apart. Reusing what neighbouring probes share was the first idea and is the smaller one. A chunk's end is monotone in the budget, so from one start a walk between a failed budget and a held one ends its chunk between theirs, and the leading chunks on which those two agree are the probe's own, with no chunk's cost ever needed; but a greedy chunk is filled to within a character of its budget, so two walks more than a character apart in budget part ways at the first chunk, and only the last probe or two of a bisection share anything (0.6 of 7 passes on periodic content, nothing elsewhere). It is kept for the probes that remain. What pays is that the probes are independent instances of one program over one text: up to eight budgets are walked at once, one per vector lane, one vector per state. Accelerated 256-bit vectors carry eight 32-bit costs; ARM64 NEON carries eight saturating 16-bit costs, since every symbol's budget fits that representation. A lane whose chunk closes re-reads the character that did not fit as the first of its next chunk and so falls a step behind, which keeps the lanes within a few characters of each other; while they are at the same character, which is nearly always, a step is the scalar loop's class lookup and branches over vector adds and mins, and costs about what one scalar step costs. Around a chunk end they are stepped separately with the class and byte cost per lane in scalar code, and closing a chunk is scalar code too, a dozen times per lane in thousands of steps. The first batch is the walk near the floor itself: eight budgets from 3 to 31 bits above it (5 to 47 under UTF-8, where a three-byte character is 24 bits that cannot be cut and balanced budgets reach 46 bits above the floor), the cheapest that holds the bound's count settling the count, the versions of its band and the ceiling, and its failed neighbour the floor; a second batch takes every budget left between them. A plan is then the whole text's plan and two batches, on one-byte and UTF-8 content alike. Runs of U+FEFF inside the text are the exception the floor does not see: a cut kept off a run moves the run and the character ahead of it to the next chunk, which can leave the answer above every budget of the first batch, and the search then fell back to the walk at the capacity and to scalar probes (runs of marks after forty digits, 15,000 characters, planned in 2.2 to 4.5 times the time of the same text with an ordinary character in the marks' place, depending on the level and the range). When every budget of the first batch fails, one more batch from the highest spreads across the most such a cut can leave unused. Below a ceiling known to hold the count, as when the bracket is opened, it is taken whenever two of its budgets fit under it; below the capacity the count is asked at, which may not hold it, the answer can be past the ceiling and the batch is then a walk the fallback repeats, so it is taken only when the ceiling leaves it at least half its budgets (taking it there regardless cost a set whose count was one more than the bound's up to a fifth more planning). Measured against the twin, versions 1 to 40 unless named: six marks at level L 2.4 → 1.5 times, twenty at Q 4.5 → 2.5, ten at H 3.2 → 2.0, four at M (versions 1 to 33) 2.2 → 1.9; twenty at L stays at 2.8, and that is the gate's price: the capacity leaves its second batch two budgets, one of which would settle the count, and taken regardless it would plan with no scalar walk instead of three. Which way a batch cut that short goes is not known before it is walked, and the gate takes the side of the sets whose count is one more than the bound's. Spreading the first batch by the run instead cost the texts whose answer stays near the floor a third (four and ten marks at L), which the retry leaves as they were; text without marks never reaches it. The byte order mark `Utf8Bom` asks for keeps the scalar walks, since its chunk is priced by another rule, and so does content whose chunks average under the backend's threshold (128 characters for the 256-bit path, 20 for NEON), where chunks close so often, a step or two apart, that the lanes are rarely together (measured: 78-character chunks half as slow again with lanes, 267-character ones nearly twice as fast). A pair needs no rule of its own while stepping: it is priced whole on its first half, so a budget it breaks is broken on both halves and the chunk ends before the pair either way, which a mutation that dropped the rule and changed no plan is what showed. The scalar probes run where neither accelerated 256-bit vectors nor ARM64 NEON are available. NEON keeps native vectors in registers and tests whether any budget overflowed before extracting individual lane bits; wrapping the vector state in another struct introduced costly stack traffic. Its shorter threshold follows measurements around the old boundary and on small symbols: the compact state pays below 128 characters, while very short chunks still favor scalar probes. `StructuredAppendNeonParityTest` checks saturation, long text offsets and the budget representation boundary against the scalar walk. `StructuredAppendLaneWalkTest` holds the lanes to the scalar walk lane by lane, from the start of the text and resumed, at budgets one, four and thirteen bits apart across five versions and every charset, with pairs, lone surrogates and U+FEFF inside the text (a line, after digits, after a pair, at the head), and requires the plan to be the same plan with and without them. What the plan does not show it pins separately: where each batch from the floor lands against the scalar search's answer (the second batch below either ceiling, at three budgets and at four, and only once), the reach `KeptOffBits` gives it, and the walks one plan per call site takes, which the planner counts per thread for that purpose.


The search also does not walk the answer again once it has it: the probe that last lowers the ceiling walked at the budget the search ends on, so its split is kept aside, since a failing probe after it overwrites the caller's buffer, and handed back. The text is walked a final time only when no walk settled the budget the search ends on, which is when the capacity itself was the answer; the same test file requires the split handed back to be the walk at the budget handed back, across pinned versions that put the answer at the top of the bracket.

**The symbols of a set are planned together, and only where a plan can differ from one run.** What is left of the program after the planner is the writer's: each symbol needs its runs, which costs only do not give, so each chunk was run through the program once more with its predecessors recorded, walked back, measured, and measured again by its caller for the set's header; on order lines that was 200 us of a 1.45 ms set where the single-mode stream of the same chunks takes 7. Three things are handed over instead of worked out. The planner's pass for the longest run of digits and of alphanumerics, which already decides whether its searches need the program, also settles when every plan is one Byte run: with no run of digits reaching three and no alphanumeric run reaching six, a run outside Byte costs strictly more than the bytes it replaces at every version (two digits save 9 bits and five alphanumerics 12, against a header of at least 13), so the minimal plan is unique, tie-breaks have no say, and a Byte chunk of such a text is written as its single-mode stream, which is that plan's stream, with no program run for it. The threshold for digits is one below the planner's own: three digits tie with Byte and the program gives a tie to the split, so saying it one digit later would change what is emitted. The plan builder hands its measurement to its caller. And the plans of up to eight symbols are taken in one pass: the program is a serial recurrence, so one chunk cannot be vectorised, but the chunks of a set have nothing to do with each other, so each gets a vector lane at its own character: eight at once with accelerated 256-bit vectors, or groups of four 32-bit lanes with ARM64 NEON. What makes that a vector step rather than fifteen compares and blends is the form the states are carried in with predecessors: a key, the cost shifted up three bits with the state's number below it, whose minimum is the minimum cost and among equal costs the lowest state, which is the tie-break every ordered chain had because each listed its candidates in state order; the predecessor is the low bits of a minimum the cost-only loop takes anyway. In scalar code the keyed body only draws with the ordered chains, and it replaced them because the lanes and the single chunk then share one arithmetic. Only symbols that take a plan get a lane, in order, eight to a pass: a chunk of digits is never planned, and as a lane it would be the longest and keep the others waiting. Lanes end at different characters, so the loop runs to the nearest end, takes the results of the lanes that end there, and points them at the longest chunk so they go on reading text that exists; under UTF-8 a character's bytes are two compares on the vector of characters, asked lane by lane only on a step that holds a surrogate. NEON retains 32-bit keys because the cost multiplied by eight cannot fit the budget walker's 16-bit representation. Groups with too little useful work relative to their longest chunk use scalar planning; this avoids spending most of a vector pass on padding. The parent layout and tie-break remain shared, so plans and emitted modules are identical across backends. `ModeSegmenterLaneParityTest` compares costs, final states and reconstructed runs against an independent all-states reference, including every UTF-16 classification value, uneven groups, surrogate boundaries and U+FEFF. None of these three changes what is written, so the writer counts, per thread, the chunks it plans alone and the passes that plan them together, and `StructuredAppendStreamTest` pins both on prose (neither), twelve symbols of order lines (two passes), three (one pass, the fewest a pass takes) and a label of two (two alone).

The table the program records is what the walk back needs and no more. Of a character's six predecessors three are constants of the packing groups (Numeric0 comes from Numeric2, Numeric2 from Numeric1, Alnum0 from Alnum1), so the table holds the other three in two bytes a character where it held seven, and the stack holds the table of 256 characters instead of 73. The walk back used to read `parents[i * 7 + state]` character by character, each load's address waiting for the state the load before it produced, and cost close to half of the program that filled the table. A run of Numeric can only begin where the state is Numeric1, every third character of the run, a run of Alphanumeric every second, and a run of Byte at the first entry that does not say Byte, so the walk steps from one such place to the one before it and reads only whether the run continued there: the address is the position alone, the branch is "continued" nearly always, and nothing waits for anything (45 us to 8 on order lines). Decoding the state from a word of packed fields, the first attempt at removing the dependent load, was slower than the load. The lanes interleave the same two bytes by lane so that a step is one store, and the walk back reads its lane by stride; the single chunk is one lane of that shape, because a plain loop over a Byte run is within noise of a vectorised search everywhere but on prose, which is the content the planner's verdict takes away.

**A search that cannot win still costs, so each is gated.** Every walk is asked "at most this many?", and stops the moment the answer is no: at a version far below the one the set needs, an unbounded walk splits the text into thousands of chunks and pays a binary search for each, which on the open version range was most of the cost of the feature. A lower bound on what any split can cost, every character at the cheapest rate any mode gives it plus the headers every symbol pays at the narrowest widths any version has, is priced once and rules out the versions whose capacity times the count cannot reach it, so the version scan walks only the candidates that could hold the count, and the budget search starts at the bound's average share rather than at one bit. The bound only rejects: it is additive over chunks because a split never cuts a surrogate pair, and `StructuredAppendPlannerTest` holds it to never refusing a count the walk reaches at any version and level. Under `QRSegmentation.Optimal` a walk is a pass over the text, so a version the rate bound admits is checked a second time against a tighter floor before it is walked: the minimal plan for the whole text at that version's count indicator widths, since a split's plans concatenate into one plan for the whole at the same widths and every chunk also pays its headers. On digit-and-word content the rate bound, which prices every digit as if it sat in a full Numeric group, was admitting the eight versions just below the answer and walking each one nearly to the end of the text; the planned floor turns them away, computed once per count indicator band and shared with the budget bracket that needs the same number. The same test file holds it to never refusing a count the walk reaches, and checks with the walk alone that every version below the chosen one fails. `QRCodeStructuredAppendEncode` measures the split against the symbols it produces, so its Ratio column is the planning overhead itself.

**The charset is decided once and the parity is one XOR.** The whole text is analysed once, the charset that analysis chooses is forced on every chunk and declared in every symbol (a pure-ASCII chunk of a UTF-8 set still carries the UTF-8 ECI), and the parity every symbol carries is the XOR of the whole text's bytes in that charset, computed once before splitting. Any per-chunk computation can diverge from it when a chunk would have chosen another charset on its own, and a reader cannot tell which bytes an encoder XORed, so the value has to be a function of the input and the charset alone. A byte order mark, when `Utf8Bom` asks for one and the first chunk is written in Byte mode, goes into the first symbol only and is counted in the parity, since it is a prefix of the data as in the single-symbol path. `BoostEccLevel` raises the whole set to the highest level every chunk still fits at the shared version, never one symbol at a time, so a set does not come out with mixed levels. `MaskPattern` applies to every symbol; `Segmentation` and `QuietZoneSize` apply per symbol.

**A U+FEFF inside the text never opens a symbol, and never opens a Byte run.** The byte-segment decoder takes a segment's leading U+FEFF for a byte order mark and drops it. A set meets that twice, and both answers are rules of the walk rather than checks after it. A chunk is a symbol of its own, so a cut that lands just before the mark would open the next symbol with it and lose it under either segmentation; every walk therefore moves such a cut back past the character ahead of the mark (a surrogate pair whole, and on back through a run of marks), which leaves the chunk within its budget and the end monotone in the budget. A run of marks longer than a symbol holds cannot be kept off a symbol's head at all, and such a text is refused like any text sixteen symbols do not hold. Inside a chunk the segmentation program opens no Byte run at the mark (see "Plans the byte-segment decoder would misread are never built"), so the plan a chunk is sized by is always the plan written. Both rules are for UTF-8 only: under a declared ISO-8859-1 the decoder drops nothing and the mark is written as the Latin-1 writer writes any character it lacks (a replacement byte, or its low byte on the targets that narrow). A mark at the head of the text is the first symbol's and is lost on decode, as it is in a single symbol. The forms this replaced are worth their lessons. The plan builder used to refuse the minimal plan when it opened a Byte run at the mark, and wrote the single-mode stream instead, which a chunk sized by the plan had never been sized for: the encode threw, as did the boost and the one-symbol path. Pricing such texts as `Single` cured the throw and made fourteen symbols of nine; pricing as asked and falling back to `Single` only when some chunk's plan was refused kept most texts whole, but a mark after a run of ten digits is refused every time, and one such mark in forty-eight thousand characters refused a text that sixteen symbols hold, or cost a version. The cut rule likewise lived in the scalar walk alone, with the lanes' split checked afterwards and searched again without them when it opened a symbol with a mark: on a text with a mark a line that second search ran for three texts in ten and made the planner three times as slow. Each of those was a check placed after a search that did not know the rule; the rule in the recurrence and in the chunk close costs a compare where a mark is and nothing where none is. It did cost the lanes their company at first: a cut moved off a mark sets that lane back by more than the one step a close costs the others, the lanes never shared a character again, and the rest of the walk ran per lane at a seventh of the speed, slower than no lanes at all. The lanes ahead now wait, keeping their states, until the one furthest behind is level with them, which costs a step or two per close and brought a text with a mark a line (`marked-40k-any`: 40,000 characters of order lines, versions 1 to 40, level L) from 2.9 ms to 1.6 ms, the time of its unmarked twin. A lane that moves while the lanes are apart is at the head of its chunk, one character and then the marks its cut was kept off, where continuing that character's run is never dearer than opening another, so that step needs no rule for the mark; that is true only while the wait works, which makes the wait part of the walk's correctness on marked text and not only of its speed. The walk counts the steps it takes apart and `StructuredAppendLaneWalkTest` bounds them below and above, by the text's length since a failed lane keeps closing to the end of it; a wait that half works changes plans, which the parity tests catch, and one that stops changes the count. `StructuredAppendStreamTest` walks the cut across the mark a character at a time at three versions, with and without a pair ahead of it, and holds the round trip under both segmentations, the refusal of a run of marks no symbol can keep off its head, the symbol count of a marked text to its unmarked twin's (after a line feed, after an alphanumeric, and after a run of digits at the sixteen-symbol limit, there the version too), and the plan that does open a Byte run at the mark under a declared ISO-8859-1. `StructuredAppendLaneWalkTest` holds the lanes to the scalar walk on texts with a mark a line, after digits, after a pair and at the head, and on a lane's last characters.

**The parity is of the bytes written, also when a forced charset does not hold the text.** `EciMode.Iso8859_1` forced on text outside Latin-1 is lossy by the caller's choice, and the Byte writer writes the transcoder's replacement byte on the targets that go through it and the low byte on the ones that narrow; the parity takes the same byte per target, so it is still the XOR of what the symbols carry.

**Each symbol is the stream `Create` would write for its chunk, behind the header.** The header precedes the ECI header, then the mode segments; the per-symbol pipeline from data codewords on is unchanged, which is what keeps every existing invariant (padding, error correction, interleaving, masking) in force for a set.

---

## Pipeline

### 1. Validate the matrix request

All generation overloads reject:

- requested versions outside `1..40` (except `-1`, meaning automatic);
- negative quiet-zone sizes.

`TryGetRequiredBufferSize` and the span-output overload additionally reject quiet zones whose resulting side or squared byte count exceeds `int.MaxValue`. The span overload also rejects caller-provided buffers smaller than the calculated matrix. These paths compute dimensions with `long` arithmetic before narrowing to `int`, preventing overflow in `coreSize + 2 * quietZoneSize` and `totalSize * totalSize`.

### 2. Analyze text and choose mode / ECI

`TextAnalyzer` performs a single pass over the UTF-16 input and classifies the entire payload:

1. Numeric when every character is `0..9`.
2. Alphanumeric when every character belongs to the 45-character QR alphabet.
3. Byte otherwise.

Empty input is deliberately represented as a zero-length Byte segment. The standard does not define a special empty-data mode, and Byte mode gives the least surprising representation.

With `EciMode.Default`, Byte-mode character encoding is selected as follows:

| Input | Effective ECI | Header |
|---|---|---|
| ASCII only | Default | none |
| Contains non-ASCII, but every character is in U+0000..U+00FF | ISO-8859-1 | ECI 3 |
| Any character above U+00FF | UTF-8 | ECI 26 |

Numeric and Alphanumeric payloads do not need character-set conversion, although an explicitly requested non-default ECI is still emitted before the data-mode indicator.

Explicit ECI is a caller constraint. In particular, forcing `Iso8859_1` is only semantically correct for text in U+0000..U+00FF; `Default` avoids an incompatible choice by upgrading such input to UTF-8.

On supported x86/x64 runtimes, analysis uses AVX2 or SSE2 for character-class checks; on ARM64 it uses a NEON tier (16 chars per step with 8-wide vector remainder blocks). A scalar path covers short inputs and other targets.

### 3. Select the version

Automatic selection scans versions 1 through 40 and picks the first whose data-codeword capacity can hold:

```
optional ECI header
+ 4-bit mode indicator
+ version-dependent character-count indicator
+ encoded payload bits
```

The character-count width changes at versions 10 and 27:

| Version range | Numeric | Alphanumeric | Byte |
|---|---:|---:|---:|
| 1–9 | 10 | 9 | 8 |
| 10–26 | 12 | 11 | 16 |
| 27–40 | 14 | 13 | 16 |

Byte-mode capacity is calculated from encoded byte count, not UTF-16 `char` count. UTF-8 BOM contributes three bytes to both capacity selection and the Byte-mode character-count indicator.

Only UTF-8 is actually counted. Latin-1 is one byte per `char`, out-of-range ones included: the writer narrows each `char`, and the encoder replaces each `char` it cannot represent with one byte of its own, a surrogate pair counting as the two code units it is rather than the one scalar it spells. So a charset the caller forced over content it cannot represent still produces a well-formed symbol, which is what makes the mojibake the caller asked for readable as mojibake rather than a stream a reader takes apart wrongly. Which of the two charsets applies is what the classification pass has just decided, so it hands the answer over instead of letting the count re-derive it with a second scan of the text.

The version calculation does not reserve four mandatory terminator bits: the terminator is allowed to shrink to the remaining capacity, including zero bits for an exact fit. If no version can hold the required header and payload bits, generation fails instead of truncating.

When `QRCodeGeneratorOptions.Version` pins a version, automatic selection is bypassed. It is intended for callers that need a fixed symbol size and already know the payload fits.

#### Version ranges

`QRCodeGeneratorOptions.Version` is a `QRVersionRange` rather than a single version, and the scan runs over `[Min, Max]` instead of 1 to 40. A pinned version is the degenerate `Exactly(n)` case, so there is one concept rather than a requested version and a range that could contradict each other. The range's bounds are validated when it is constructed, before any generator is called, and both are **inclusive** — which is why this is a domain type and not C#'s `..`, whose end is exclusive and would make `1..40` mean 1 through 39.

Two behaviours were introduced with the range and are now unconditional. Both differed from the 1.1.1 `requestedVersion` parameter, which was removed in 2.0.0:

- **A range narrower than 1-40 is checked for fit.** `Exactly(n)` reports content that does not fit version *n* as `false` from `TryGetRequiredBufferSize` (or an `ArgumentException` from `Create`). The removed parameter handed the version straight to the encoder and failed deep inside with `ArgumentOutOfRangeException (Parameter 'length')` from a span slice.
- **Sizing honours the version.** The released `GetRequiredBufferSize` had no `requestedVersion` parameter, so an ignored `Version` would have been a silent trap. `TryGetRequiredBufferSize` reports the version the range resolves to, matching what Micro QR and rMQR already do.

`QRVersionRange.Any` short-circuits to the automatic path before any range resolution runs, so the default costs nothing extra; a constrained range or an ECC boost pays for the additional text analysis its resolution needs, since a boost has to know the version before it can raise the level.

**The scan does not assume the fit predicate is monotone in the version**, even though it is. It could plausibly not be: the character-count indicator widens at versions 10 and 27, so a larger version costs more header bits. Scanning `[Min, Max]` is correct either way, and `VersionRangeTest.StandardQr_FitsIsMonotoneInVersion` sweeps 3 modes × 4 ECC levels × 3 ECI modes × 58 lengths × 40 versions to keep the monotonicity a checked fact rather than an assumption the code rests on.

#### ECC boost

`QRCodeGeneratorOptions.BoostEccLevel` reinterprets the requested ECC level as a minimum: the version is chosen for that level exactly as above, then the level is raised while the next one still fits the chosen version. Because the version is fixed before the boost starts, boosting **never grows the symbol** — it converts padding the symbol would carry anyway into error-correction capacity. The main audience is symbols with an icon overlay, where the spare capacity absorbs the covered modules.

- **Off by default.** A raised level rewrites the format information and can change the winning mask, so a default of on would silently change every existing symbol; existing tests, golden pixels and playground permalinks all assume a requested level is the emitted level.
- **Sizing ignores the flag.** The buffer size depends only on the version and the quiet zone, and the boost cannot change the version, so `TryGetRequiredBufferSize` reports the same answer either way. This is documented on the API rather than left implicit.
- **The error contract is unchanged.** Content that fits no version in the range fails with the same exception, message included, as the boost-free path — the unconstrained overflow stays `InvalidOperationException` (the released contract), a constrained range stays `ArgumentException`. Turning boost on must not reclassify an error.
- **Standard QR only.** Micro QR ties its legal levels to the version (M1 has none, only M4 offers Q), so a boost there would interact with version selection instead of following it; rMQR has a single M→H step. Either can adopt the same contract later.

`EccBoostTest` pins the headroom classes (boost to H, stop at an intermediate level, no headroom, already at H), the version invariance, the sizing indifference and the error parity.

#### Mixed-mode segmentation

**What.** `QRSegmentation.Optimal` splits the content into the Numeric / Alphanumeric / Byte runs whose total bit cost is minimal for a candidate version, and fits the version against that cost instead of the single-mode cost. `QRSegmentation.Single` (the default) keeps one run in one mode.

**Why.** Mixed payloads pay the whole-content mode for every character under a single segment: a URL prefix followed by a long numeric identifier is all Byte, so the digits cost 8 bits each instead of 3⅓. Splitting the digits off routinely drops the symbol a version or more (`https://example.com/item?id=` + 30 digits: version 4-M as one Byte run, version 3-M split).

**Why opt-in, and why the ceiling.** Changing the default would move the emitted bit stream, and therefore the rendered symbol, for existing callers. When the content fits in a single mode, that fit caps the scan from above: only strictly smaller versions are tried, so a plan is emitted only when it lowers the version, and the single-mode stream is emitted byte for byte in every other case. The end-to-end tests assert both properties for every corpus entry.

**Why the scan needs almost no bounding machinery.** The character-count indicator widths are constant within the three version bands (1–9 / 10–26 / 27–40), so the optimal cost is itself constant within a band: the scan computes it at most once per band — three O(n) cost runs in the worst case, no reconstruction table — and compares it against each candidate capacity. rMQR needed a trivial bound, a floor and a re-priced ceiling because its 32 versions carry 13 distinct width triples across a strategy-ordered ranking; a totally ordered version set with banded widths makes the floor and the ceiling unnecessary, which is a lesson worth keeping next to the rMQR one rather than porting the bounds by reflex. The trivial bound alone did carry over — one O(n) pass pricing each character at the cheapest rate any mode could give it — because without it, content no split can shrink still paid for a band cost run: measured on 120 single-mode characters, the Optimal arm went from 1.8x the Single encode to roughly parity, while the winning shapes were untouched. Its blind spot is the same as rMQR's: finely alternating content clears the bound and pays for planning that then gains nothing, because seeing that switching modes every character never pays *is* the dynamic program.

**When no single mode fits.** The ceiling does not exist, so the scan runs to the window's end. This is the one place `Optimal` accepts input `Single` rejects: 1,000 lowercase letters followed by 4,500 digits is 5,500 Byte-mode characters, far over the 2,953 version 40-L holds, but well inside its 23,648 bits once the digits split off. Only when a mixed plan fails as well does the path throw, with the single-mode path's exact exception type per constraint shape, so turning segmentation on cannot reclassify an error.

**Content that cannot benefit.** All-Numeric content skips planning: no mode prices a digit below Numeric and every extra run adds a header, so one run is provably the optimum. The rule is one predicate rather than a repeated condition, because every caller that answers it with no skips a whole cost run and would emit a different stream if it were ever wrong: the version scan, the Structured Append chunk cost (which the ECC boost re-asks per level) and the per-symbol writer all ask it, and `QRSegmentPlannerUnitTest` pins it against the program itself at every version band, both charsets and the lengths that land mid packing group.

**How the optimum is exact.** A run does not cost a constant per character (Numeric packs 3 digits into 10 bits, Alphanumeric 2 characters into 11), so the dynamic program carries the packing-group remainder in its state rather than rounding a per-character average. The state layout and transitions are in `ModeSegmenter`, shared with the rMQR and Micro QR planners — the symbologies differ only in header widths, taken as parameters, so one implementation keeps the cost models (the UTF-8 surrogate rules included) from drifting apart. Micro QR additionally passes per-version mode availability (M1 is Numeric-only, M2 has no Byte mode), which disables the missing transitions. `QRSegmentPlannerUnitTest` holds the program to an independent exhaustive mode-assignment optimum on short content across the bands and charsets, and the rMQR suite holds the same code to its own independent oracle across all 32 versions.

**Bounds.** Content longer than the largest character count any version holds in any mode (7,089, Numeric at 40-L, an exact fit) is rejected before any cost run; the margin is 4 bits, and the derivation sits with the constant in `QRSegmentPlanner` so a capacity-table change re-derives rather than nudges it. The plan buffer is stack-allocated for content up to 64 characters and pooled at text length above that — a plan cannot hold more runs than the content has characters, so the pooled path can never fail for space. The reconstructed plan is re-costed from the byte counts the encoder will actually emit and rejected on disagreement, because the bit-stream writers store without per-flush bounds checks.

**Composition with the other options.** The BOM is a stream-level prefix, and a split would relocate it into the middle of the decoded text, so `Utf8Bom` falls back to the single-mode stream exactly when a BOM would actually be written — a UTF-8 Byte-mode stream. Content whose single mode is Numeric or Alphanumeric never carries a BOM (even under an explicitly requested UTF-8 charset) and still splits; the first cut of the gate suppressed those too, and code review caught it costing a full version for nothing. A version range narrows the scan window; a pinned version that only a mixed plan fits succeeds where `Single` throws. ECC boost runs after the plan is fixed and compares the exact planned stream bits against the higher level's capacity, keeping the version-invariance contract. A pinned mask applies at the matrix stage, orthogonally. Argument validation keeps the quiet-zone-first precedence of the other surfaces, and an undefined segmentation value reports the same `segmentation` parameter name on the generators and the builder.

**Plans the byte-segment decoder would misread are never built.** The shared byte-segment decoder consumes a leading EF BB BF of every segment without an explicit ISO-8859-1 declaration, even behind an explicit UTF-8 ECI, so a plan that opens a Byte run at a mid-content U+FEFF would decode with the character silently dropped. Under UTF-8 the segmentation program has no such transition (`ModeSegmenter.ByteOrderMark`): at a U+FEFF past the first character a Byte run only continues, so the run the mark is in opens a character early and keeps it interior, and the cheapest safe plan is the optimum. Every text still has a plan, its single Byte run, so the rule refuses nothing, and a text whose unconstrained optimum was safe keeps that plan bit for bit. The first character is exempt because the single-mode stream starts with the same bytes and loses the mark identically. All three planners share the rule through the shared program, in the cost-only loop, the loop with parents, the prefix walk and both vector walks; `ModeSegmenterPlanParityTest` holds the three scalar loops to an all-states reference that carries the same rule, the writer's lane per chunk is held to the scalar plan by `StructuredAppendWriterPlanTest` and the budget search's lanes to the scalar walk by `StructuredAppendLaneWalkTest`, and each symbology's segmentation test holds the round trip and the smaller symbol. `ModeSegmenter.HasBomRelocatedToARunStart` and the Standard QR plan pricing keep the old refusal as the answer to a model that disagreed. Micro QR, which has no ECI to pin a charset, additionally rejects plans containing a non-ASCII Latin-1 Byte run whose narrowed bytes the decoder's unspecified-charset resolution would read as UTF-8 (`SegmentDecoders.ResolvesToUtf8WhenUnspecified`, kept beside the resolution it mirrors): isolating such a run from its disambiguating invalid neighbours would decode it as different text, and there only the minimal-bit plan is checked, so such content reports "does not fit" when no single mode holds it. Lesson: a mixed-mode plan is only as good as the decode it produces, and cost optimality had to be constrained by the decoder's per-segment charset heuristics, which adversarial review caught by reproduction, not inspection. The U+FEFF constraint was first a rejection of the minimal plan with a fallback to the single-mode stream, with the known gap that a slightly costlier safe split was never searched (3,000 digits + U+FEFF + 10 letters reported "does not fit"); moving it into the program was deferred until the input class mattered, and Structured Append is where it did, since a set sizes sixteen chunks by plans and a refused plan there is a symbol written as a stream its chunk was never sized for.

**ECI.** One prefix ahead of the first run: a decoder carries the declared charset across the runs that follow, so a plan needs no repetition. Its 12 bits are part of the cost the version scan compares.

**Kanji.** Still not encoded, so a Japanese payload mixes Byte (UTF-8) with Numeric runs rather than reaching for 13-bit Kanji. The decoder reads Kanji segments other encoders produce.

### 4. Build the data codewords

`QRBinaryEncoder` writes MSB-first through `BitWriter`:

1. Optional ECI indicator `0111` and 8-bit assignment number.
2. Data mode indicator.
3. Character-count indicator.
4. Mode-specific payload.
5. Up to four zero terminator bits.
6. Zero bits to the next byte boundary.
7. Alternating pad codewords `0xEC`, `0x11` until the data capacity is full.

Mode-specific packing is:

| Mode | Packing |
|---|---|
| Numeric | 3 digits → 10 bits; final 2 → 7 bits; final 1 → 4 bits |
| Alphanumeric | 2 values → `first * 45 + second` in 11 bits; final 1 → 6 bits |
| Byte | 8 bits per encoded byte |

Byte mode uses ISO-8859-1 narrowing or UTF-8 encoding. Temporary charset buffers use `stackalloc` up to 256 bytes and `ArrayPool<byte>` above that threshold. `BitWriter` stages bits in a 64-bit accumulator and bulk-writes big-endian words; Byte-mode data is copied eight bytes at a time where possible.

### 5. Generate Reed-Solomon error correction

The selected version and ECC level identify:

- total data codewords;
- ECC codewords per block;
- Group 1 and Group 2 block counts;
- data codewords per block in each group.

Data codewords are partitioned by that table, and `EccBinaryEncoder` calculates the Reed-Solomon remainder independently for each block over GF(256), using primitive polynomial `0x11D` and generator roots `alpha^0..alpha^(n-1)`.

The public dispatch selects the fastest available kernel while preserving byte-identical results:

- GFNI / SSSE3 on supported x86/x64 targets;
- AdvSimd on ARM64;
- cached log-domain scalar implementation elsewhere.

Every optimized kernel is parity-tested against a deliberately naive polynomial-division reference.

### 6. Interleave the final message

`BinaryInterleaver` emits:

1. data codeword 0 from every block, then data codeword 1 from every block, and so on;
2. the extra final data row from the longer Group 2 blocks, when present;
3. ECC codeword 0 from every block, then ECC codeword 1, and so on;
4. zero remainder bits for the selected version.

The implementation writes the output sequentially and accepts strided source reads. A one-block symbol takes an identity fast path. Remainder-bit storage is cleared explicitly so uninitialized stack or pooled memory cannot affect the matrix.

### 7. Place function patterns and data

The encoder places or reserves (the byte-per-module core is fully written by the placer; no zeroing is required):

- three 7×7 finder patterns;
- one-module separators;
- alignment patterns for version 2+;
- horizontal and vertical timing patterns;
- the fixed dark module;
- both format-information areas;
- both version-information areas for version 7+.

Reserved modules are represented by a compact bit mask. Both the painted function modules and the bit mask are built once per version by the reference `PlaceFunctionModulesReference` painters and cached (`ModulePlacer.PlacementLayout`); the encoder copies them per symbol and the decoder reads the same cached mask when it needs to distinguish function modules from data modules, keeping both directions structurally identical.

Interleaved bits are then consumed MSB-first in the standard two-column zigzag from bottom-right to top-left, skipping column 6 and every reserved module. The reference walk keeps up to 64 pending stream bits in a register and handles both modules of a strip row together; the production placement uses the cached walk (core index per stream bit, rows where both strip modules are free as runs): the stream is expanded to one byte per bit and each run row is a single 16-bit store, everything else an index-table scatter (parity-tested against the reference walk for every version).

### 8. Evaluate all eight masks

The encoder tests every Standard QR mask pattern and chooses the lowest ISO/IEC 18004 penalty score:

1. long same-color runs;
2. 2×2 same-color blocks;
3. finder-like `1:1:3:1:1` patterns with the required light margin;
4. deviation from 50% dark modules.

Each candidate is scored as the final symbol will appear:

- the mask is applied only to data modules;
- candidate-specific format bits are inserted before scoring;
- version bits are included for version 7+.

This matters because format modules participate in the visual penalty rules and can change which mask wins. Ties are deterministic: the lower mask index wins because candidates are visited in order and only a strictly lower score replaces the current best.

Masking and scoring operate on packed rows rather than byte-per-module loops:

- versions 1–11 fit each row in one `ulong`;
- versions 12–40 use a fixed 192-bit row made from three `ulong` values.

The eight formulas are precomputed as 12-row periodic templates. XOR, shifts, and popcount implement both masking and all four penalty rules without changing the reference result. On AVX2 the versions 1-11 tier scores four candidates per vector (lane = pattern) with per-version tables of pre-masked templates and format-bit overlays; the larger tiers score four rows per vector. Parity tests compare every representation against straightforward textbook formulas.

**Pinned mask.** `QRCodeGeneratorOptions.MaskPattern` (0-7, `null` = automatic) skips the evaluation entirely and applies that one pattern via a scalar per-module loop. Any pattern is a legal symbol — the specification only recommends the best scorer — so pinning exists for byte-exact reproduction of symbols produced elsewhere (the decoder reports the pattern in `QRCodeDecodeInfo.MaskPattern`) and for exercising decoders against all eight patterns. Invalid values are rejected when the option is set, like `Version`. Micro QR offers the same option over its four patterns (`MicroQRCodeGeneratorOptions.MaskPattern`, an unrelated numbering — see the Micro QR spec map); rMQR has a single fixed mask, so it has no such option.


### 9. Write format / version information and expose output

After the winning mask is applied:

- BCH(15,5) format information encodes ECC level and mask index, applies the standard format mask, and is written twice;
- BCH(18,6) version information is written twice for versions 7–40.

For `QRCodeData`, the temporary core matrix is packed into the object's one-bit-per-module payload; the quiet zone remains virtual.

For span output:

- with quiet zone 0, the core pipeline writes directly into the destination;
- with a quiet zone, the contiguous core is built in a pooled temporary and copied row-by-row into the centered destination.

The encoder produces a module matrix, not an image. Color, pixels-per-module, shapes, gradients, icons, PNG/SVG encoding, and other presentation concerns belong to `SymbolRenderer` / `QRCodeImageBuilder`.

---

## Why

- **One canonical binary pipeline.** String and span inputs, object and span outputs, and all rendering APIs ultimately depend on the same mode, ECC, interleaving, placement, and masking logic.
- **Tables drive structural correctness.** Capacity, block grouping, alignment centers, and remainder counts come from centralized Standard QR tables rather than duplicated conditionals.
- **Encoder-decoder parity by construction.** Function-module layout, format generation, and block conventions are shared or tested in both directions.
- **Independent validation.** ZXing decodes generated symbols across modes, ECI choices, ECC levels, boundary capacities, and large versions. The in-process decoder adds all-version round trips and error-injection coverage.
- **Optimizations are representation changes, not algorithm changes.** The production kernels are guarded by parity tests against simple reference implementations for ECC, interleaving, placement, and mask scoring.
- **Deterministic output.** Version scan order, block order, interleaving order, mask tie-breaking, zeroed remainder bits, and clean destination handling make repeated calls byte-for-byte stable.

---

## Decisions

- **Single segment per input, by default.** It keeps the default path auditable and makes mode selection a single pass; the trade-off, non-minimal symbols for mixed-mode payloads, is answered by the opt-in `QRSegmentation.Optimal`, which never changes the emitted stream unless it lowers the version.
- **No Kanji mode when encoding.** Unicode input is represented as UTF-8 Byte mode with ECI 26, at the cost of lower capacity for Japanese text. This originally also avoided shipping a Shift_JIS table; that argument lapsed when Kanji DECODING shipped and the assembly gained the 16 KB JIS X 0208 table, so the remaining reasons are output stability and not adding an encoding dependency.
- **ASCII omits ECI by default.** This minimizes overhead and maximizes compatibility. Latin-1 and wider Unicode receive explicit ECI declarations under automatic selection.
- **BOM is explicit and UTF-8-only.** `QRCodeGeneratorOptions.Utf8Bom` affects the stream only when the selected data mode is Byte and the effective ECI is UTF-8.
- **Version can be forced.** Fixed-size applications need control over symbol dimensions, so a pinned `QRCodeGeneratorOptions.Version` bypasses minimum-fit selection rather than acting as a lower bound.
- **Quiet zone is output policy, not core symbol data.** Core encoding is always performed on the `21 + 4 * (version - 1)` matrix. Quiet-zone storage differs by output model without changing encoded modules.
- **Mask scoring includes final metadata.** Scoring a data-only candidate can choose a different winner from scoring the actual final matrix, so format and version information are part of candidate evaluation.

---

## Lessons Learned

### Capacity and text encoding

- **Byte-mode length means encoded bytes, not UTF-16 characters.** This is the central boundary condition for UTF-8 input; using `text.Length` would under-size every non-ASCII payload and make version transitions wrong.
- **The UTF-8 BOM belongs in the Byte character count.** Treating it as out-of-band metadata creates symbols that some readers reject because the declared count is three bytes short.
- **ECI overhead can change the version and the mask.** Twelve header bits can cross a version boundary; even when they do not, they shift every following bit, which changes ECC, interleaving, placed data, and often the selected mask.
- **Exact-capacity inputs need no full terminator.** Padding must add `min(remaining, 4)` terminator bits rather than assuming four bits are always available.
- **A branch test whose fixture cannot reach the branch asserts nothing.** The first ECC-boost-under-segmentation test used content whose planned stream (350 bits) could never fit the next level's capacity (272), so its `>= requested level` assertion held with the boost never firing; adversarial review caught it by computing the headroom. Boost and threshold tests must pin the exact resulting level on a fixture whose arithmetic is worked out in the test comment.
- **Sub-microsecond benchmark tables need a noise disclaimer.** Rows where an arm doing strictly more work measures "faster" (Optimal beating Single on all-numeric content that never plans) are code-layout and run-to-run jitter; single-iteration BenchmarkDotNet jobs on this class of benchmark swing ±30%, so ratios are only trusted from MediumRun jobs and deltas within a few percent are not signal.

### Matrix construction

- **Function areas need one shared source of truth.** Placement, data walking, masking, and decoding all depend on exactly the same blocked-module geometry. Reconstructing it independently is an invitation for one-module drift around format, alignment, or version areas.
- **Remainder bits must be deterministic even though they carry no payload.** Stack and pooled buffers are not guaranteed to be zeroed; leaving the tail untouched makes output depend on prior memory contents.
- **Mask candidates must contain their own format bits.** The 30 format modules affect runs, 2×2 blocks, finder-like windows, and dark balance. Scoring without them is observably not the same algorithm.
- **The quiet zone should not inflate object storage.** Keeping it virtual reduced `QRCodeData` to core bits only while preserving the public matrix coordinate space.

### Performance

- **Bit-packing was the decisive mask optimization.** Parallelizing eight expensive byte-domain candidates still pays the byte-domain cost and adds scheduling/allocation overhead. Packed scalar rows measured roughly 8× at version 1, 44× at version 10, and 30–40× at version 40 over the former per-module implementation.
- **For the small versions the eight candidates are the vector lanes, not the rows.** With one `ulong` per row (versions 1-11), scoring four candidates per vector removes every scalar tail and every per-candidate horizontal reduction, and lets the pre-masked templates and the format-bit overlays be per-version tables (one XOR / OR per row); together with fusing popcounts of provably disjoint bit sets (dark vs light 5-runs, the two finder-like orientations) this halved the mask kernel again (1.6-1.9x) after the lane-per-row round. Fusing all scoring passes into one loop lost (register pressure), and vectorizing the balance score bought nothing measurable.

- **Sequential output wins during interleaving.** Round-robin source reads with a contiguous destination measured better than sequential source reads with scattered writes, despite the strided access.
- **The data placement stream should stay in a register.** Refilling a 64-bit MSB-aligned accumulator removes a byte load and variable shift from each module and enables a two-module fast path for the common unblocked case (the reference walk).
- **Everything the placer derives from the version alone belongs in a per-version table.** Painting the function patterns, building the blocked bit mask and deciding the zigzag order per symbol was ~25-35 % of the encode; a cached template + mask + walk order (built by the reference painters, so correct by construction) turned the placer into a memcpy plus one vector bit expansion and a run/scatter store pass: 9x at version 1 and 4.5x at version 40 in the kernel, -26 % (v1) to -44 % (v40) on the encode E2E, and the decoder shares the cached mask. Strided byte scatter is store-issue bound; wider stores per row do not help (same finding as the rMQR placer).
- **Reed-Solomon setup is reusable.** Generator polynomials depend only on ECC count, so caching their log-domain form removes repeated polynomial construction and reduces the scalar inner loop to table lookup and XOR.
- **Steady-state allocation guarantees require warm-up-aware tests.** Lazy tables, JIT compilation, and `ArrayPool` initialization are one-time effects; the Release-only allocation test warms them before measuring the span API.

---

## Validation

The encoder is covered at several independent layers:

| Layer | Evidence |
|---|---|
| Bit stream | mode, ECI, count widths, Numeric/Alphanumeric/Byte packing, BOM, padding, canonical `HELLO WORLD` codewords |
| Capacity | exact-fit and one-over boundaries across modes, ECC levels, and representative versions |
| ECC | ISO worked examples and scalar/SIMD parity against naive GF(256) division |
| Interleaving | unequal block groups, single-block identity, version-40 block counts, naive-reference parity |
| Placement | binary placement parity against a per-module zigzag reference |
| Masking | all-zero, all-one, and realistic matrices compared with byte-domain reference formulas |
| Output APIs | `QRCodeData` and span matrices compared module-for-module, dirty buffers, quiet-zone sizes, overflow checks, allocation test |
| External compatibility | generated images decoded by ZXing |
| Internal compatibility | encode/decode round trips for all versions and ECC levels |

When the optimized implementation and a reference disagree, the simple reference and external decoder are treated as the specification oracle; performance code is not allowed to define behavior.
