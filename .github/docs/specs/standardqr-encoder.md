# Standard QR Encoder

This record covers what the Standard QR encoder (`QRCodeGenerator`) does, why its pipeline is built this way, and what was learned making it spec-compatible and fast. The [spec-to-code map](standardqr-spec-map.md) indexes the normative details and their implementation. [Standard QR Decoder](standardqr-decoder.md) documents the inverse pipeline.

---

## What

`QRCodeGenerator` converts text into a Standard QR module matrix through the full ISO/IEC 18004 encoding pipeline:

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

`Create(ReadOnlySpan<char>, ...)` returns a `QRCodeData`. A `string` converts to the span implicitly, except on the netstandard2.0 asset below C# 14.

- The core matrix is stored bit-packed, one bit per module.
- The quiet zone is virtual: it changes the public coordinates but uses no payload storage.
- The same text and options give the same matrix.
- Renderers and serialization use this object model.

#### Caller-provided matrix buffer

`Create(ReadOnlySpan<char>, ..., Span<byte> destination, ...)` writes:

- one byte per module.
- `0` for light and `1` for dark.
- flat row-major order.
- quiet zone included.

`TryGetRequiredBufferSize` returns the required matrix side, byte count and selected version. It returns `false` when the content exceeds every version in range. Argument errors (an undefined ECC level, a quiet zone outside 0 to 10,000) still throw, so `false` only means "does not fit".

It is the only sizing method. The throwing `GetRequiredBufferSize` released in 1.1.1 was deprecated in 1.2.0 and removed in 2.0.0. [rmqr-encoder.md](rmqr-encoder.md) records why sizing is split this way and why it is a `Try` method rather than a dedicated exception type. Standard QR follows the same rule, so all three symbologies share one surface.

The encoder overwrites every byte of the region it writes: the core is copied from a per-version template, and only a non-zero quiet zone is cleared. It accepts a dirty pooled destination and leaves any tail past the returned byte count untouched. After JIT and pool warm-up, the span path allocates nothing in Release builds.

### Supported

| Area | Coverage |
|---|---|
| Symbology | Standard QR |
| Versions | 1–40 |
| ECC levels | L, M, Q, H |
| Data modes | Numeric, Alphanumeric, Byte, and Kanji |
| ECI | Default/no header, ISO-8859-1 (assignment 3), UTF-8 (assignment 26) |
| UTF-8 BOM | Optional in UTF-8 Byte mode |
| Version selection | Automatic minimum-fit, caller-requested version, or a version range |
| ECC boost | Optional: the requested level is a minimum, raised as far as the chosen version's capacity allows without changing the version |
| Segmentation | One segment in one mode by default. Opt-in mixed-mode segmentation (`QRSegmentation.Optimal`) splits the content into the Numeric / Alphanumeric / Byte runs with the fewest bits |
| Quiet zone | Configurable non-negative size. Span sizing and span output reject dimensions that cannot fit an `int`-sized matrix |
| Structured Append | `CreateStructuredAppend` splits text across the fewest symbols (2 to 16) at one shared version within `QRCodeGeneratorOptions.Version`, balanced so the fullest symbol is as empty as possible. Text that fits one symbol in the range returns the plain symbol. See [Structured Append](#structured-append) |
| Output | Bit-packed `QRCodeData` or byte-per-module `Span<byte>` |

### Not implemented

- FNC1
- Arbitrary ECI assignment numbers
- Arbitrary binary payload input
- Micro QR and rMQR

By default the encoder analyzes the input once and emits one data segment. `QRSegmentation.Optimal` selects the globally minimal mixed-mode split instead (see [Mixed-mode segmentation](#mixed-mode-segmentation)).

### Structured Append

ISO/IEC 18004 lets one message span up to sixteen symbols, each with a 20-bit header before its segments: mode `0011`, a 4-bit position, a 4-bit count minus one and an 8-bit parity. `CreateStructuredAppend` writes such a set, and a reader reassembles it by the rules on `QRStructuredAppend`.

The caller's version cap decides the symbol count. "Fewest symbols" needs a bound, since without one version 40's 2,953 bytes make it one symbol. The bound is the largest symbol the caller can print, which `QRCodeGeneratorOptions.Version` already expresses, so there is no count parameter: the caller passes `QRVersionRange.AtMost(n)`.

Text that fits one symbol in the range gets `Create`'s symbol, with no header: a set of one costs 20 bits and tells a reader nothing.

The planner prices every chunk with the set header, so alone it split a text reaching into a symbol's last 20 bits in two, and refused a single character too wide for a symbol with the header, which `Create` holds. So when it splits or refuses a text that could be one symbol, the one-symbol question is asked again as `Create` asks it, without the quiet zone.

The chunk count cannot tell which texts need this: at version 1-H, the six bytes of "a", a kana and "aa" fit one symbol to the bit yet make three chunks. The measure the search ran on can, and the planner has it before counting: the whole text's plan or, where no plan beats the single-mode stream, that stream's closed form. No stream of the text costs less, so a text whose measure does not fit the largest symbol without the header, as in nearly every set, is not asked again.

Asking every text shorter than a symbol's best case (ten bits per three characters) cost a segmentation run that `Create` repeated: a fifth more time on a two-symbol, label-sized set of order lines, and a tenth more on prose. The questions are counted, and a label that is plainly a set must ask none.

A text held in one chunk goes straight to `Create`, so the common case costs one plan, not two.

Three searches balance the split: the fewest symbols at the largest version, the smallest version in the range that holds that count, and the smallest per-symbol budget at that version that holds it. Splitting at that budget gives every symbol one version and none fuller than necessary. A greedy fill would leave three full symbols and one nearly empty, which looks like a defect on one label. Splits fall on `char` boundaries, never inside a surrogate pair. More than sixteen symbols at the largest version, or a single character that fits no symbol there, throws `ArgumentException` on the options.

A symbol's budget is its version's data capacity minus the 20-bit header and, when the set declares a charset, the 12-bit ECI header every symbol repeats. A chunk costs its single-mode stream or, under `QRSegmentation.Optimal`, the cheaper of that and the minimal mixed plan. Cost never falls as a chunk grows, so the longest chunk within a budget is well defined, and a greedy walk at a budget gives the count it needs.

The walk finds the chunk end without pricing whole prefixes: a prefix's mode changes at most twice along the text (Numeric, then Alphanumeric, then Byte), and within one mode the single-mode cost is a closed form of the length. So the end takes a few arithmetic steps, plus one pass over the run's bytes for a UTF-8 Byte run.

Under `Optimal` the mixed plan's candidates include the single-mode plan, so the answer is one forward pass of the segmentation program, which yields every prefix's optimum and stops at the first over budget. The pass is skipped inside an all-digit run, which no split improves. Behind a byte order mark it covers only the leading alphanumeric run, because only a Byte-mode chunk carries a mark, and a mark forces the chunk's single-mode stream.

The first version priced each binary-search probe with a full analysis (and, under `Optimal`, a full cost run) of a prefix that could reach the end of the text. `StructuredAppendPlannerTest` checks the fast path against a reference walk pricing one character at a time by the cost definition, at every budget where the answer can change.

A run only Byte can encode costs one addition per character, not a state-machine step. A character outside the alphanumeric alphabet can only extend Byte, so after one, the program's five other mode states stay unreachable until an alphanumeric character arrives. The loop detects this and adds the bytes to the Byte state directly, recording the predecessor the state machine would have recorded. This path takes 86 % of characters in Japanese behind a UTF-8 declaration, 55 % in English prose and 24 % in the digit-and-word mixture.

All three symbologies share the program, so `ModeSegmenterByteRunParityTest` checks it against an independent program that always walks all seven states (the six mode states and the virtual start), on cost, final state and reconstructed plan, across the bands, charsets and the mode-availability flags Micro QR passes.

The program's step is arithmetic on six registers, one per mode state: each target state continues its run or opens it from the cheapest state, one add and one min over the previous character's six costs. The first form relaxed every state from every state through two stack arrays, paying a bounds-checked write per relaxation, a 28-byte copy per character and, on the prefix walk, a six-state scan per character, memory traffic that cost six times the arithmetic (13 ns per character against 2).

The common shape, a Latin charset with Byte allowed, has its own loops for costs, costs with parents and the prefix walk, so the constant byte cost and mode flags are not re-decided per character. UTF-8 and the Micro QR versions lacking a mode share one general loop per entry point.

Where parents are recorded, candidates are tried in state order and a later one wins only when strictly cheaper, the relaxation's tie-break, so the writer emits the same plan byte for byte. On a Latin symbol a character's three varying parents take two byte stores: one for the Byte state's parent, and one that packs the Numeric1 and Alnum1 parents.

`ModeSegmenterPlanParityTest` checks every loop against the all-states relaxation and its predecessor rule on cost, final state, reconstructed runs and the walk's stopping point at every budget where that point can change, across the bands, three charsets, the four mode-availability combinations and seeded random mixes of every class.

On the digit-and-word mixture, the walk went from 12.8 to 2.0 ns per character and the per-symbol plan from 11.2 to 3.0. These shapes lost:

- a vector form of the step, because the broadcast and the horizontal minimum sit on the serial chain.
- a branch-free minimum, because the cost structure, not the content, decides which candidate wins, and the branch predictor learns it.
- a factored recurrence, which shortens a dependency chain the code does not have, since the minimums compile to branches.

The searches consult the segmentation program only where it could help. A mixed plan beats one run only if a run uses a denser mode than the content's and repays at least the mode and count indicators it adds: four digits or six alphanumeric characters at the narrowest band.

One pass finds the text's longest run of each kind, which answers for every chunk, since no chunk has longer runs than the text. On a no, the three searches run as `QRSegmentation.Single`, whose costs, and so whose split, are the same. English prose and Japanese hold neither run, so their sixteen passes over the text drop to none.

The per-symbol writer still plans where a tie is possible, on content whose longest digit run is three, because a tied minimum's reconstructed plan is not always the single run, and skipping it would change which of two equally priced streams a symbol carries. Everything below that is decided without the program (see "only where a plan can differ from one run" below).

`PlanCouldWinTest` checks the direction that matters: a no from the predicate must be a no from the program, for runs of every length near the thresholds, at the start, middle and end of Byte-mode content, across the bands and both charsets.

The budget search's bracket is computed, not taken from capacity. Probes carry the split's cost: each walks the text into chunks and, under `QRSegmentation.Optimal`, each chunk costs a forward pass of the program, so the search costs one pass per bisection step, and the step count is the log of the bracket's width.

Between the rate bound and the version's capacity the bracket is thousands of bits wide, with the answer tens of bits from one end, so the floor is computed in one pass: the whole text's minimal plan divided by the symbol count, plus the headers every symbol pays. A split's plans concatenate into one plan for the whole text, so together they cost at least the minimal plan.

Only content whose probes are program passes computes this floor. `QRSegmentation.Single`, and content no plan can beat, keep the rate bounds, since their probes are arithmetic. `StructuredAppendPlannerTest` checks the bracket: the budget must hold the count, and one bit less must not.

One walk near that floor decides the count, the version and the ceiling. The balanced budget sits 3 to 22 bits above the plan's floor, separated by one chunk's rounding and a run header or two at the cuts, not by the content. This was measured on digit-and-word mixtures, alphanumeric, Latin-1 and UTF-8 content, content whose density changes along the text, and seeded random runs of every class, across three version ranges and two levels.

The first ceiling, the fullest chunk of the text cut into equal character counts, is within 55 bits of the floor only on periodic content. Where density varies it is hundreds to thousands of bits over the floor and usually past the capacity, so on real content it bracketed nothing (eight to ten probes instead of five), which the benchmark's repeated lines had hidden.

So under `Optimal` the planner first takes the whole text's plan, which gives the fewest symbols any split can use (its share of the capacity, rounded up), then walks once at the largest version at the floor plus 31 bits, limited to that count. If the walk holds the count:

- the count is that bound, since it cannot be fewer, and the walk at the capacity is skipped.
- a scan candidate in the same count indicator band with capacity at least the walk's budget holds the same split, because a chunk's cost depends on the version only through the band, so it is not walked either.
- the walk's budget becomes the budget search's ceiling, 31 bits above its floor, and its split is kept as the current answer.

Where the answer's band has no such walk (the scan went down a band, or the walk was refused), the search opens from the floor in widening steps of 31, 31 again, then tripling, each failure raising the floor. A surrogate pair is 32 bits that cannot be cut, so content of pairs sits 32 to 47 bits above its floor, inside the second step.

The first walk runs only when the bound's count leaves each symbol two margins of slack. With less, packing losses (small symbols at a high level lose up to a pair's width each) usually put that count out of reach, and the walk at the capacity finds the count as before. A text that fits the largest version as one single-mode stream is still answered by the walk's closed form before any plan.

Every shortcut is a lower bound or an existing split, so the plan is the one the three searches alone would find. `StructuredAppendPlannerTest` checks count, version and budget against a walk that prices one character at a time, on periodic content, content whose density changes, ranges that cross a band (9/10, 26/27), and the pair-heavy content that fails the first margin.

In program passes per plan, the digit-and-word mixture went from 8.9 to 7.0, digits followed by that mixture from 8.0 to 5.0, and random runs from 10.9 to 7.0. No shape takes more passes than before. Pricing the even cut's chunks as eight lanes of one vector program ran 4.6 times as fast as the scalar pricing and was dropped, since the design that replaced the even cut has no cut to price.

The budget search walks eight budgets at a time. After the walk near the floor, a plan under `Optimal` used to be the whole text's plan, that walk and five bisection probes inside its 31-bit bracket: seven program passes, five of them the same walk at budgets a few bits apart.

Reusing what neighbouring probes share, the first idea, gains less. A chunk's end never moves back as the budget grows, so from one start, a walk at a budget between a failed and a held budget ends its chunk between theirs and inherits, unpriced, the leading chunks both agree on. But a greedy chunk fills to within a character of its budget, so walks more than a character apart in budget diverge at the first chunk, and only a bisection's last probe or two share anything: 0.6 of 7 passes on periodic content, none elsewhere. The reuse stays for the remaining probes.

The larger gain comes from the probes being independent instances of one program over one text. Up to eight budgets are walked at once, one per vector lane, with one vector per state:

- Accelerated 256-bit vectors carry eight 32-bit costs.
- ARM64 NEON carries eight saturating 16-bit costs, since every symbol's budget fits that representation.
- Every other 128-bit target (x64 without AVX, and WebAssembly) carries the NEON form on portable vectors, with the saturating add that SSE2 or WebAssembly provides (2026-09-30).

On those targets the lane walks win only on chunks averaging 40 characters on x64 and 80 on WebAssembly, where interpreted code needs the longer chunks and one flag gates both builds. With lanes, the planner alone took 0.31 to 0.53 of its scalar probes' time on default NativeAOT, 0.61 to 0.69 on WebAssembly AOT and 0.26 to 0.75 interpreted, and an Optimal set took 0.76 to 0.83, 0.91 to 0.92 and 0.70 to 0.95 on the same three.

A lane whose chunk closes re-reads the character that did not fit as the first of its next chunk, falling a step behind, so the lanes stay within a few characters of each other. While they share a character, nearly always, a step is the scalar loop's class lookup and branches over vector adds and mins, about one scalar step. Around a chunk end the lanes step separately, with the class and byte cost per lane in scalar code. Closing a chunk is scalar too, but happens a dozen times per lane in thousands of steps.

The first batch is the walk near the floor: eight budgets 3 to 31 bits above the floor, or 5 to 47 under UTF-8, because there a three-byte character is 24 bits that cannot be cut and balanced budgets reach 46 bits above the floor. The cheapest budget that holds the bound's count decides the count, the versions of its band and the ceiling, and its failed neighbour becomes the floor. A second batch takes every budget left between them, so a plan is the whole text's plan and two batches, on one-byte and UTF-8 content alike.

Runs of U+FEFF inside the text are an exception the floor does not see. A cut kept off a run moves the run and the character ahead of it to the next chunk, which can put the answer above every budget of the first batch, and the search then fell back to the walk at the capacity and to scalar probes. Runs of marks after forty digits (15,000 characters) took 2.2 to 4.5 times as long to plan as the same text with an ordinary character in their place, depending on level and range.

When every budget of the first batch fails, one more batch starts from the highest budget and spreads across the most such a cut can leave unused. Whether it is taken depends on the ceiling above it:

- Below a ceiling known to hold the count, as when the bracket is opened, it is taken whenever two of its budgets fit under the ceiling.
- Below the capacity the count is asked at, which may not hold the count, the answer can be past the ceiling, making the batch a walk the fallback repeats. So it is taken only when the ceiling leaves it at least half its budgets. Taking it regardless cost up to a fifth more planning for a set whose count was one more than the bound's.

Planning time, as a multiple of the unmarked twin's, changed as follows (versions 1 to 40 unless named):

- six marks at level L: 2.4 → 1.5 times.
- twenty at Q: 4.5 → 2.5.
- ten at H: 3.2 → 2.0.
- four at M (versions 1 to 33): 2.2 → 1.9.

Twenty marks at L stays at 2.8, the gate's cost: the capacity leaves its second batch two budgets, one of which would decide the count, and taking it regardless would plan with no scalar walk instead of three. Whether a batch cut that short decides the count is not known before it is walked, and the gate favours sets whose count is one more than the bound's. Spreading the first batch by the run instead cost a third more on texts whose answer stays near the floor (four and ten marks at L), which the retry leaves unchanged. Text without marks never reaches the retry.

A byte order mark from `Utf8Bom` keeps the scalar walks, since its chunk is priced by another rule. So does content whose chunks average under the backend's threshold (128 characters for the 256-bit path, 20 for NEON), where chunks close so often, a step or two apart, that the lanes are rarely together. With lanes, 78-character chunks took half as long again, and 267-character chunks were nearly twice as fast.

A surrogate pair needs no stepping rule of its own: it is priced whole on its first half, so a budget it breaks is broken on both halves, and the chunk ends before the pair either way. A mutation that dropped the rule and changed no plan showed this.

The scalar probes run on the netstandard builds, on targets without accelerated 128-bit vectors, and on content whose chunks average under the backend's threshold: 128 characters for the 256-bit path, 20 for NEON, 40 for portable vectors on x64 and 80 on WebAssembly. NEON keeps native vectors in registers and tests whether any budget overflowed before extracting lane bits. Wrapping the vector state in another struct caused costly stack traffic. NEON's shorter threshold follows measurements around the old boundary and on small symbols: the compact state wins below 128 characters, while very short chunks still favor scalar probes. `StructuredAppendNeonParityTest` checks saturation, long text offsets and the budget representation boundary against the scalar walk.

`StructuredAppendLaneWalkTest` checks the lanes against the scalar walk, lane by lane, covering:

- walks from the start of the text, and resumed walks.
- budgets one, four and thirteen bits apart.
- four versions (9, 26, 27 and 40: below the 9/10 edge, both sides of the 26/27 edge, and the largest).
- eight lanes, and two.
- every charset.
- pairs, lone surrogates and U+FEFF inside the text (one on every line, after digits, after a pair, at the head).

It also requires the same plan with and without the lanes.

These test sets were sized against faults planted in the lanes, the walk and the prefix search. Several cases caught nothing the rest miss: a mid-band version, five lanes, the middle error-correction levels, version ranges inside or across two bands, a chunk start a few characters in, a second 20,000-character text for the plan, and the long periodic text for the prefix search. In the lane-by-lane walk, only the four-bit spacing caught a tail that closes a chunk at a cost equal to its budget.

The test also checks what the plan does not show:

- where each batch from the floor lands against the scalar search's answer (the second batch below either ceiling, at three budgets and at four, and only once).
- the reach `KeptOffBits` gives that batch.
- the walks one plan takes per call site, which the planner counts per thread for this purpose.


The search does not walk the answer again. The probe that last lowers the ceiling walked at the final budget, so its split is handed back, kept aside because a later failing probe overwrites the caller's buffer. The text is walked a final time only when no walk ran at the final budget, which happens when the capacity itself was the answer. The same test file requires the returned split to be the walk at the returned budget, across pinned versions that put the answer at the top of the bracket.

The symbols of a set are planned together, and only where a plan can differ from one run. After the planner, the program's remaining work is the writer's, since each symbol needs its runs, which costs alone do not give. Each chunk used to go through the program again with predecessors recorded, be walked back and measured, then be measured again by its caller for the set's header: 200 us of a 1.45 ms set on order lines, where the same chunks' single-mode stream takes 7. Three things are now handed over instead.

First, the planner's pass for the longest digit and alphanumeric runs, which decides whether its searches need the program, also tells when every plan is one Byte run. With no digit run reaching three and no alphanumeric run reaching six, a run outside Byte costs strictly more than the bytes it replaces at every version (two digits save 9 bits and five alphanumerics 12, against a header of at least 13), so the minimal plan is unique and tie-breaks play no part. A Byte chunk of such a text is written as its single-mode stream, which is that plan's stream, without running the program. The digit threshold is one below the planner's: three digits tie with Byte, and the program gives a tie to the split, so drawing the line one digit later would change what is emitted.

Second, the plan builder hands its measurement to its caller.

Third, up to eight symbols' plans are built in one pass. The program is a serial recurrence, so one chunk cannot be vectorised, but a set's chunks are independent, so each gets a vector lane at its own character:

- eight at once with accelerated 256-bit vectors.
- groups of four 32-bit lanes with ARM64 NEON.
- on every other 128-bit target, the same groups on portable vectors, which cut an Optimal set's time by 1 to 3 % (WebAssembly AOT unchanged).

Carrying each state with its predecessor as a key makes this a vector step rather than fifteen compares and blends. The key is the cost shifted up three bits over the state's number, so its minimum is the minimum cost and, among equal costs, the lowest state: the tie-break every ordered chain had, since each listed its candidates in state order. The predecessor is the low bits of a minimum the cost-only loop takes anyway. In scalar code the keyed body is only as fast as the ordered chains, and it replaced them so that the lanes and the single chunk share one arithmetic.

Only symbols that need a plan get a lane, in order, eight per pass. A digit chunk is never planned, and as a lane it would be the longest and keep the others waiting. Lanes end at different characters, so the loop runs to the nearest end, takes the results of the lanes ending there and points them at the longest chunk to keep them reading text that exists. Under UTF-8 a character's byte count is two compares on the vector of characters, asked lane by lane only on a step holding a surrogate.

NEON keeps 32-bit keys, because the cost times eight cannot fit the budget walker's 16-bit representation. Groups with too little useful work relative to their longest chunk use scalar planning, so a vector pass is not spent mostly on padding. The parent layout and tie-break are shared, so plans and emitted modules are identical across backends. `ModeSegmenterLaneParityTest` compares costs, final states and reconstructed runs against an independent all-states reference, including every UTF-16 classification value, uneven groups, surrogate boundaries and U+FEFF.

None of the three changes what is written, so the writer counts, per thread, the chunks it plans alone and the passes that plan chunks together. `StructuredAppendStreamTest` checks both counts on prose (neither), twelve symbols of order lines (two passes), three symbols (one pass, the fewest a pass takes) and a label of two (two planned alone).

The program records only what the walk back needs. Three of a character's six predecessors are fixed by the packing groups (Numeric0 comes from Numeric2, Numeric2 from Numeric1, and Alnum0 from Alnum1), so the table holds the other three in two bytes per character instead of seven, and the stack holds a table of 256 characters instead of 73.

The walk back used to read `parents[i * 7 + state]` one character at a time, each load's address waiting for the state the previous load produced, and cost close to half the program that filled the table. A run can begin only at certain places: a Numeric run where the state is Numeric1 (every third character of the run), an Alphanumeric run every second character, and a Byte run at the first entry that does not say Byte. So the walk steps back between such places and reads only whether the run continued there. The address depends on the position alone, the branch is nearly always "continued", and no load waits on another (45 us to 8 on order lines). The first attempt at removing the dependent load, decoding the state from a word of packed fields, was slower than the load.

The lanes interleave the same two bytes by lane, so a step is one store and the walk back reads its lane by stride. The single chunk is one lane of that shape, because a plain loop over a Byte run is within noise of a vectorised search everywhere except prose, and the planner's verdict removes prose.

A search that cannot win still costs, so each is gated: every walk is asked "at most this many?" and stops as soon as the answer is no. At a version far below the one the set needs, an unbounded walk splits the text into thousands of chunks and pays a binary search for each, which on the open version range was most of the feature's cost.

A lower bound on any split's cost is priced once: every character at the cheapest rate any mode gives it, plus the headers every symbol pays at the narrowest widths of any version. It rules out versions whose capacity times the count cannot reach it, so the version scan walks only candidates that could hold the count, and the budget search starts at the bound's average share rather than at one bit. The bound only rejects. It is additive over chunks because a split never cuts a surrogate pair, and `StructuredAppendPlannerTest` requires it never to refuse a count the walk reaches, at any version and level.

Under `QRSegmentation.Optimal` a walk is a pass over the text, so a version the rate bound admits is checked again before it is walked, against a tighter floor: the whole text's minimal plan at that version's count indicator widths, since a split's plans concatenate into one plan for the whole text at the same widths, and every chunk also pays its headers. The rate bound prices every digit as if in a full Numeric group, so on digit-and-word content it admitted the eight versions just below the answer and walked each nearly to the end of the text, and the planned floor rejects them. It is computed once per count indicator band and shared with the budget bracket, which needs the same number. The same test file requires it never to refuse a count the walk reaches, and checks with the walk alone that every version below the chosen one fails.

`QRCodeStructuredAppendEncode` measures the split against the symbols it produces, so its Ratio column is the planning overhead.

The charset is decided once and the parity is one XOR. The whole text is analysed once, and its charset is forced on every chunk and declared in every symbol, so even a pure-ASCII chunk of a UTF-8 set carries the UTF-8 ECI. Every symbol's parity is the XOR of the whole text's bytes in that charset, computed once before splitting. A per-chunk computation can diverge when a chunk would have chosen another charset on its own, and a reader cannot tell which bytes an encoder XORed, so the value must depend on the input and the charset alone.

When `Utf8Bom` asks for a byte order mark and the first chunk is in Byte mode, the mark goes into the first symbol only and counts in the parity as a prefix of the data, as in the single-symbol path. `BoostEccLevel` raises the whole set to the highest level at which every chunk still fits the shared version, never one symbol at a time, so a set never has mixed levels. `MaskPattern` applies to every symbol. `Segmentation` and `QuietZoneSize` apply per symbol.

A Kanji-eligible text can be a Kanji set. The whole text is analysed as `Create` analyses it, so with `AllowKanji` a text the library would write in Kanji mode can be a set in Kanji mode ([When Kanji mode is written](qrcode-symbologies.md#when-kanji-mode-is-written)). Such a set declares no charset, and no symbol carries an ECI header. Its parity is the XOR of the whole text's Shift_JIS bytes, an ASCII character counting as its own byte, and those bytes depend on the text, not its plan, so the parity is still one XOR before splitting. For the CodeGlyphX fixture's text it is 176, the value CodeGlyphX writes, where a UTF-8 set of the same text carries 6.

- Which set is written depends on the segmentation and the text. Under `Single` a chunk must be one segment, so the set is a Kanji set only when every character has a cell, each chunk being one Kanji segment. Under `Optimal` each chunk of a Kanji set is its Kanji plan, with no UTF-8 fallback, since the set carries one charset.

  A text whose every character has a cell is a Kanji set under both segmentations, as `Create` writes it in Kanji mode under both. Each character costs 13 bits against 16 or 24, with no ECI header, so any split of its UTF-8 set also splits its Kanji set, and the Kanji set is never larger.

  A text containing ASCII is compared with its UTF-8 set, as `Create` compares a plan with the single stream, and becomes a Kanji set only when that needs fewer symbols, or as many at a lower version. Its Kanji runs pay a header wherever a character with a cell meets ASCII, and on interleaved text the UTF-8 set is smaller.

  Each set has a floor: the fewest largest-version symbols its characters could fill at their cheapest rate. The Kanji set's floor also counts a run for every stretch of cells and every stretch of ASCII, since no run crosses between them (`StructuredAppendPlanner.CanHoldKanji`). The set with the lower floor is planned first, and the other only where its floor still lets it win: with fewer symbols, or as many at a lower version (for the Kanji set) or at a version no larger (for the UTF-8 set). When the Kanji set is skipped, its floor at one symbol, with the set header's bits given back, still tells whether `Create` could hold the text in one symbol. Planning every Kanji set and comparing had cost interleaved text half again its UTF-8 set's time, for a Kanji set that lost.
- The cost model keeps the three searches and changes the chunk's price and the bounds gating them. A chunk of one Kanji segment costs 13 bits per character in closed form, and a Kanji plan's chunk end is one forward pass of the eighth-state program (`ModeSegmenter.LongestPrefixWithinBudgetKanji`). The lower bound prices a character with a cell at 13 bits, and the floor is the whole text's Kanji plan. The UTF-8 walk's single-mode shortcuts (a mode that changes at most twice along the text, and a character outside the alphanumeric alphabet that only extends a Byte run) do not hold for a Kanji set, whose chunks can hold both kinds of character, so a Kanji set does not take them.
- The lanes carry the seven-state program, without the eighth state, so a Kanji set's walks and plans run in scalar code. The UTF-8 set it is compared with is planned as before, lanes included. The lanes were to gain the eighth state only if the scalar path left a Kanji set slower than its UTF-8 set, and it does not: for an every-cell text under `Single`, planning is 2 % of the Kanji set's time, while the UTF-8 set's planning with lanes is 8 % of its own. The Kanji set also needs fewer symbols: 7 against 12 for 1,000 cells at versions up to 10-L (29.5 µs against 44.2). On 2026-10-01 it was decided that the lanes stay seven-state.
- The one-symbol question uses `Create`'s analysis, so a text that fits one symbol as Kanji or as a Kanji plan, but not as UTF-8, comes out as `Create`'s symbol. Before sets supported Kanji, it came out as a UTF-8 set of two or more symbols.

A U+FEFF inside the text never opens a symbol or a Byte run. The byte-segment decoder takes a U+FEFF at the start of a segment for a byte order mark and drops it. A set meets this in two places, and rules of the walk handle both, rather than checks after it.

A chunk is its own symbol, so a cut just before a mark would open the next symbol with the mark and lose it under either segmentation. Every walk therefore moves such a cut back past the character ahead of the mark: a surrogate pair moves whole, and the cut keeps moving back through a run of marks. The chunk stays within its budget, and the chunk end still never moves back as the budget grows. A run of marks longer than a symbol holds cannot be kept off a symbol's head, and such a text is refused like any text sixteen symbols do not hold.

Inside a chunk, the segmentation program opens no Byte run at a mark (see "Plans the byte-segment decoder would misread are never built"), so the plan a chunk is sized by is always the plan written. Both rules apply to UTF-8 only. Under a declared ISO-8859-1 the decoder drops nothing, and the mark is written as the Latin-1 writer writes any character it lacks: a replacement byte, or its low byte on the targets that narrow. A mark at the head of the text belongs to the first symbol and is lost on decode, as in a single symbol.

This replaced four earlier forms:

- The plan builder refused a minimal plan that opened a Byte run at the mark and wrote the single-mode stream instead. A chunk sized by the plan was never sized for that stream, so the encode threw, as did the boost and the one-symbol path.
- Pricing such texts as `Single` fixed the throw but made fourteen symbols out of nine.
- Pricing as asked, with a fallback to `Single` only when a chunk's plan was refused, kept most texts whole. But a mark after a run of ten digits is refused every time, and one such mark in forty-eight thousand characters refused a text sixteen symbols hold, or cost a version.
- The cut rule lived in the scalar walk alone. The lanes' split was checked afterwards and, when it opened a symbol with a mark, searched again without lanes. On text with a mark on every line, that second search ran for three texts in ten and made the planner three times as slow.

Each was a check after a search that did not know the rule. With the rule in the recurrence and the chunk close, it costs a compare where a mark is and nothing elsewhere.

At first the rule kept the lanes from stepping together: a cut moved off a mark sets that lane back by more than the one step a close costs the others, so the lanes never shared a character again, and the rest of the walk ran per lane at a seventh of the speed, slower than no lanes. Now the lanes ahead wait, keeping their states, until the one furthest behind is level. That costs a step or two per close, and brought a text with a mark on every line (`marked-40k-any`: 40,000 characters of order lines, versions 1 to 40, level L) from 2.9 ms to 1.6 ms, the time of its unmarked twin.

The wait is needed for correctness on marked text, not only for speed. A lane that moves while the lanes are apart is at the head of its chunk: one character, then the marks its cut was kept off. There, continuing that character's run never costs more than opening another, so that step needs no mark rule, but only while the wait works.

The walk counts the steps it takes apart, and `StructuredAppendLaneWalkTest` bounds that count below and above, by the text's length, since a failed lane keeps closing chunks to the end of the text. A wait that half works changes plans, which the parity tests catch, and a wait that stops changes the count.

`StructuredAppendStreamTest` walks the cut across the mark one character at a time at three versions, with and without a pair ahead of it, and checks:

- the round trip under both segmentations.
- the refusal of a run of marks that no symbol can keep off its head.
- that a marked text has the same symbol count as its unmarked twin (after a line feed, after an alphanumeric, and after a run of digits at the sixteen-symbol limit, where the version must match too).
- the plan that does open a Byte run at the mark under a declared ISO-8859-1.

Besides the marked texts in its list above, `StructuredAppendLaneWalkTest` checks the lanes against the scalar walk with a mark on a lane's last characters.

The parity covers the bytes written, also when a forced charset cannot hold the text. `EciMode.Iso8859_1` forced on text outside Latin-1 is lossy by the caller's choice. The Byte writer writes the transcoder's replacement byte on targets that use the transcoder, and the low byte on targets that narrow. The parity takes the same byte on each target, so it is still the XOR of what the symbols carry.

Each symbol is the stream `Create` would write for its chunk, behind the header: the header, then the ECI header, then the mode segments. The per-symbol pipeline from data codewords on is unchanged, which keeps every existing invariant (padding, error correction, interleaving, masking) for a set.

A refusal advises only what would work. Advice to "widen the version range" at version 40, or to "lower the ECC level" where L is refused too, only leads to a second refusal. So each of the three changes that keep the text's bytes as asked (a wider range, a lower level, `QRSegmentation.Optimal`) is tried by planning the text that way, one-symbol question included. Every single change that works is offered as an alternative, and only when none works is the smallest working combination given. A lower level is named as the highest level that works, since the next one down may not.

The refusal also states two reasons the set's size does not show: a run of U+FEFF that no symbol holds together with the character ahead of it, and marks whose kept-off cuts cost the text its sixteen symbols, which the same text shows by fitting with an ordinary character of the mark's width in place of the marks. The refusal's first sentence names the text's mode, charset and length, as `Create`'s refusal does. The trials run on the throw path only. Advising the caller to drop a byte order mark or a declared charset was rejected, because both change the bytes the caller asked for.

---

## Pipeline

### 1. Validate the matrix request

Every entry point (`Create`, `CreateStructuredAppend`, `TryGetRequiredBufferSize`) rejects, before it reads the content:

- requested versions outside `1..40` (except `-1`, meaning automatic).
- an undefined ECC level.
- an `EciMode` other than `Default`, `Iso8859_1` and `Utf8`.
- quiet-zone sizes outside `0..10,000`, the bound Micro QR and rMQR use. The `QRCodeData` constructors apply it too, after refusing a version outside `1..40`.

Errors come in the order the other two symbologies report them: quiet zone, segmentation, ECI (rMQR's place for it; Micro QR has none), ECC level, then whether the content fits. At 10,000 the largest side squared still fits `int`, so the size arithmetic needs no overflow check. The span overload also rejects buffers smaller than the matrix.

### 2. Analyze text and choose mode / ECI

`TextAnalyzer` classifies the payload in one pass over the UTF-16 input:

1. Numeric when every character is `0..9`.
2. Alphanumeric when every character belongs to the 45-character QR alphabet.
3. Byte otherwise.

Empty input is a zero-length Byte segment: the standard defines no empty-data mode, and Byte mode is the least surprising representation.

With `EciMode.Default`, Byte mode picks its character encoding as follows:

| Input | Effective ECI | Header |
|---|---|---|
| ASCII only | Default | none |
| Contains non-ASCII, but every character is in U+0000..U+00FF | ISO-8859-1 | ECI 3 |
| Any character above U+00FF | UTF-8 | ECI 26 |

Numeric and Alphanumeric payloads need no charset conversion, but an explicitly requested non-default ECI is still emitted before their data-mode indicator.

An explicit ECI is a caller constraint: forcing `Iso8859_1` is semantically correct only for text in U+0000..U+00FF, and `Default` upgrades text outside that range to UTF-8 to avoid an incompatible choice.

On supported x86/x64 runtimes, analysis uses AVX2 or SSE2 for character-class checks, and on ARM64 a NEON tier (16 chars per step, with 8-wide vector remainder blocks). A scalar path covers short inputs and other targets.

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

Byte-mode capacity uses the encoded byte count, not the UTF-16 `char` count. A UTF-8 BOM adds three bytes to both capacity selection and the Byte-mode character-count indicator.

Only UTF-8 is counted. Latin-1 is one byte per `char`, out-of-range ones included (the writer narrows each `char`, and the encoder replaces each one it cannot represent with one byte of its own), and a surrogate pair counts as its two code units, not the one scalar value it spells. So a charset forced over content it cannot represent still gives a well-formed symbol, and the mojibake the caller asked for reads as mojibake, not as a stream a reader takes apart wrongly. The classification pass has just decided which of the two charsets applies and hands that answer over, so the count does not rescan the text.

The version calculation does not reserve the four terminator bits: the terminator may shrink to the remaining capacity, down to zero bits for an exact fit. If no version holds the header and payload bits, generation fails instead of truncating.

Each candidate's capacity is an index into the version × level ECC table, not a search of it. The level was validated on entry (step 1), so the lookup's own range check only keeps an internal caller from reading another level's entry.

A version pinned by `QRCodeGeneratorOptions.Version` bypasses automatic selection. It is meant for callers that need a fixed symbol size and know the payload fits.

#### Version ranges

`QRCodeGeneratorOptions.Version` is a `QRVersionRange`, and the scan runs over `[Min, Max]` instead of 1 to 40. A pinned version is the degenerate `Exactly(n)` case, so one concept replaces a requested version and a range that could contradict each other. Both bounds are validated when the range is constructed, before any generator is called, and both are inclusive, which is why this is a domain type and not C#'s `..`, whose exclusive end would make `1..40` mean 1 through 39.

Two behaviours came with the range and are now unconditional. Both differed from the 1.1.1 `requestedVersion` parameter, removed in 2.0.0:

- A range narrower than 1-40 is checked for fit. `Exactly(n)` reports content that does not fit version n as `false` from `TryGetRequiredBufferSize` (or an `ArgumentException` from `Create`). The removed parameter passed the version straight to the encoder, which failed deep inside with `ArgumentOutOfRangeException (Parameter 'length')` from a span slice.
- Sizing honours the version. The released `GetRequiredBufferSize` had no `requestedVersion` parameter, so ignoring `Version` would have been a silent trap. `TryGetRequiredBufferSize` reports the version the range resolves to, as Micro QR and rMQR already do.

`QRVersionRange.Any` goes straight to the automatic path before any range resolution, so the default costs nothing extra. A constrained range or an ECC boost analyzes the text once too: the analysis that resolves the version and level, which a boost needs before it can raise the level, is the one the symbol is written from. It is the analysis the automatic path makes (Kanji mode only where no byte order mark asks for UTF-8), so the resolution and the stream cannot describe two different encodings. Mixed-mode encodes that end up writing the single-mode stream write it from their own analysis the same way, since that analysis differs from the single-mode one only in whether a Kanji plan is possible, which the single-mode stream does not read.

The scan does not assume the fit predicate is monotone in the version, although it is. It plausibly might not be, since the character-count indicator widens at versions 10 and 27 and a larger version then costs more header bits, and scanning `[Min, Max]` is correct either way. `VersionRangeTest.StandardQr_FitsIsMonotoneInVersion` sweeps 3 modes × 4 ECC levels × 3 ECI modes × 58 lengths × 40 versions, so monotonicity is a checked fact the code does not rely on.

#### ECC boost

`QRCodeGeneratorOptions.BoostEccLevel` treats the requested ECC level as a minimum: the version is chosen for that level as above, then the level is raised while the next level still fits that version. With the version fixed first, boosting never grows the symbol and turns padding the symbol would carry anyway into error-correction capacity. It is mainly for symbols with an icon overlay, whose spare capacity absorbs the covered modules.

- The boost is off by default. A raised level rewrites the format information and can change the winning mask, so a default boost would silently change every existing symbol, while existing tests, golden pixels and playground permalinks all assume the requested level is the emitted level.
- Sizing ignores the flag, as the API documents: the buffer size depends only on the version and the quiet zone, and the boost cannot change the version, so `TryGetRequiredBufferSize` gives the same answer either way.
- The error contract is unchanged. Content that fits no version in the range fails with the same exception and message as without the boost: `InvalidOperationException` for the unconstrained overflow (the released contract) and `ArgumentException` for a constrained range. The boost must not reclassify an error.
- The boost is Standard QR only. Micro QR ties its legal levels to the version (M1 has none, only M4 offers Q), so a boost there would interact with version selection instead of following it. rMQR has a single M→H step. Either can adopt the same contract later.

`EccBoostTest` checks the headroom classes (boost to H, stop at an intermediate level, no headroom, already at H), version invariance, sizing indifference and error parity.

#### Mixed-mode segmentation

`QRSegmentation.Optimal` splits the content into the Numeric / Alphanumeric / Byte runs of minimal total bit cost for a candidate version and fits the version to that cost instead of the single-mode cost. `QRSegmentation.Single` (the default) keeps one run in one mode.

In a single segment, a mixed payload pays the content's mode for every character: a URL prefix followed by a long numeric identifier is all Byte, so the digits cost 8 bits each instead of 3⅓. Splitting the digits off routinely makes the symbol a version or more smaller (`https://example.com/item?id=` + 30 digits: version 4-M as one Byte run, version 3-M split).

It is opt-in because changing the default would change the emitted bit stream, and so the rendered symbol, for existing callers. A single-mode fit caps the scan, so only strictly smaller versions are tried: a plan is emitted only when it lowers the version, and otherwise the single-mode stream is emitted byte for byte. The end-to-end tests assert both properties for every corpus entry.

The scan needs almost no bounding machinery: the character-count indicator widths, and so the optimal cost, are constant within each of the three version bands (1–9 / 10–26 / 27–40). The scan computes the cost at most once per band (three O(n) cost runs at worst, with no reconstruction table) and compares it with each candidate capacity.

rMQR needed a trivial bound, a floor and a re-priced ceiling, because its 32 versions carry 13 distinct width triples across a strategy-ordered ranking. A totally ordered version set with banded widths needs neither floor nor ceiling, a lesson kept next to the rMQR one so the bounds are not ported by reflex.

Only the trivial bound carried over: one O(n) pass that prices each character at the cheapest rate any mode could give it. Without it, content no split can shrink still paid for a band cost run. On 120 single-mode characters, the Optimal arm went from 1.8x the Single encode to roughly parity, and the winning shapes were untouched. Its blind spot is rMQR's: finely alternating content clears the bound and pays for planning that gains nothing, because only the dynamic program itself can see that switching modes on every character never pays.

When no single mode fits, there is no ceiling and the scan runs to the end of the window, the one place where `Optimal` accepts input `Single` rejects. For example, 1,000 lowercase letters followed by 4,500 digits is 5,500 Byte-mode characters, far over the 2,953 that version 40-L holds, but well inside its 23,648 bits once the digits are split off. The path throws only when a mixed plan fails too, and then throws the single-mode path's exception type for each constraint shape, so turning segmentation on cannot reclassify an error.

All-Numeric content cannot benefit and skips planning: no mode prices a digit below Numeric, and every extra run adds a header, so one run is provably optimal. The rule is one predicate, not a repeated condition, because every caller that gets a no skips a whole cost run and would emit a different stream if the predicate were wrong. The version scan, the Structured Append chunk cost (which the ECC boost asks again per level) and the per-symbol writer all ask it. `QRSegmentPlannerUnitTest` checks it against the program at every version band, for both charsets, and at lengths that end mid packing group.

The optimum is exact: a run's cost per character is not constant (Numeric packs 3 digits into 10 bits, Alphanumeric 2 characters into 11), so the dynamic program carries the packing-group remainder in its state instead of rounding to a per-character average. The state layout and transitions are in `ModeSegmenter`, shared with the rMQR and Micro QR planners. The symbologies differ only in header widths, passed as parameters, so one implementation keeps the cost models (UTF-8 surrogate rules included) from drifting apart. Micro QR also passes per-version mode availability (M1 is Numeric-only, M2 has no Byte mode), which disables the missing transitions. `QRSegmentPlannerUnitTest` checks the program against an independent exhaustive search over mode assignments on short content across the bands and charsets, and the rMQR suite checks the same code against its own independent oracle across all 32 versions.

Content longer than the largest character count any version holds in any mode (7,089, Numeric at 40-L, an exact fit) is rejected before any cost run, with a margin of 4 bits. The derivation sits next to the constant in `QRSegmentPlanner`, so a capacity table change re-derives the constant instead of nudging it. The plan buffer is stack-allocated up to 64 characters and pooled at the text's length above. A plan cannot hold more runs than the content has characters, so the pooled path never runs out of space. The reconstructed plan is re-costed from the byte counts the encoder will emit and rejected if the two disagree, because the bit-stream writers store without per-flush bounds checks.

The BOM is a stream-level prefix that a split would move into the middle of the decoded text, so `Utf8Bom` falls back to the single-mode stream exactly when a BOM would be written: in a UTF-8 Byte-mode stream. Content whose single mode is Numeric or Alphanumeric never carries a BOM (even under an explicitly requested UTF-8 charset) and still splits. The gate's first version suppressed those splits too, and code review caught it costing a full version for nothing. The other options compose as follows:

- A version range narrows the scan window. A pinned version that only a mixed plan fits succeeds where `Single` throws.
- ECC boost runs after the plan is fixed and compares the planned stream's exact bit count with the higher level's capacity, which keeps the version-invariance contract.
- A pinned mask applies at the matrix stage, independently of the plan.
- Argument validation keeps the quiet-zone-first precedence of the other surfaces, and an undefined segmentation value reports the same `segmentation` parameter name on the generators and the builder.

Plans the byte-segment decoder would misread are never built. The shared byte-segment decoder consumes a leading EF BB BF in every segment without an explicit ISO-8859-1 declaration, even behind an explicit UTF-8 ECI, so a plan that opened a Byte run at a mid-content U+FEFF would decode with that character silently dropped.

Under UTF-8 the segmentation program has no such transition (`ModeSegmenter.ByteOrderMark`): at a U+FEFF past the first character a Byte run can only continue, so the run holding the mark opens a character early to keep the mark inside, and the optimum is the cheapest safe plan. Every text still has a plan, its single Byte run, so the rule refuses nothing, and a text whose unconstrained optimum was safe keeps that plan bit for bit. The first character is exempt because the single-mode stream starts with the same bytes and loses the mark the same way.

All three planners share the rule through the shared program's cost-only loop, loop with parents, prefix walk and both vector walks. These tests check it:

- `ModeSegmenterPlanParityTest` checks the three scalar loops against an all-states reference that carries the same rule.
- `StructuredAppendWriterPlanTest` checks the writer's lane per chunk against the scalar plan.
- `StructuredAppendLaneWalkTest` checks the budget search's lanes against the scalar walk.
- Each symbology's segmentation test checks the round trip and the smaller symbol.

`ModeSegmenter.HasBomRelocatedToARunStart` and the Standard QR plan pricing keep the old refusal as the answer to a model that disagreed.

Micro QR, which has no ECI to pin a charset, also rejects plans containing a non-ASCII Latin-1 Byte run whose narrowed bytes the decoder's unspecified-charset resolution would read as UTF-8 (`SegmentDecoders.ResolvesToUtf8WhenUnspecified`, kept beside the resolution it mirrors). Isolating such a run from the invalid neighbours that disambiguate it would decode it as different text. There only the minimal-bit plan is checked, so such content reports "does not fit" when no single mode holds it.

A mixed-mode plan is only as good as the decode it produces. Cost optimality had to be constrained by the decoder's per-segment charset heuristics, and adversarial review caught this by reproduction, not inspection.

The U+FEFF constraint was first a rejection of the minimal plan, with a fallback to the single-mode stream. Its known gap was that it never searched a slightly costlier safe split, so 3,000 digits + U+FEFF + 10 letters reported "does not fit". Moving the rule into the program waited until the input class mattered, which it did for Structured Append: a set sizes sixteen chunks by plans, and a refused plan there is a symbol written as a stream its chunk was never sized for.

A plan has one ECI prefix, ahead of the first run, because a decoder carries the declared charset across the following runs. Its 12 bits are part of the cost the version scan compares.

A text whose every character has a Kanji cell is one Kanji run under both segmentations, and the planner returns before any cost run: no character fits another mode more cheaply, and Byte would need an ECI header ([When Kanji mode is written](qrcode-symbologies.md#when-kanji-mode-is-written)).

A Kanji-eligible text containing ASCII has two plans under `Optimal`. The analysis marks it `KanjiPlannable` on the `Optimal` paths only, so `Single` does not pay for reading past its first ASCII character.

- The Kanji plan comes from an eighth state, Kanji, in its own program (`ModeSegmenter.ComputeCostsKanji`): an ASCII character reaches Numeric, Alphanumeric and Byte at one byte, and a character with a cell reaches only Kanji. The stream carries no ECI header, since its Byte runs hold only ASCII. After any character either Kanji or the other states are reachable, never both, so Kanji's predecessor is stored in the byte holding Byte's predecessor at an ASCII position: the table stays two bytes per character, and the walk back steps through a Kanji run as through a Byte run. It is a separate program, not a flag on the seven-state loops, so every ineligible text runs the program it ran before.
- The UTF-8 plan is the one `Optimal` wrote before.
- The scan goes up from the smallest version, below the single-mode (UTF-8) fit, and takes the first version a plan holds, preferring the Kanji plan where both do. Each program is priced at most once per band, behind its own screen. The Kanji run keeps its predecessors, and the plan is walked back from the run that accepted it: the band's run, at the widths of whichever version of the band is taken, so rerunning the program for the build would compute the same table. Writing the table costs the scan less than the run the build is spared.
- The Kanji screen prices a character with a cell at 13 bits (78 sixths), adds no ECI header, and adds a header for every stretch of characters with a cell and every stretch of ASCII, since no run of the plan crosses between them. Pricing the text at its UTF-8 bytes behind the header, as the seven-state screen does, would skip the version where a Kanji plan of kana-heavy text fits (「こんにちは世界、QRコードの分割テストです。」×3 at M). The header per stretch makes the screen useful on interleaved text: with one header in all, 「日本7777」×10 cleared version 4-M's screen and paid for a cost run whose plan could not fit. The narrowest count indicator stays 8 bits, since Kanji's is 8 at versions 1–9, as Byte's is. The same pass gives the UTF-8 screen.
- The UTF-8 plan still competes because interleaved kanji and digits (「1日」×20 then 60 digits) pay a header per run as Kanji runs, and one UTF-8 Byte run with the digits split off is a version smaller. Without that plan in the scan, `Optimal` would have grown a symbol it wrote before.

`ModeSegmenterKanjiParityTest` checks both loops, the walk back and the run counts against an independent eight-state reference. `KanjiOptimalTest` checks the output against a reference scan: a Kanji plan is written out from the standard with the mask pinned, and a UTF-8 plan is compared with the same text with UTF-8 asked for. `KanjiPlanBoundsTest` checks the screen against the optimum at every band. A Kanji Structured Append set runs the eighth state in scalar walks and plans. The lanes, used only by sets, keep the seven states and serve UTF-8 sets (see "The lanes" under [Structured Append](#structured-append)).

Where its UTF-8 twin plans nothing, a Kanji plan does more work: the analysis reads past ASCII, the scan prices the eighth state, and a plan is built and written run by run. Its target, "no slower than the same text with UTF-8 asked for", is not met: on 「日本7777」×10 at M, the Kanji plan (version 5) takes about 5-10 % longer than the UTF-8 stream (version 6).

The gap is wider on small symbols: Micro QR's 「日本語12345」 at L takes 10-35 % longer as a Kanji plan at M3 than as its UTF-8 single stream at M4, and rMQR's 「日本7777」×10 takes 6-25 % longer at R17x77 than at R13x139. The spread is between measurements. The rMQR record explains why its build runs the program itself.

Three changes narrowed the gap:

- The analysis only asks whether a character has a cell, a membership test on the reverse table without the rank and the value, so the writer's lookup of each value is the one lookup per character.
- The plan is walked back from the scan's own cost run where that run accepted it (on Standard QR the band's run, as above), so the build does not run the program again.
- The counts and the measure are filled in one pass.

The gap was accepted with the user on 2026-10-01, because Kanji mode is written only on request, and a caller who asks gets the smaller symbol.

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
| Numeric | 3 digits → 10 bits, final 2 → 7 bits, final 1 → 4 bits |
| Alphanumeric | 2 values → `first * 45 + second` in 11 bits, final 1 → 6 bits |
| Byte | 8 bits per encoded byte |

Byte mode uses ISO-8859-1 narrowing or UTF-8 encoding. Temporary charset buffers use `stackalloc` up to 256 bytes and `ArrayPool<byte>` above. `BitWriter` stages bits in a 64-bit accumulator and bulk-writes big-endian words. Byte-mode data is copied eight bytes at a time where possible.

The Numeric and Alphanumeric payloads are written several fields an append, not a field an append. Every build has a portable writer that puts fifteen digits, or four pairs, into one 50- or 44-bit append (`BitWriter.WriteWide`). On x64 a vector step writes twelve digits (SSSE3, for a run of 40 digits or more) or sixteen characters (SSE4.1): the values, the pairs and the fields come from nibble tables and multiply-adds. WebAssembly runs the Alphanumeric step on its own SIMD and the portable Numeric writer. ARM64 runs NEON steps of sixteen characters, whose values and membership come from one table lookup over 64 bytes, and of fifteen digits, the portable writer's append. They start from eight characters and 40 digits on .NET 10, and from 32 characters and 160 digits on .NET 8, whose code keeps the writer's fields in memory. A run shorter than the first step, under sixteen digits or eight characters, is written in the writer's caller.

The analysis or the plan has already found every character in its mode's alphabet, so a writer checks a whole step at once rather than each character. A run with a character outside the alphanumeric alphabet still throws `ArgumentException` at that character, after the fields of the pairs ahead of it, as the per-character writer did. The Numeric writer, as before, does not check its digits.

### 5. Generate Reed-Solomon error correction

The selected version and ECC level identify:

- total data codewords.
- ECC codewords per block.
- Group 1 and Group 2 block counts.
- data codewords per block in each group.

Data codewords are partitioned by that table, and `EccBinaryEncoder` computes each block's Reed-Solomon remainder over GF(256), with primitive polynomial `0x11D` and generator roots `alpha^0..alpha^(n-1)`.

A block's remainder is one serial chain, each step's factors read from what the step before left. The blocks of a group are independent and share their length and generator, so on x86 a group of two or more blocks runs their chains side by side in one loop (`CalculateECCBlocks`): GFNI four and two blocks at a time on .NET 10, above 16 ECC codewords only with 256-bit GFNI and AVX2, and SSSE3 two at a time up to 16 ECC codewords. Every other block, and every block on the other builds, goes through the single-block kernels below. rMQR assembles its codewords through the same entry.

The public dispatch picks the fastest available kernel, with byte-identical results:

- GFNI / SSSE3 on supported x86/x64 targets.
- AdvSimd on ARM64.
- the AdvSimd kernel on WebAssembly, its swizzle replacing the table lookup, for blocks of 90 data bytes × ECC codewords or more (2026-10-01). It runs at 0.11 to 0.13 of the scalar kernel on a version 40-L block, and 0.54 to 0.64 on Micro QR M4-L. Under that product, the interpreter's setup (0.35 µs before the first step) costs more than the scalar division.
- a cached log-domain scalar implementation elsewhere.

Every optimized kernel is parity-tested against a naive polynomial-division reference.

### 6. Interleave the final message

`BinaryInterleaver` emits:

1. data codeword 0 from every block, then data codeword 1 from every block, and so on.
2. the extra final data row from the longer Group 2 blocks, when present.
3. ECC codeword 0 from every block, then ECC codeword 1, and so on.
4. zero remainder bits for the selected version.

Output is written sequentially, with strided source reads. A one-block symbol uses an identity fast path. Remainder-bit storage is cleared explicitly so uninitialized stack or pooled memory cannot affect the matrix.

### 7. Place function patterns and data

The encoder places or reserves the following (the placer writes the whole byte-per-module core, so no zeroing is needed):

- three 7×7 finder patterns.
- one-module separators.
- alignment patterns for version 2+.
- horizontal and vertical timing patterns.
- the fixed dark module.
- both format-information areas.
- both version-information areas for version 7+.

Reserved modules are a compact bit mask. The reference `PlaceFunctionModulesReference` painters build the painted function modules and the mask once per version, both are cached (`ModulePlacer.PlacementLayout`), and the encoder copies them for each symbol. The decoder reads the same cached mask to tell function modules from data modules, so both directions are structurally identical.

The interleaved bits are then placed MSB-first in the standard two-column zigzag from bottom-right to top-left, skipping column 6 and every reserved module. The reference walk keeps up to 64 pending stream bits in a register and handles both modules of a strip row together. Production placement uses the cached walk, which holds the core index per stream bit and records rows where both strip modules are free as runs. The stream is expanded to one byte per bit, each run row is then one 16-bit store, and everything else is an index-table scatter. This placement is parity-tested against the reference walk for every version.

On AVX2, with the mask chosen automatically, versions 12-40 skip the byte placement. The interleaved stream goes straight into the transposed scorer's column words (step 8), over a per-version column template that already holds the function modules and the version information, and the winning candidate is written to the matrix whole, all but the format information. The zigzag fills a pair of columns a row at a time, so a run of rows is the two columns' bits interleaved in the stream, already in column order; only the modules beside function patterns take their bits one at a time. The tables take 3,944 to 13,352 bytes a version, built on first use. Versions 1-11, the other builds and a pinned mask keep the byte placement: at versions 1 and 6 the stream form was slower (Performance). A parity test holds the chosen pattern and the written symbol to the byte placement and the transposed selection at every version 12-40 and level, on random, uniform, alternating, short and overlong streams written into a dirty buffer.

Bit expansion uses AVX2 or SSSE3 on x86, and ARM64 NEON on .NET 8+, when supported. Every backend keeps MSB-first order and writes eight 0/1 bytes per input byte. Short streams and final vector blocks stay within the logical spans and need no scratch-buffer slack. The ARM64 path also handles short vectors, and may align a large output stream with a scalar prefix. That alignment is only a performance hint, so an unaligned buffer or a moving GC does not change the result. The portable fallback uses arithmetic for short streams and a lazily initialized 2 KiB lookup table for longer ones, with no per-call allocation. Direct expansion parity tests cover every byte value, the vector and alignment thresholds, offset buffers and dirty output guards, besides the placement tests for all 40 versions.

### 8. Evaluate all eight masks

The encoder tries every Standard QR mask pattern and picks the lowest ISO/IEC 18004 penalty score:

1. long same-color runs.
2. 2×2 same-color blocks.
3. finder-like `1:1:3:1:1` patterns with the required light margin.
4. deviation from 50% dark modules.

Each candidate is scored as the final symbol will appear:

- the mask is applied only to data modules.
- candidate-specific format bits are inserted before scoring.
- version bits are included for version 7+.

Format modules take part in the visual penalty rules, so including them can change which mask wins. Ties are deterministic: the lower mask index wins, because candidates are visited in order and only a strictly lower score replaces the current best.

Masking and scoring work on packed rows, not byte-per-module loops:

- versions 1–11 fit each row in one `ulong`.
- versions 12–40 need two `ulong` values a row up to version 27 and three from version 28.

The eight formulas are precomputed as 12-row periodic templates. XOR, shifts and popcount implement masking and all four penalty rules with the same result as the reference. The finder-like rule is built from two shared terms, four light modules and the seven-module core, rather than an 11-module match per window. On AVX2 the tier for versions 1-11 scores four candidates per vector (one pattern per lane), with per-version tables of pre-masked templates and format-bit overlays. For versions 12-40, whose rows span two or three words, the tier holds each candidate twice, as row words and as column words (the transpose), so a row-direction rule is a column-direction rule on the transpose and every rule runs between whole words of neighbouring rows: only the 2x2 rule shifts, by one bit. The data is packed and transposed once per symbol (with automatic selection on AVX2 the column words come from the stream, step 7, and the row words are their transpose), and each candidate is that XOR the pattern's 12-periodic template on the data area, in both orientations. x64 without AVX2 and WebAssembly run both tiers on 128-bit vectors, two candidates per vector for versions 1-11 and two rows or columns per vector for 12-40. ARM64 keeps its two-row tier for 1-11 and runs the 128-bit transposed tier for 12-40, with NEON's popcount and its own 16-module row packing and winner unpack. Builds without a vector tier, the netstandard builds among them, score the row words in scalar code. Parity tests compare every representation with straightforward textbook formulas, on the scores as well as the chosen patterns.

A mask pinned by `QRCodeGeneratorOptions.MaskPattern` (0-7, `null` = automatic) skips the evaluation and is applied as the evaluation applies its winner: each row takes the pattern's packed template row, masked to the row's unblocked modules, and XORs it into the bytes. Any pattern gives a legal symbol, since the specification only recommends the best scorer, so pinning serves byte-exact reproduction of symbols produced elsewhere (the decoder reports the pattern in `QRCodeDecodeInfo.MaskPattern`) and testing decoders against all eight patterns. Invalid values are rejected when the option is set, as with `Version`. Micro QR offers the same option over its four patterns (`MicroQRCodeGeneratorOptions.MaskPattern`, with an unrelated numbering). See the Micro QR spec map. rMQR has a single fixed mask, so it has no such option.


### 9. Write format / version information and expose output

After the winning mask is applied:

- BCH(15,5) format information encodes ECC level and mask index, applies the standard format mask, and is written twice.
- BCH(18,6) version information is written twice for versions 7–40.

For `QRCodeData`, the temporary core matrix is packed into the object's one-bit-per-module payload, and the quiet zone stays virtual.

For span output:

- with quiet zone 0, the core pipeline writes directly into the destination.
- with a quiet zone, the contiguous core is built at the start of the destination and moved row by row into its centered window there, the last row first, so no row is overwritten before it moves. Only the margins are cleared, the gap between two rows once the row after it has moved. No buffer is rented and the destination is not cleared as a whole. The netstandard2.0 build keeps the earlier path, a pooled core copied row by row into the cleared destination, since on .NET Framework 4.8, which runs it, the move in place measured slower. netstandard2.1 moves the core in place as .NET 8 and later do, which on .NET 6 and 7 measured no slower end to end (Performance).

The encoder produces a module matrix, not an image. Color, pixels per module, shapes, gradients, icons, PNG/SVG encoding and other presentation concerns belong to `SymbolRenderer` / `QRCodeImageBuilder`.

---

## Why

- One canonical binary pipeline serves string and span inputs, object and span outputs, and all rendering APIs, which share the same mode, ECC, interleaving, placement and masking logic.
- Central Standard QR tables, not duplicated conditionals, supply capacity, block grouping, alignment centers and remainder counts, which keeps the structure correct.
- Encoder and decoder agree by construction: they share, or test in both directions, function-module layout, format generation and block conventions.
- Validation is independent: ZXing decodes generated symbols across modes, ECI choices, ECC levels, boundary capacities and large versions, and the in-process decoder adds all-version round trips and error injection.
- Optimizations change representations, not algorithms, and parity tests against simple reference implementations guard the ECC, interleaving, placement and mask-scoring kernels.
- Output is deterministic: version scan order, block order, interleaving order, mask tie-breaking, zeroed remainder bits and clean destination handling make repeated calls byte-for-byte stable.

---

## Decisions

- One segment per input is the default: it keeps the default path auditable and mode selection one pass, at the cost of non-minimal symbols for mixed-mode payloads. The opt-in `QRSegmentation.Optimal` addresses that and changes the stream only when it lowers the version.
- From 2.0.0, text JIS X 0208 holds is written in Kanji mode on request. Through 1.x all Unicode input went out as UTF-8 Byte mode with ECI 26, at a capacity cost for Japanese, for output stability and, before Kanji decoding shipped, to avoid carrying a Shift_JIS table. The table is now generated, not taken from `System.Text.Encoding.CodePages`, so no dependency is added. With `AllowKanji`, a text whose every character has an encoder cell is one Kanji segment with no ECI header, when the charset is the library's choice and no byte order mark is asked for. The option is off by default because an Android phone's own scanners read no Kanji mode, so default output stays 1.x's. [When Kanji mode is written](qrcode-symbologies.md#when-kanji-mode-is-written) gives the rule and why it is not wider.
- ASCII omits ECI by default, minimizing overhead and maximizing compatibility, while automatic selection declares Latin-1 and wider Unicode explicitly.
- The BOM is explicit and UTF-8-only: `QRCodeGeneratorOptions.Utf8Bom` affects the stream only in Byte mode with an effective UTF-8 ECI.
- The version can be forced for fixed-size applications: a pinned `QRCodeGeneratorOptions.Version` bypasses minimum-fit selection instead of acting as a lower bound.
- The quiet zone is output policy, not core symbol data: core encoding always uses the `21 + 4 * (version - 1)` matrix, and quiet-zone storage varies by output model without changing encoded modules.
- Mask scoring includes format and version information, because scoring a data-only candidate can pick a different winner than scoring the final matrix.
- Structured Append is one method, with the count taken from the version cap. It has no explicit-count overload, no set sizing API (`TryGetRequiredBufferSize` stays single-symbol) and no combine helper. The first two can come in a minor release on request. A combine helper would fix an irreversible reassembly policy (a missing symbol, a duplicate, a parity mismatch), and the four rules on `QRStructuredAppend` are all a caller needs.
- A set of one is never written: its header costs 20 bits and tells a reader nothing. The decoder still accepts one.
- Stack buffers stay zero-initialized: the encoder uses no `[SkipLocalsInit]`. Skipping the zeroing measured 1 to 12 % of a version 1 to 10 encode (2026-10-02) and nothing on larger symbols. Under the memory safety rules C# 15 starts in preview, a `stackalloc` without an initializer in such a member is an unsafe operation, and in an encoder a read before a write would not fail but put stale stack bytes into a symbol that leaves the process. The zeroing's cost is cut instead by allocating less (see Lessons Learned, Performance).
- The parity follows the bytes, so Kanji encoding changes Japanese text's parity: in Kanji mode (with `AllowKanji`) it is the XOR of Shift_JIS bytes, not UTF-8 ones. A set stays consistent, so no reader breaks, but a pinned value of this encoder's parity for Japanese input depends on the option: 「こんにちは世界、QRコードの分割テストです。」×3 carries 176 as a Kanji set and 6 as the UTF-8 set. `StructuredAppendKanjiTest` checks the Kanji sets' parity, and older tests check ASCII, Latin-1 and UTF-8. The decode corpus holds Japanese sets both ways, since the decoder reports what is on the wire.

---

## Lessons Learned

### Capacity and text encoding

- Byte-mode length counts encoded bytes, not UTF-16 characters, which is the central boundary condition for UTF-8 input: `text.Length` would under-size every non-ASCII payload and misplace version transitions.
- The UTF-8 BOM belongs in the Byte character count. Treated as out-of-band metadata, it leaves the declared count three bytes short, and some readers reject the symbol.
- ECI overhead can change the version and the mask: twelve header bits can cross a version boundary, and otherwise still shift every later bit, changing ECC, interleaving, placed data and often the selected mask.
- Exact-capacity inputs need no full terminator: padding must add `min(remaining, 4)` terminator bits, not assume four bits are always available.
- A branch test whose fixture cannot reach the branch asserts nothing. The first test of ECC boost under segmentation used content whose planned stream (350 bits) could never fit the next level's capacity (272), so its `>= requested level` assertion held with the boost never firing, and adversarial review caught it by computing the headroom. Boost and threshold tests must assert the exact resulting level, on a fixture whose arithmetic the test comment works out.
- Sub-microsecond benchmark tables need a noise disclaimer. An arm doing strictly more work that measures "faster" (Optimal beating Single on all-numeric content that never plans) shows code-layout and run-to-run jitter. Single-iteration BenchmarkDotNet jobs on this class of benchmark swing ±30%, so only MediumRun ratios are trusted, and deltas within a few percent are noise.

### Matrix construction

- Function areas need one shared source of truth: placement, data walking, masking and decoding depend on the same blocked-module geometry, and rebuilding it separately invites one-module drift around format, alignment or version areas.
- Remainder bits must be deterministic though they carry no payload: stack and pooled buffers are not guaranteed zeroed, so an untouched tail makes output depend on prior memory contents.
- Mask candidates must contain their own format bits: the 30 format modules affect runs, 2×2 blocks, finder-like windows and dark balance, so scoring without them is observably a different algorithm.
- The quiet zone should not inflate object storage: keeping it virtual cut `QRCodeData` to core bits and kept the public matrix coordinate space.
- An argument checked where it is first used is checked only on the routes that use it. The ECC level was checked by the capacity-table lookup, which threw `ArgumentException` without a parameter name, and the quiet zone's upper bound by the buffer-size arithmetic, which only the sizing and span routes ran, so `Create` and `CreateStructuredAppend` returned a `QRCodeData` whose `Size` had overflowed. The ECI was checked only by the Byte-length count, which Numeric and Alphanumeric text never reaches, so an undefined value was written into the header of a symbol this library could not read back. All three are now checked at every entry, with rMQR's bounds and parameter names.

### Performance

- Bit-packing was the decisive mask optimization. Parallelizing eight expensive byte-domain candidates still pays the byte-domain cost plus scheduling and allocation overhead. Packed scalar rows measured roughly 8× at version 1, 44× at version 10 and 30–40× at version 40 over the former per-module implementation.
- For the small versions the eight candidates are the vector lanes, not the rows. With one `ulong` per row (versions 1-11), four candidates per vector remove every scalar tail and per-candidate horizontal reduction, and make the pre-masked templates and format-bit overlays per-version tables (one XOR / OR per row). With the popcounts of provably disjoint bit sets fused (dark vs light 5-runs, the two finder-like orientations), this made the mask kernel 1.6-1.9x as fast again after the lane-per-row round. Fusing all scoring passes into one loop lost to register pressure, and a vectorized balance score gained nothing measurable.

  x64 without AVX2 and WebAssembly run the same scorer on two candidates per 128-bit vector (2026-09-30), popcounting with SSSE3's nibble table or WebAssembly's byte popcount into 16-bit accumulators. Versions 1-11 run at 0.59 to 0.63 of the scalar paths on default NativeAOT, 0.46 to 0.49 on WebAssembly AOT and 0.69 to 0.71 interpreted. WebAssembly needs its own 64-bit lane shifts there, since the portable ones call a software fallback in AOT-compiled code, 8x slower than scalar. The SoA scorers for versions 12-40, two rows per vector, did not beat the scalar paths on either target: they took 0.94 to 1.68 of the scalar paths' time on default NativeAOT, the 0.94 at version 20 within its runs' spread, and 1.40 to 5.80 on WebAssembly, so those versions stayed scalar there until the transposed tier (below) beat the scalar paths on every 128-bit build.

- Sequential output is faster for interleaving: round-robin source reads into a contiguous destination beat sequential reads with scattered writes, despite the strided access.
- The data placement stream should stay in a register: refilling a 64-bit MSB-aligned accumulator removes a byte load and a variable shift per module and enables a two-module fast path for the common unblocked case (the reference walk).
- Everything the placer derives from the version alone belongs in a per-version table. Painting function patterns, building the blocked bit mask and deciding the zigzag order per symbol took ~25-35 % of the encode. A cached template + mask + walk order (built by the reference painters, so correct by construction) reduced the placer to a memcpy, one vector bit expansion and a run/scatter store pass: 9x at version 1 and 4.5x at version 40 in the kernel, -26 % (v1) to -44 % (v40) on the encode E2E. The decoder shares the cached mask. Strided byte scatter is bound by store issue, and wider stores per row do not help (as with the rMQR placer).
- Reed-Solomon setup is reusable: generator polynomials depend only on the ECC count, so caching their log-domain form removes repeated construction and reduces the scalar inner loop to table lookup and XOR.
- A lookup that reads as constant can be a scan. `QRCodeConstants.GetEccInfo` walked its 160 entries through `IReadOnlyList<ECCInfo>`, and automatic selection asks once per version it tries, which was 2.35 µs of a version 40 encode, more than the whole interleave. As an index it is 0.1 µs (2026-10-02).
- A path that resolves something first must hand on what it resolved from. A pinned version or a boost analyzed the text to pick the version, then the pipeline analyzed it again, and the cross-language benchmark pins the version on every encode. Writing the symbol from the first analysis saves that pass: 18 to 23 ns on the `SimpleEncode` payloads, 1 to 3 % of their 0.8 to 1.4 µs encodes and at the edge of what an A/B on a desktop resolves, and 0.1 µs on 65 Kanji characters and 0.4 µs on 320, about 4 %, since Kanji eligibility is a pass of its own. Micro QR had the same double analysis. There the pass is 20 ns of a 0.16 to 0.22 µs encode, and pinned encodes ran 8 to 12 % faster. A review then found Micro QR's resolved paths (a narrowed range, and Optimal encodes writing the single-mode stream) fitting again the version they had just fitted, a second cost of the same kind. Without it, and with the resolved path moved behind a call of its own, pinned encodes ran another 2 to 5 % faster and a pinned encode now costs what an automatic one does, though that gain is at the edge of what an A/B on a desktop resolves and the Kanji arm showed none. Both figures are net of the drift the same runs showed on arms the change does not reach (2026-10-04).
- On small symbols the zeroing of stack buffers is a visible cost. The single-word mask tier zeroed 6.5 KB per call, sized for 64 rows whatever the symbol, about 50 ns of a 700 ns version 1 encode. A constant 32-row size for versions 1 to 3, and complements computed where they are read instead of kept in a third buffer, took the version 1 to 10 encodes to 0.93 to 0.97 of their time. A size taken from the symbol at run time lost at versions 6 and 10, where a variable-size `stackalloc` zeroes less efficiently than a constant one.

  What a buffer costs depends on the build, so each tier was measured on its own builds (2026-10-03). The Vector128 tier's third buffer went too: without it default NativeAOT and WebAssembly ran versions 1 to 10 faster by 2 to 6 %, and the JIT without AVX2 read level end to end with its mask kernel about 3 % slower. The 32-row size gained nothing measurable on any 128-bit build, nor on the scalar single-word tier, so those keep 64 rows.
- Rule 3's finder-like windows were 11-term AND chains, forward and backward, in rows and in columns. Each window is four light modules beside the seven-module core, and the light run is a term the per-colour form of rule 1 builds anyway, so both orientations are two ANDs over two shared terms, and in the column direction each term is built once per row and read by both windows that use it. The gain follows the cost of a shift (2026-10-03). On two-word rows (versions 12 to 27), where every shifted term costs two shifts and an OR per word, AVX2 mask selection took 0.40 to 0.42 of its time and the version 19 encode 0.52. Three-word rows took 0.69 to 0.71, single-word rows 0.92 to 0.94, the scalar tiers 0.81 to 0.87, and the Vector128 tier 0.89 to 0.93 on default NativeAOT and WebAssembly.
- A test that compares chosen patterns does not test a scorer. Three faults planted in the vector tiers' scalar tail windows changed scores and never the winner on the selection tests' inputs. Only comparing every tier's scores with the textbook score caught them.
- A pinned mask must not cost more than choosing one. The forced path tested the mask predicate module by module, and at version 26 that took 26 µs where scoring and choosing among all eight patterns took 9 µs, so a pinned pattern encoded in twice the automatic time. Applying the pattern's packed template rows the way the selection applies its winner made it the selection's last step alone: 0.05 to 0.08 of its former time from version 6 up on the JIT with AVX2, and a pinned encode now runs at 0.25 to 0.64 of the automatic one (2026-10-03).
- A benchmark that passes a constant where the caller passes a variable can time another program. The stage row applied the pinned mask with a literal pattern, the JIT inlined the call into the row and folded the predicate for that pattern, and the row read half of what an encode paid. That is why the slowdown reproduced end to end and not in stage timing. The row now takes the pattern as a captured variable.
- On rows of two or three words, a rule should not run along the row. The SoA tiers shifted every term of a row-direction rule across words, and the three-word tier cost about three times the two-word one at the boundary. Holding each candidate also transposed turns every row-direction rule into a column-direction rule, which combines whole words of neighbouring rows, and the data is transposed once per symbol because masking is an XOR. On the JIT with AVX2 mask selection took 0.82 to 0.90 of the SoA tiers' time at versions 12 to 27 and 0.36 to 0.44 at 28 to 40, with no step between the widths, and the version 39 and 40 encodes about half (2026-10-04). On the 128-bit builds the SoA tiers, two rows per vector, had lost to the scalar paths (up to 5.8 times their time on WebAssembly, 2026-09-30). The transposed tier, two words per vector and no cross-word shift but the 2x2 rule's one bit, took 0.39 to 0.59 of the scalar paths' time at versions 12 to 27 and 0.56 to 0.71 at 28 to 40, on the JIT without AVX2, default NativeAOT, WebAssembly AOT and interpreted (2026-10-05). Against the scalar path rewritten for .NET Framework (below) it took 0.67 to 0.73 at versions 12, 20, 27, 28 and 40 on the JIT with AVX off (2026-10-08). On ARM64 (Apple M2) it took 0.87 to 0.98 of the two-row SoA tiers' time at versions 12 to 27 and 0.42 to 0.57 at 28 to 40, on the JIT and NativeAOT, and the version 39 and 40 encodes 0.55 to 0.64, and replaced them (2026-10-05).
- A portable tier on a new build pays for its fallbacks. The 128-bit transposed tier ran on ARM64 as it stood, through portable vectors, at 1.24 to 1.39 of the SoA tiers' time at versions 12 to 27: its popcount fell back to a SWAR count, its row packing to a movemask the target lacks, its winner unpack to eight modules a step. NEON's popcount alone took it to 0.89 to 1.01, and the NEON tier's sixteen-module packing and unpack to 0.85 to 0.96, so ARM64 runs the 128-bit tier's rules with those three of its own (2026-10-05).
- A pass over padded storage should stop where the reads stop. The transposed planes are padded to whole 64-row blocks for the transpose, and the masking pass first ran over the whole plane, 128 entries at version 12 for 65 rows. Stopping it nine entries past the symbol, the farthest any rule reads, took versions 12 to 27 from 0.90 to 1.00 of the SoA tiers to about 0.82 of them. An early-abort checkpoint gains only where little is left after it: placed after the row planes, half the score, it almost never fired, and placed before the last finder windows it gained 2 to 6 % at versions 12 to 27.
- A payload writer pays by the append, not by the character. The Alphanumeric writer looked each character up through two throwing checks and appended one 11-bit field a pair: 1.1 ns a character on the JIT and 3.7 on WebAssembly AOT, 13 % of a version 40 encode with AVX2 and a fifth of it on WebAssembly once the mask work had shrunk the rest (2026-10-05). The Numeric writer, one 10-bit field a group, was 11 % of a version 40 numeric encode. A table whose -1 marks the characters outside the alphabet lets a whole step be checked by one OR. With four pairs, or fifteen digits, an append, the portable writers took 0.44 to 0.70 of the old writers' time from 40 characters up on x64, and 0.24 to 0.45 on ARM64 (Apple M2), where the old writers were slower. The vector steps took 0.18 to 0.29 of the old writers' time from 300 characters up on x64. On WebAssembly the base-against-change runs measure it, since the harness's copy of the old writer runs slow AOT-compiled: the version 40 alphanumeric payload took 0.12 of its time AOT-compiled and 0.14 interpreted. End to end that is 0.92 to 0.93 of a version 40 encode on x64, 0.83 to 0.86 of a version 40 alphanumeric one on WebAssembly, and 0.84 to 0.96 at version 10.
- A vector step has to win on each build. AVX2's wider steps gained at most 3 to 9 % over the 128-bit ones, on the longest runs, and the register-held writer gained as much, so x64 keeps one tier. WebAssembly's Numeric step was 0.95 of the portable writer AOT-compiled but up to 1.65 interpreted, where one flag gates both builds, so WebAssembly keeps the portable Numeric writer. A short run does not reach a step at all: under sixteen digits, or eight characters, the writer's own calls cost the interpreter more than they saved (1.12 of the old loop at twelve digits), so such a run is written in the caller. Nor does a Numeric run under 40 digits on x64: the SSSE3 step reads sixteen chars to write twelve, and at 16 to 36 digits, where it runs once or twice, it took up to 1.22 of the portable writer's time (2026-10-05). On ARM64 the NEON steps took 0.40 to 0.43 of the portable Alphanumeric writer's time and 0.57 to 0.61 of the Numeric one's from 300 characters up on .NET 10. With the transposed scorer the payload was 4.2 % of a version 40 alphanumeric encode there and 3.5 % of a numeric one, over the 3 % bar of the tier inventory (`qrcode-symbologies.md`), which is on the share. End to end the steps took the alphanumeric encodes to 0.95 to 0.98 of their time and the numeric ones to 0.98 to 1.00 on the JIT and NativeAOT, about the 2.5 and 1.5 % the stage rows give, and lost nowhere, so ARM64 runs them (2026-10-05). The steps declined on the other builds would have saved 0.1 to 0.3 % (AVX2's over the 128-bit ones, WebAssembly's Numeric one AOT-compiled).
- A method's inlining budget is spent by what it inlines. Inlined into its two-parameter entry, a body of the portable writer whose throwing path still sat in its loop ran out of budget before `BitWriter.WriteWide`'s 8-byte store, which was left a call, and kept the run in memory: 1.64 times the old writer's time at 16 characters, against 0.77 compiled on its own. The shipped body, that path out of its loop, still reads 0.81 inlined against 0.71 on its own at 16 characters (2026-10-05). The bodies are kept out of their callers, and the path that throws at a bad character out of the loops, where it had taken the budget of `BitWriter.Write` in an earlier variant.
- A writer's state belongs in registers for a long run. Reached through a reference, its fields went to memory on every append; a local copy for the vector loop took 0.86 to 0.93 of the loop's time from 300 characters up and costs about a nanosecond, so a run too short for a step does not make it. The copy is stored back before anything else writes, the throwing path included, or the fields of the pairs ahead of a bad character would be lost.
- A local copy is in registers only where the runtime promotes it. .NET 10 promotes `BitWriter`, a span and three more fields. The .NET 8 JIT and NativeAOT 8 keep it on the stack, so on ARM64 every append of a NEON step went through memory there: the Alphanumeric step took 0.60 to 0.64 of the portable writer's time from 300 characters up against 0.40 to 0.43 on .NET 10, and the Numeric step lost to the portable writer up to 48 digits and won only from 160 (2026-10-05). The same code needs a cut-over per runtime. The x64 steps keep the same copy and were timed on .NET 10 only.
- A struct form that .NET 10 compiles inline can cost a multiple on .NET Framework. The scalar scorer for versions 12 to 40, the netstandard builds' route, held each row as a three-word struct whose operators take `in` arguments, in a span. On .NET Framework 4.8 mask selection was 89 % of a version 40 encode and all of a version 12 one, a candidate's score cost about 395 ns a row at versions 12, 19 and 40 against 26 ns for the single-word scorer at version 10, and a version 19 encode took 329 µs where version 10 took 19. There the scorer compiled to 13.1 KB: the operators' results went through the stack, the portable span tested its pinned object at each access, and in the last loops of the column finder windows the operators and the indexer were calls. .NET 10 with hardware intrinsics off compiled it to 4.9 KB with every operator and indexer inlined. The same rules over plain words in one array took mask selection to 0.22 to 0.24 of its time. Rule 1 along a row taken from the equalities rule 2 builds, both colours at once since a dark and a light run never share a position, took that to 0.72 to 0.74, and two words a row up to version 27 to 0.75 to 0.77 there. Against the scorer it replaced, mask selection took 0.12 to 0.13 of its time at versions 12 to 26 and 0.18 to 0.19 at 40, and the quiet-zone-free encode 0.15 to 0.20 and 0.27 to 0.29, on two builds of the change. In those builds the RS stage, which the change does not touch and whose kernel compiles to the same machine code, read 0.76 to 0.80 of base timed alone at versions 19 and 40, and 0.98 to 1.02 in a build of base with one unused method added, so the encode figures there carry some placement. On .NET 8 and 10 with hardware intrinsics off, where the net8.0 and net10.0 builds take this path too, mask selection took 0.54 to 0.57 at versions 12 to 26 and 0.79 to 0.83 at 40 (2026-10-07). This path was not timed on the runtimes that load the netstandard2.1 build, .NET 6 and 7 among them, nor on .NET Framework on 32-bit x86. Summing each term's popcounts of a row's words before one reduction in the row loop, one loop for every word's column rules that sums them there too, an early abort before the column rules, and fewer masks each read even or slower.
- A load wider than what it uses is kept inside its input by its loop bound alone. The Numeric writers read four characters for three digits and sixteen for twelve, and a bound one short reads past the run without changing the stream, so no output test can see it. The tests put runs where readable memory ends, against a protected page, and a run as a slice of a longer text whose next characters are in the alphabet, as a plan passes them. Of 26 faults planted in the writers, three equivalent by construction, the reads past the run were the only ones the first version of the tests missed.
- An intrinsic can cost a method WebAssembly's interpreter compiler. With `PackedSimd.AnyTrue` the interpreter's trace compiler (jiterpreter, .NET 10.0.11) emitted an invalid module for the Alphanumeric step and left it interpreted, which the output never shows. `PackedSimd.Bitmask` compiles.
- A kernel kept apart from its siblings misses their tuning. `QRCodeData` packed its core eight modules a step while the other two data models had a vector packer for the same stream, MSB-first in row-major order. Through that packer the pack took 0.23 to 0.37 of its time, and the class API went from 1.07 to 1.11 times the quiet-zone-free span encode to 0.99 to 1.07 (JIT with AVX2, 2026-10-06). Where the packer has no vector step (netstandard, or hardware intrinsics off) its scalar loop folds every byte to 0 or 1 first, which took 1.4 to 1.6 times the old loop's time on .NET Framework 4.8, so there the placer's 0/1 modules go through the old multiply-gather (`ModuleBitPacker.PackZeroOrOne`). Building the object from the winner's packed rows would save at most the 1.6 to 2.5 % left, and every scorer tier would have to hand its rows out and still reorder them, so it was not done.
- Moving the core into the quiet zone's window in place first cost more than the rented buffer, the clear of the whole destination and the row copies it replaced, because each gap between two rows was cleared by a call, after all the rows had moved: a version 1 encode took 1.03 times as long end to end, and the kernel 1.39 to 1.44 times as long at versions 1 and 6 ([qrcode-symbologies.md](qrcode-symbologies.md#lessons-learned)). With each gap cleared as the row after it moved, still by the call, the kernel took 1.29 to 1.32 there. With one or two stores a gap, the quiet zone costs a version 1 to 40 encode 1 to 7 % (JIT with AVX2, 2026-10-06). On .NET Framework 4.8, in one process of a kernel, the move was slower to version 20, 1.13 to 1.26 times the old path's time with one or two stores a gap, and took 0.99 to 1.07 of it at version 40, so the netstandard2.0 build keeps the old path. On .NET 6 and 7, which run netstandard2.1, the move was no slower end to end: at quiet zones 4 and 6 the quiet zone cost a version 1 to 40 encode 0.3 to 2.1 % of its quiet-zone-free time with the move, against 0.5 to 2.6 % with the old path, and no symbol more (alternating pinned processes, 2026-10-07). In a kernel the move took 0.71 to 0.99 of the old path's time with one store a gap, and with two 0.78 to 0.95 on .NET 7 and 1.07 to 1.09 at versions 1 and 10 on .NET 6, which the encodes did not show. With a clear a gap (quiet zone 9) it took 1.14 to 1.48 to version 20 on both, as on .NET 8 in the same kernel (1.42 to 1.58). So netstandard2.1 moves the core in place.
- A serial recurrence is serial per instance, not per symbol. Each block's Reed-Solomon chain waits on its own register every step, but a symbol from version 6 up has several blocks of one length and generator. Run side by side in one loop, two or four of them took the RS stage to 0.67 to 0.79 of its time from version 6 up on the JIT with GFNI, a version 40 encode to 0.95 to 0.97 and rMQR R17x139's to 0.92 to 0.93 (2026-10-07). One block a vector lane took 0.55 to 0.74 of the serial kernel at versions 26 to 40, with the blocks' bytes already in lanes. With the copy into lanes counted, a byte at a time, it took 1.28 to 1.44 (a vector transpose was not tried), so it was not shipped.
- A new entry costs the symbols that gain nothing from it. The block entry first sent every symbol through its checks and the group dispatch, and the RS stage of one-block symbols read 1.13 to 1.14 at version 1 and 1.21 at rMQR R7x43 on the JIT with GFNI. A path for single blocks, tested in an inlined entry, reads them at 0.88 to 1.03 with GFNI and with SSSE3.
- Placement as bits won on the transposed scorer and lost on single-word rows. Written straight into the transposed scorer's column words, placement and selection together took 0.86 to 0.94 of their time at versions 19 to 40 in two runs and 0.97 at version 12 in one, and the quiet-zone-free encode with both changes 0.84 to 0.95 from version 12 (JIT with AVX2, 2026-10-07). On single-word rows the stream form took 1.12 to 1.14 at version 1, 1.03 at 6 and 0.97 at 10 in one run, so versions 1-11 keep the byte placement.
- A ceiling can drop a lead before it is built. For the single-word tier's popcount, a scorer that skipped the reduction outright, wrong scores and the most any deferral could save, read 0.95 to 1.03 of the same scorer with it in two runs (sizes 21 to 61), and the deferred reduction itself 1.04 to 1.26 of the shipped scorer. All eight candidates in one 512-bit pass took 1.05 to 1.15 of it on Zen 4 in one run (2026-10-07).
- Steady-state allocation guarantees need warm-up-aware tests: lazy tables, JIT compilation and `ArrayPool` initialization are one-time effects, so the Release-only allocation test warms them up before measuring the span API.

### Structured Append

- A fast path that must agree with a definition keeps the definition beside it. The closed-form chunk end had two slips, a budget with the mode indicator subtracted twice and a byte order mark that suppressed the plan for a whole chunk instead of only its Byte part. Both cost only optimality, so every round-trip test passed them, and a test against the reference walk at every budget where the answer can change caught them.
- A rule stated inline in one caller is unknown to the next. The version scan skipped the segmentation program on all-digit content, but the chunk cost and the writer ran it once per chunk and once per boost level until naming the rule (`QRSegmentPlanner.CanPlanBeatSingleMode`) let them ask it.
- An option no benchmark row turns on has an unknown cost, and a row that turns it on may still not exercise it. The boost was in no benchmark shape, and the shapes added for it never climbed a level, because a balanced split at L leaves no room for M. Only a test that climbs showed that.
- When timing stops resolving anything, count. On a machine too loaded to time, a counter of characters the program steps over per plan found the size of the waste and which end of the search bracket it was at, and on a quiet machine the counted reductions later matched wall-clock time almost one to one. The counter cannot see a per-character gain, which needs its own number: the share of steps it takes.
- Tighten a bound only where it is loose, measured on content that differs in kind. The first tightening of the budget bracket moved the end that was already tight, and the ceiling that looked tight for several rounds was tight only on the benchmark's repeated line. One table of where the answer sits against each end, across content classes, showed the floor tight everywhere and the ceiling almost nowhere.
- An estimate holds only for the design it was made under. A reuse promised by an analysis of an earlier bracket was worth a twelfth of the promise, and a brief to "hand over the last walk's plan" named a walk already removed. Read the code as it stands and measure its stages before designing.
- Vector lanes win when a lane is a whole independent instance. A vector form of one program's step lost, because its broadcast and horizontal minimum lengthen the serial chain, while eight budgets as eight lanes won by about 2.5 times, and the writer's chunks as lanes won too. The budget lanes nearly always share a character, so the control stays scalar and only the costs are vectors: classifying eight characters in vector code on every step cost 1.85 scalar steps, against one for the scalar lookup.
- A check after a search that did not know the rule is a second search. Three fixes to pricing text with U+FEFF each moved the failure to another shape, until the rule went into the recurrence and the chunk close. A fix of a review finding is new code and is reviewed as such: most findings after the first review round were in the fixes.
- A change whose point is that the output does not move needs a mutant showing that it would. Several planner savings change no symbol, so each is checked by counting the work it saves. A guard added later can empty an earlier test without failing it, so a rule's mutant is re-run whenever code ahead of the rule changes.
- Measure before designing a fix for a measured cost. A reported end-to-end gap on runs of U+FEFF was mostly the order in which the two texts were timed (the first pays for the JIT's tiering), and only the planner's part was real. A number measured at one configuration is a claim about that configuration and names it.

### Kanji

- A rule read literally can grow a symbol. "Kanji runs taken where they lower the version, as any plan is" said nothing about the UTF-8 plan `Optimal` wrote before, and a Kanji plan pays a header wherever a cell meets ASCII, so interleaved text can lose to one UTF-8 Byte run (「1日」×20 then 60 digits: 7-M as UTF-8, 8-M as Kanji). Pricing 「1日2回3錠」 by hand before writing the scan found this, and a rule of "never larger than `Single`" would not have.
- A comparison must be benchmarked where it loses too. The first Structured Append arms were all texts whose Kanji set wins, and read as a clean gain. Interleaved text, whose set stays UTF-8, was 46 % slower than forced UTF-8, because the Kanji set was planned in full before it lost, and the floors that decide which set is planned first came from that arm.
- A bound's first test can catch a fault for a reason other than the bound. A fault in the rMQR scan's memo key was first caught where a Kanji key collided with a UTF-8 key of the same widths, and a key that kept the two programs apart but dropped Kanji's width got through until a search under that fault found 「1日」×9 then 28 digits.
- A reference that checks a table-driven computation must reach every range of the table. No cell of the cost corpus sat in Shift_JIS's second range, so the parity's second offset was untested until the mutation check. Telling a Kanji set from a UTF-8 set by parity first failed for another reason: a text repeated an even number of times XORs to 0 in both charsets.
- What a reuse can save depends on which filter accepts. Walking the plan back from the scan's run saves the build's run only where a cost run accepted at the build's widths. rMQR accepts on an upper bound and has nothing to reuse. Step timings with the accepting filter traced showed that before any code changed.
- A harness must warm past tiered compilation, and a shared machine must be checked before timing. A 15 ms warm-up ended before the call-counting delay, so the first items timed ran unoptimised code and read 3 to 10 times slow. Two 40-minute ShortRun A/Bs were lost to other sessions' builds and a busy loop, which the CPU load would have shown first. Alternating base and head in short processes, with a noise arm, held up against the drift. An arm moves by about 6 % between sets of processes on this machine, so a 3-6 % gain is real only where several arms agree.

---

## Validation

The encoder is covered at several independent layers:

| Layer | Evidence |
|---|---|
| Bit stream | mode, ECI, count widths, Numeric/Alphanumeric/Byte packing, BOM, padding, canonical `HELLO WORLD` codewords, and the portable and x64 tiers of the Numeric and Alphanumeric writers against the payload written from the definition: lengths 0 to 300 at every starting bit alignment and longer runs at every fifth, every character outside the alphabet up to 0xFF and seven past it in the lanes of a vector step, as a slice of a longer text, and on runs that end where readable memory ends. The WebAssembly tier is held to the writers it replaced, alone and as a slice, by the timing mode's parity check on that build |
| Capacity | exact-fit and one-over boundaries across modes, ECC levels, and representative versions |
| ECC | ISO worked examples and scalar/SIMD parity against naive GF(256) division |
| Interleaving | unequal block groups, single-block identity, version-40 block counts, naive-reference parity |
| Placement | binary placement parity against a per-module zigzag reference |
| Masking | all-zero, all-one, and realistic matrices compared with byte-domain reference formulas, and every tier's penalty score, not only its chosen pattern, compared with the textbook score on random, degenerate and planted finder-window matrices, and every pinned pattern's matrix, on every route, compared with the ISO predicates applied module by module at all 40 versions |
| Output APIs | `QRCodeData` and span matrices compared module-for-module, dirty buffers, quiet-zone sizes, argument bounds on every entry route, allocation test |
| External compatibility | generated images decoded by ZXing |
| Internal compatibility | encode/decode round trips for all versions and ECC levels |

When the optimized implementation and a reference disagree, the simple reference and the external decoder are the specification oracle. Performance code must not define behavior.
