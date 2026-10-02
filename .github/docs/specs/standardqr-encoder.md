# Standard QR Encoder

Design record for the Standard QR encode feature (`QRCodeGenerator`): what it does, why the pipeline has this structure, and what was learned while making the implementation spec-compatible and fast. The [spec-to-code map](standardqr-spec-map.md) indexes the normative details and where they are implemented. [Standard QR Decoder](standardqr-decoder.md) documents the inverse pipeline.

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

`Create(ReadOnlySpan<char>, ...)` returns a `QRCodeData` object. A `string` converts to the span implicitly, except on the netstandard2.0 asset below C# 14.

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

`TryGetRequiredBufferSize` returns the required matrix side, byte count, and selected version. It returns `false` when the content exceeds the capacity of every version in range. Argument errors (a negative or overflowing quiet zone) still throw, so `false` means "does not fit" and nothing else.

It is the only sizing method on the surface. The throwing `GetRequiredBufferSize` that 1.1.1 released was deprecated in 1.2.0 and removed in 2.0.0. The rationale for that split, and for a `Try` method rather than a dedicated exception type, is recorded once in [rmqr-encoder.md](rmqr-encoder.md). Standard QR follows the same rule, so the three symbologies present one surface.

The encoder overwrites every byte of the region it writes: the core is a copy of a per-version template, and only a non-zero quiet zone is cleared. It accepts a dirty pooled destination, and it leaves any tail beyond the returned byte count untouched. After JIT and pool warm-up, the span path allocates nothing in Release builds.

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
| Segmentation | One segment in one mode by default. Opt-in mixed-mode segmentation (`QRSegmentation.Optimal`) splits the content into the Numeric / Alphanumeric / Byte runs with the fewest bits |
| Quiet zone | Configurable non-negative size. Span sizing and span output reject dimensions that cannot fit an `int`-sized matrix |
| Structured Append | `CreateStructuredAppend` splits text across the fewest symbols (2 to 16) whose version stays within `QRCodeGeneratorOptions.Version`. All symbols share one version, and the split is balanced so the fullest symbol is as empty as it can be. Text that fits one symbol in the range returns the plain symbol. See [Structured Append](#structured-append) |
| Output | Bit-packed `QRCodeData` or byte-per-module `Span<byte>` |

### Not implemented

- Kanji mode
- FNC1
- Arbitrary ECI assignment numbers
- Arbitrary binary payload input
- Micro QR and rMQR

By default the encoder analyzes the complete input once and emits one data segment. `QRSegmentation.Optimal` opts into the globally minimal mixed-mode split instead (see [Mixed-mode segmentation](#mixed-mode-segmentation)).

### Structured Append

ISO/IEC 18004 lets one message span up to sixteen symbols. Each symbol carries a 20-bit header ahead of its segments: mode `0011`, a 4-bit position, a 4-bit count minus one, and an 8-bit parity. `CreateStructuredAppend` writes such a set. A reader reassembles it by the rules on `QRStructuredAppend`.

**What decides the symbol count is the version cap the caller already has.** "Fewest symbols" has no answer without a bound: version 40 holds 2,953 bytes, so without a bound the answer is one symbol. The bound a caller has is the largest symbol they can print, and `QRCodeGeneratorOptions.Version` already expresses it. No count parameter exists: the caller asks with `QRVersionRange.AtMost(n)`.

Text that fits one symbol in the range returns exactly what `Create` returns, with no header. A set of one costs 20 bits and tells a reader nothing.

The planner prices every chunk with the set header. Asked alone, it therefore split in two a text that reached into the last 20 bits of a symbol, and it refused a single character too wide for a symbol with the header, although `Create` holds it. So whenever the planner splits or refuses a text that could be one symbol, the one-symbol question is asked again the way `Create` asks it, with the quiet zone left out.

The number of chunks the planner reports does not narrow which texts need that second question. At version 1-H, the six bytes of "a", a kana and "aa" fit one symbol to the bit, yet make three chunks of a set. What does narrow it is the measure the planner's search ran on, which the planner has before it counts anything: the whole text's plan or, where no plan can beat the single-mode stream, the closed form of that stream. No stream of the text costs less than that measure. So a text whose measure does not fit the largest symbol without the header is not asked about, and that is nearly every set.

Asking the question of every text shorter than a symbol's best case (ten bits on three characters) cost a run of the segmentation program that `Create` then repeated. That took a fifth more time on a two-symbol, label-sized set of order lines, and a tenth more on prose. The questions are counted, and a label that is plainly a set must ask none.

The refusal's advice asks the same two questions, so what it says would work is what the caller gets. A text the planner holds in one chunk goes straight to `Create`, so the common case costs one plan, not two.

**The split is balanced, in three searches over the symbol count.** The planner first finds the fewest symbols at the largest version. It then finds the smallest version in the range that still holds that count, and then the smallest per-symbol budget at that version that still holds it. It splits at that budget. So every symbol shares one version, and no symbol is fuller than it has to be. A greedy fill would leave a set of three full symbols and one nearly empty, which looks like a defect on one label. Splits fall on `char` boundaries and never inside a surrogate pair. More than sixteen symbols at the largest version, or a single character that fits no symbol there, throws `ArgumentException` on the options.

Each symbol's budget is its version's data capacity minus the 20-bit header and, when the set declares a charset, minus the 12-bit ECI header that every symbol repeats. A chunk's cost is its single-mode stream or, under `QRSegmentation.Optimal`, the cheaper of that and the minimal mixed plan. Cost never decreases as a chunk grows longer, so the longest chunk that fits a budget is well defined, and a greedy walk at a budget gives the symbol count that budget needs.

The walk finds that chunk end without pricing whole prefixes. Along the text, a prefix's mode changes at most twice (Numeric, then Alphanumeric, then Byte), and within one mode the single-mode cost is a closed form of the length. So the end takes a few arithmetic steps, plus one pass over the run's own bytes for a UTF-8 Byte run.

Under `Optimal`, the mixed plan includes the single-mode plan among its candidates, so the answer is one forward pass of the segmentation program. That pass yields every prefix's optimum as it goes and stops at the first prefix over budget. It is skipped inside an all-digit run, because no split improves one. Behind a byte order mark it is narrowed to the leading alphanumeric run, because only a Byte-mode chunk carries a mark, and carrying one forces the chunk's single-mode stream.

The first version priced each probe of a binary search with a full analysis of the prefix (and, under `Optimal`, a full cost run), over a prefix that could reach the end of the text. `StructuredAppendPlannerTest` checks the fast path against a reference walk that prices one character at a time with the cost definition, at every budget where the answer can change.

**A run no mode but Byte encodes is one addition per character, not a step of the state machine.** The program has seven states. A character outside the alphanumeric alphabet can only extend a Byte run, so after one such character every other state is unreachable, and stays so until an alphanumeric character arrives. The loop detects this and adds the character's bytes to the Byte state directly, recording the same predecessor the state machine would have recorded. This is where the characters are: the share that takes this path is 86 % in Japanese behind a UTF-8 declaration, 55 % in English prose, and 24 % in the digit-and-word mixture.

The program is shared by all three symbologies. So `ModeSegmenterByteRunParityTest` checks it against an independent program that always walks all seven states, comparing cost, final state and the reconstructed plan across the bands, the charsets and the mode-availability flags Micro QR passes.

**The program's step is arithmetic on six registers, not a relaxation through memory.** Each target state has two kinds of candidate: continue its run, or open the run from the cheapest state. So its cost is one add and one min over the six costs of the previous character. The first form of the program relaxed every state from every state through two stack arrays. It paid a bounds-checked write per relaxation, a 28-byte copy per character and, on the prefix walk, a scan of the six states per character. That memory traffic cost six times as much as the arithmetic (13 ns per character against 2).

The common shape, a Latin charset with Byte allowed, has its own loop for costs, for costs with parents and for the prefix walk, so the constant byte cost and the mode flags are not decided again on every character. UTF-8 and the Micro QR versions that lack a mode share one general loop per entry point.

Where parents are recorded, the candidates are tried in state order, and a later one replaces an earlier one only when it is strictly cheaper. The relaxation had the same tie-break, so the writer emits byte for byte the same plan. On a Latin symbol the six parents of a character are one 8-byte store.

`ModeSegmenterPlanParityTest` checks every loop against the all-states relaxation with its predecessor rule. It compares cost, final state, the reconstructed runs and the walk's stopping point at every budget where that point can change, across the bands, three charsets, the four mode-availability combinations and seeded random mixes of every class.

Measured on the digit-and-word mixture, the walk went from 12.8 to 2.0 ns per character and the per-symbol plan from 11.2 to 3.0. These shapes were tried and lost:

- a vector form of the step, because the broadcast and the horizontal minimum sit on the serial chain;
- a branch-free minimum, because the structure of the costs, not the content, decides which candidate wins, and the branch predictor learns it;
- a factored recurrence, which shortens a dependency chain the code does not have, since the minimums compile to branches.

**The searches only consult the segmentation program about content it could help.** A mixed plan is cheaper than one run only when one of its runs is in a mode denser than the whole content's mode. That run must also repay at least the mode and count indicators it adds, which at the narrowest band takes four digits or six characters of the alphanumeric alphabet.

One pass over the whole text finds the longest run of each kind, and that answers the question for every chunk at once, since no chunk has longer runs than the text. When the answer is no, the three searches run as `QRSegmentation.Single`: the two costs are the same number, so the split is the same split. English prose and Japanese hold neither run, and their sixteen passes over the text drop to none.

The per-symbol writer still builds its plan where a tie is possible. When the minimum is a tie, the plan it reconstructs is not always the single run, so skipping it would change which of two equally priced streams a symbol carries. That is content whose longest run of digits is exactly three. Everything below that is decided without the program (see "only where a plan can differ from one run" below).

`PlanCouldWinTest` checks the rule in the only direction that matters: a no from the predicate must be a no from the program. It covers runs of every length near the thresholds, at the start, the middle and the end of Byte-mode content, across the bands and both charsets.

**The budget search is bracketed by construction, not by capacity.** Its probes are where the split's cost goes. A probe walks the text into chunks and, under `QRSegmentation.Optimal`, each chunk costs a forward pass of the segmentation program. So the search costs one pass per bisection step, and the number of steps is the log of the bracket's width.

With the rate bound below and the version's capacity above, that bracket is thousands of bits wide, while the answer lies tens of bits from one end. So the floor is computed instead, from one pass over the text: the minimal plan for the whole text divided by the symbol count, plus the headers every symbol pays. The plans of a split concatenate into one plan for the whole text, so together they cost at least the minimal plan.

Only content whose probes are program passes pays for computing this floor. `QRSegmentation.Single`, and content no plan can beat, keep the rate bounds, since there a probe is arithmetic. `StructuredAppendPlannerTest` checks the bracket by requiring the budget to hold the count and one bit less not to.

**One walk near that floor settles the count, the version and the ceiling.** The balanced budget sits 3 to 22 bits above the plan's floor. What separates them is one chunk's rounding and a run header or two at the cuts, not the content. This was measured over digit-and-word mixtures, alphanumeric, Latin-1 and UTF-8 content, content whose density changes along the text, and seeded random runs of every class, across three version ranges and two levels.

The first form of the ceiling was the fullest chunk of the text cut into equal character counts. It is within 55 bits of the floor only on periodic content. On content whose density varies, it is hundreds to thousands of bits over the floor and usually past the capacity. So it bracketed nothing exactly where real content lives (eight to ten probes instead of five), and the benchmark's repeated lines had hidden that.

So under `Optimal` the planner takes the whole text's plan first. That plan gives the fewest symbols any split can use (its share of the capacity, rounded up). The planner then walks once at the largest version, at the floor plus 31 bits, limited to that count. If the walk holds the count:

- the count is that bound, since it cannot be fewer, and the walk at the capacity is not made;
- a scan candidate in the same count indicator band whose capacity is at least the walk's budget holds that very split, since a chunk's cost depends on the version only through the band, so that candidate is not walked either;
- the walk's budget is the ceiling of the budget search, 31 bits above its floor, and the walk's split is kept as the settled one.

Where the answer's band has no such walk (the scan went down a band, or the walk was refused), the search opens from the floor in widening steps: 31, 31 again, then tripling, with each failure raising the floor. A surrogate pair is 32 bits that cannot be cut, so content made of pairs sits 32 to 47 bits above its floor, inside the second step.

The first walk is attempted only when the bound's count leaves each symbol two margins of slack. With less slack, packing losses usually put that count out of reach (small symbols at a high level lose up to a pair's width each), and the walk at the capacity finds the count as it always did. A text that fits the largest version as one single-mode stream is answered by the walk's closed form before any plan is taken, as before.

Every shortcut is either a lower bound or a split that exists, so the plan is the one the three searches alone would find. `StructuredAppendPlannerTest` checks the count, the version and the budget against a walk that prices one character at a time. It does so on periodic content, on content whose density changes, on ranges that cross a band (9/10, 26/27), and on the pair-heavy content that fails the first margin.

Counted in passes of the program per plan, the digit-and-word mixture went from 8.9 to 7.0, digits followed by that mixture from 8.0 to 5.0, and random runs from 10.9 to 7.0. No shape takes more passes than before. Pricing the even cut's chunks as eight lanes of one vector program was measured at 4.6 times the scalar pricing and not kept, since the design that replaced the even cut has no cut to price.

**The walks of the budget search go together, eight budgets at a time.** After the walk near the floor, a plan under `Optimal` used to be the whole text's plan, that walk, and five bisection probes inside its 31-bit bracket. That is seven passes of the segmentation program, five of them the same walk at budgets a few bits apart.

Reusing what neighbouring probes share was the first idea, and it is the smaller gain. A chunk's end never moves back as the budget grows. So, from one start, a walk at a budget between a failed budget and a held one ends its chunk between their ends. The leading chunks on which those two walks agree are the probe's own, with no chunk's cost ever needed. But a greedy chunk is filled to within a character of its budget, so two walks more than a character apart in budget part ways at the first chunk. Only the last probe or two of a bisection share anything: 0.6 of 7 passes on periodic content, and nothing elsewhere. The reuse is kept for the probes that remain.

What pays is that the probes are independent instances of one program over one text. Up to eight budgets are walked at once, one per vector lane, with one vector per state:

- Accelerated 256-bit vectors carry eight 32-bit costs.
- ARM64 NEON carries eight saturating 16-bit costs, since every symbol's budget fits that representation.
- Every other 128-bit target (x64 without AVX, and WebAssembly) carries the NEON form on portable vectors, with the saturating add that SSE2 or WebAssembly provides (2026-09-30).

On those targets the walks pay only on chunks averaging 40 characters on x64 and 80 on WebAssembly. On WebAssembly, interpreted code needs the longer chunks, and one flag gates both builds. With lanes, the planner alone took 0.31 to 0.53 of the time of its scalar probes on default NativeAOT, 0.61 to 0.69 on WebAssembly AOT and 0.26 to 0.75 interpreted. An Optimal set took 0.76 to 0.83, 0.91 to 0.92 and 0.70 to 0.95 on the same three.

A lane whose chunk closes re-reads the character that did not fit as the first character of its next chunk, and so falls a step behind. This keeps the lanes within a few characters of each other. While they are at the same character, which is nearly always, a step is the scalar loop's class lookup and branches over vector adds and mins, and it costs about as much as one scalar step. Around a chunk end the lanes are stepped separately, with the class and byte cost per lane in scalar code. Closing a chunk is scalar code too, but it happens a dozen times per lane in thousands of steps.

The first batch is the walk near the floor itself: eight budgets from 3 to 31 bits above the floor. Under UTF-8 they run from 5 to 47 bits above it, because a three-byte character is 24 bits that cannot be cut, and balanced budgets reach 46 bits above the floor. The cheapest budget that holds the bound's count settles the count, the versions of its band and the ceiling, and its failed neighbour becomes the floor. A second batch takes every budget left between them. A plan is then the whole text's plan and two batches, on one-byte and UTF-8 content alike.

Runs of U+FEFF inside the text are the exception the floor does not see. A cut kept off a run moves the run, and the character ahead of it, to the next chunk. That can leave the answer above every budget of the first batch, and the search then fell back to the walk at the capacity and to scalar probes. Runs of marks after forty digits (15,000 characters) took 2.2 to 4.5 times as long to plan as the same text with an ordinary character in the marks' place, depending on the level and the range.

When every budget of the first batch fails, one more batch starts from the highest budget and spreads across the most that such a cut can leave unused. Whether that batch is taken depends on the ceiling above it:

- Below a ceiling known to hold the count, as when the bracket is opened, the batch is taken whenever two of its budgets fit under the ceiling.
- Below the capacity the count is asked at, which may not hold the count, the answer can be past the ceiling, and the batch is then a walk the fallback repeats. So the batch is taken only when the ceiling leaves it at least half its budgets. Taking it there regardless cost up to a fifth more planning for a set whose count was one more than the bound's.

Measured as multiples of the unmarked twin's planning time, on versions 1 to 40 unless named:

- six marks at level L: 2.4 → 1.5 times;
- twenty at Q: 4.5 → 2.5;
- ten at H: 3.2 → 2.0;
- four at M (versions 1 to 33): 2.2 → 1.9.

Twenty marks at L stays at 2.8, and that is the price of the gate. The capacity leaves its second batch two budgets, one of which would settle the count. Taken regardless, it would plan with no scalar walk instead of three. Which way a batch cut that short goes is not known before it is walked, and the gate favours the sets whose count is one more than the bound's. Spreading the first batch by the run instead cost a third more on the texts whose answer stays near the floor (four and ten marks at L), which the retry leaves as they were. Text without marks never reaches the retry.

The byte order mark that `Utf8Bom` asks for keeps the scalar walks, since its chunk is priced by another rule. So does content whose chunks average under the backend's threshold (128 characters for the 256-bit path, 20 for NEON). There chunks close so often, a step or two apart, that the lanes are rarely together. Measured: 78-character chunks took half as long again with lanes, and 267-character ones were nearly twice as fast.

A surrogate pair needs no rule of its own while stepping. It is priced whole on its first half, so a budget it breaks is broken on both halves, and the chunk ends before the pair either way. A mutation that dropped the rule and changed no plan is what showed this.

The scalar probes run where neither accelerated 256-bit vectors nor ARM64 NEON are available. NEON keeps native vectors in registers and tests whether any budget overflowed before it extracts individual lane bits. Wrapping the vector state in another struct introduced costly stack traffic. NEON's shorter threshold follows measurements around the old boundary and on small symbols: the compact state pays below 128 characters, while very short chunks still favor scalar probes. `StructuredAppendNeonParityTest` checks saturation, long text offsets and the budget representation boundary against the scalar walk.

`StructuredAppendLaneWalkTest` checks the lanes against the scalar walk, lane by lane. It covers:

- walks from the start of the text, and resumed walks;
- budgets one, four and thirteen bits apart;
- four versions (both sides of each count indicator band's edge, and 40);
- eight lanes, and two;
- every charset;
- pairs, lone surrogates and U+FEFF inside the text (one on every line, after digits, after a pair, at the head).

It also requires the plan to be the same plan with and without the lanes.

Those test sets were sized against faults planted in the lanes, the walk and the prefix search. Several cases caught nothing the rest miss: a mid-band version, five lanes, the middle error-correction levels, version ranges inside or across two bands, a chunk start a few characters in, a second 20,000-character text for the plan, and the long periodic text for the prefix search. In the lane-by-lane walk, only the spacing of four bits caught a tail that closes a chunk at a cost equal to its budget.

What the plan does not show, the test pins separately:

- where each batch from the floor lands against the scalar search's answer (the second batch below either ceiling, at three budgets and at four, and only once);
- the reach `KeptOffBits` gives that batch;
- the walks one plan takes per call site, which the planner counts per thread for that purpose.


The search also does not walk the answer again once it has it. The probe that last lowers the ceiling walked at the budget the search ends on. So its split is kept aside and handed back. It has to be kept aside because a failing probe after it overwrites the caller's buffer. The text is walked a final time only when no walk settled the budget the search ends on, which happens when the capacity itself was the answer. The same test file requires the split handed back to be the walk at the budget handed back, across pinned versions that put the answer at the top of the bracket.

**The symbols of a set are planned together, and only where a plan can differ from one run.** What is left of the program after the planner is the writer's work. Each symbol needs its runs, which costs alone do not give. So each chunk used to be run through the program once more with its predecessors recorded, walked back and measured, and then measured again by its caller for the set's header. On order lines that took 200 us of a 1.45 ms set, where the single-mode stream of the same chunks takes 7. Three things are now handed over instead of worked out again.

First, the planner's pass for the longest run of digits and of alphanumerics already decides whether its searches need the program. It also tells when every plan is one Byte run. With no run of digits reaching three and no alphanumeric run reaching six, a run outside Byte costs strictly more than the bytes it replaces at every version. Two digits save 9 bits and five alphanumerics save 12, against a header of at least 13. So the minimal plan is unique, and tie-breaks play no part. A Byte chunk of such a text is written as its single-mode stream, which is that plan's stream, without running the program. The threshold for digits is one below the planner's own. Three digits tie with Byte, and the program gives a tie to the split, so drawing the line one digit later would change what is emitted.

Second, the plan builder hands its measurement to its caller.

Third, the plans of up to eight symbols are built in one pass. The program is a serial recurrence, so one chunk cannot be vectorised. But the chunks of a set are independent of each other, so each gets a vector lane at its own character:

- eight at once with accelerated 256-bit vectors;
- groups of four 32-bit lanes with ARM64 NEON;
- on every other 128-bit target, the same groups on portable vectors, which moved an Optimal set 1 to 3 %.

What makes that a vector step, rather than fifteen compares and blends, is the form in which the states are carried with their predecessors: a key. The key is the cost shifted up three bits, with the state's number below it. Its minimum is the minimum cost and, among equal costs, the lowest state. That is the tie-break every ordered chain had, because each listed its candidates in state order. The predecessor is the low bits of a minimum that the cost-only loop takes anyway. In scalar code the keyed body is only as fast as the ordered chains. It replaced them because the lanes and the single chunk then share one arithmetic.

Only symbols that need a plan get a lane, in order, eight to a pass. A chunk of digits is never planned, and as a lane it would be the longest and keep the others waiting. Lanes end at different characters, so the loop runs to the nearest end and takes the results of the lanes that end there. It then points those lanes at the longest chunk, so they keep reading text that exists. Under UTF-8, a character's byte count is two compares on the vector of characters. It is asked lane by lane only on a step that holds a surrogate.

NEON keeps 32-bit keys, because the cost multiplied by eight cannot fit the budget walker's 16-bit representation. Groups with too little useful work relative to their longest chunk use scalar planning, which avoids spending most of a vector pass on padding. The parent layout and the tie-break stay shared, so plans and emitted modules are identical across backends. `ModeSegmenterLaneParityTest` compares costs, final states and reconstructed runs against an independent all-states reference, including every UTF-16 classification value, uneven groups, surrogate boundaries and U+FEFF.

None of these three changes what is written. So the writer counts, per thread, the chunks it plans alone and the passes that plan chunks together. `StructuredAppendStreamTest` pins both counts on prose (neither), on twelve symbols of order lines (two passes), on three symbols (one pass, the fewest a pass takes) and on a label of two (two planned alone).

The table the program records is what the walk back needs and no more. Of a character's six predecessors, three are fixed by the packing groups: Numeric0 comes from Numeric2, Numeric2 from Numeric1, and Alnum0 from Alnum1. So the table holds the other three in two bytes per character, where it held seven, and the stack holds a table of 256 characters instead of 73.

The walk back used to read `parents[i * 7 + state]` one character at a time. Each load's address waited for the state the load before it produced, and the walk cost close to half as much as the program that filled the table. A run can begin only at certain places: a Numeric run only where the state is Numeric1 (every third character of the run), an Alphanumeric run every second character, and a Byte run at the first entry that does not say Byte. So the walk steps from one such place to the one before it and reads only whether the run continued there. The address depends on the position alone, the branch is "continued" nearly always, and nothing waits for anything (45 us to 8 on order lines). Decoding the state from a word of packed fields, the first attempt at removing the dependent load, was slower than the load.

The lanes interleave the same two bytes by lane, so that a step is one store, and the walk back reads its lane by stride. The single chunk is one lane of that shape, because a plain loop over a Byte run is within noise of a vectorised search everywhere except on prose, and prose is the content the planner's verdict takes away.

**A search that cannot win still costs, so each is gated.** Every walk is asked "at most this many?" and stops the moment the answer is no. At a version far below the one the set needs, an unbounded walk splits the text into thousands of chunks and pays a binary search for each. On the open version range, that was most of the cost of the feature.

A lower bound on what any split can cost is priced once: every character at the cheapest rate any mode gives it, plus the headers every symbol pays at the narrowest widths any version has. It rules out the versions whose capacity times the count cannot reach it. So the version scan walks only the candidates that could hold the count, and the budget search starts at the bound's average share rather than at one bit. The bound only rejects. It is additive over chunks because a split never cuts a surrogate pair, and `StructuredAppendPlannerTest` requires it never to refuse a count the walk reaches, at any version and level.

Under `QRSegmentation.Optimal` a walk is a pass over the text, so a version the rate bound admits is checked a second time, against a tighter floor, before it is walked. That floor is the minimal plan for the whole text at that version's count indicator widths: a split's plans concatenate into one plan for the whole text at the same widths, and every chunk also pays its headers. The rate bound prices every digit as if it sat in a full Numeric group. On digit-and-word content it was admitting the eight versions just below the answer and walking each one nearly to the end of the text. The planned floor turns them away. It is computed once per count indicator band and shared with the budget bracket, which needs the same number. The same test file requires it never to refuse a count the walk reaches, and checks with the walk alone that every version below the chosen one fails.

`QRCodeStructuredAppendEncode` measures the split against the symbols it produces, so its Ratio column is the planning overhead itself.

**The charset is decided once and the parity is one XOR.** The whole text is analysed once. The charset that analysis chooses is forced on every chunk and declared in every symbol: even a pure-ASCII chunk of a UTF-8 set carries the UTF-8 ECI. The parity every symbol carries is the XOR of the whole text's bytes in that charset, computed once before splitting. Any per-chunk computation can diverge from it when a chunk would have chosen another charset on its own, and a reader cannot tell which bytes an encoder XORed. So the value has to be a function of the input and the charset alone.

When `Utf8Bom` asks for a byte order mark and the first chunk is written in Byte mode, the mark goes into the first symbol only. It is counted in the parity, since it is a prefix of the data, as in the single-symbol path. `BoostEccLevel` raises the whole set to the highest level at which every chunk still fits the shared version, never one symbol at a time, so a set does not come out with mixed levels. `MaskPattern` applies to every symbol. `Segmentation` and `QuietZoneSize` apply per symbol.

**A Kanji-eligible text can be a Kanji set.** The whole text is analysed as `Create` analyses it. So with `AllowKanji`, a text the library would write in Kanji mode can be a set in Kanji mode ([When Kanji mode is written](qrcode-symbologies.md#when-kanji-mode-is-written)). Such a set declares no charset: no symbol carries an ECI header. Its parity is the XOR of the whole text's Shift_JIS bytes, with an ASCII character taken as its own byte. Those bytes depend on the text, not on its plan, so the parity is still one XOR computed before splitting. For the CodeGlyphX fixture's text it is 176, the value CodeGlyphX writes, where a UTF-8 set of the same text carries 6.

- **Which set.** Under `Single` a chunk has to be one segment, so the set is a Kanji set only when every character has a cell, with each chunk one Kanji segment. Under `Optimal` each chunk of a Kanji set is its Kanji plan, with no UTF-8 fallback, since the set carries one charset.

  A text whose every character has a cell is a Kanji set under both segmentations, as `Create` writes it in Kanji mode under both. Character for character it costs 13 bits against 16 or 24, with no ECI header. So any split of its UTF-8 set is also a split of its Kanji set, and the Kanji set is never larger.

  A text with ASCII in it is compared with its UTF-8 set, the way `Create` compares a plan with the single stream. It becomes a Kanji set only when that needs fewer symbols, or as many at a lower version. Its Kanji runs pay a header wherever a character with a cell meets ASCII, and on interleaved text the UTF-8 set is the smaller one.

  Each set has a floor: the fewest symbols of the largest version that its characters could fill at their cheapest rate. For the Kanji set the floor also counts a run for every stretch of cells and every stretch of ASCII, since no run crosses between them (`StructuredAppendPlanner.CanHoldKanji`). The set with the lower floor is planned first. The other is planned only where its floor still lets it win: with fewer symbols, or with as many symbols at a lower version (for the Kanji set) or at a version no larger (for the UTF-8 set). When the Kanji set is skipped, its floor at one symbol, with the set header's bits given back, still says whether `Create` could hold the text in one symbol. Planning every Kanji set and comparing had cost interleaved text half again the time of its UTF-8 set, for a Kanji set that lost.
- **The cost model.** The three searches are the ones above. What changes is the chunk's price and the bounds that gate the searches. A chunk of one Kanji segment has a closed-form cost, 13 bits a character. A Kanji plan's chunk end is one forward pass of the eighth-state program (`ModeSegmenter.LongestPrefixWithinBudgetKanji`). The lower bound prices a character with a cell at 13 bits, and the floor is the whole text's Kanji plan. The single-mode shortcuts of the UTF-8 walk (a mode that changes at most twice along the text, and a character outside the alphanumeric alphabet that only extends a Byte run) do not hold for a Kanji set, whose chunks can hold both kinds of character. So a Kanji set does not take them.
- **The lanes.** A Kanji set's walks and plans run in scalar code: the lanes carry the seven-state program, which does not include the eighth state. The UTF-8 set that the Kanji set is compared with is planned as before, lanes included. The lanes were to gain the eighth state only if the scalar path left a Kanji set slower than its UTF-8 set, and it does not. For an every-cell text under `Single`, planning is 2 % of the Kanji set's time, while the UTF-8 set's planning with lanes is 8 % of its own. The Kanji set also wins on symbol count: 7 symbols against 12 for 1,000 cells at versions up to 10-L (29.5 µs against 44.2). Decided 2026-10-01: the lanes stay seven-state.
- **One symbol.** The one-symbol question is asked with `Create`'s analysis. So a text that fits one symbol as Kanji or as a Kanji plan, and not as UTF-8, comes out as `Create`'s symbol. Before sets supported Kanji, it came out as a UTF-8 set of two or more symbols.

**A U+FEFF inside the text never opens a symbol, and never opens a Byte run.** The byte-segment decoder takes a U+FEFF at the start of a segment for a byte order mark and drops it. A set meets that problem in two places, and both are handled by rules of the walk rather than by checks after it.

A chunk is a symbol of its own. A cut that lands just before a mark would open the next symbol with the mark and lose it under either segmentation. So every walk moves such a cut back past the character ahead of the mark: a surrogate pair moves whole, and the cut keeps moving back through a run of marks. The chunk stays within its budget, and the chunk end still never moves back as the budget grows. A run of marks longer than a symbol holds cannot be kept off a symbol's head at all, and such a text is refused like any text that sixteen symbols do not hold.

Inside a chunk, the segmentation program opens no Byte run at a mark (see "Plans the byte-segment decoder would misread are never built"), so the plan a chunk is sized by is always the plan written. Both rules apply to UTF-8 only. Under a declared ISO-8859-1 the decoder drops nothing, and the mark is written the way the Latin-1 writer writes any character it lacks: a replacement byte, or its low byte on the targets that narrow. A mark at the head of the text belongs to the first symbol and is lost on decode, as it is in a single symbol.

The forms this replaced are worth their lessons:

- The plan builder used to refuse the minimal plan when it opened a Byte run at the mark, and wrote the single-mode stream instead. A chunk sized by the plan had never been sized for that stream, so the encode threw, as did the boost and the one-symbol path.
- Pricing such texts as `Single` cured the throw, but made fourteen symbols out of nine.
- Pricing as asked, and falling back to `Single` only when some chunk's plan was refused, kept most texts whole. But a mark after a run of ten digits is refused every time, and one such mark in forty-eight thousand characters refused a text that sixteen symbols hold, or cost a version.
- The cut rule likewise lived in the scalar walk alone. The lanes' split was checked afterwards and, when it opened a symbol with a mark, searched again without the lanes. On a text with a mark on every line, that second search ran for three texts in ten and made the planner three times as slow.

Each of those was a check placed after a search that did not know the rule. With the rule in the recurrence and in the chunk close, it costs a compare where a mark is and nothing where none is.

At first the rule kept the lanes from stepping together. A cut moved off a mark sets that lane back by more than the one step a close costs the others. The lanes then never shared a character again, and the rest of the walk ran per lane at a seventh of the speed, slower than no lanes at all. Now the lanes ahead wait, keeping their states, until the one furthest behind is level with them. That costs a step or two per close. It brought a text with a mark on every line (`marked-40k-any`: 40,000 characters of order lines, versions 1 to 40, level L) from 2.9 ms to 1.6 ms, the time of its unmarked twin.

The wait is part of the walk's correctness on marked text, not only of its speed. A lane that moves while the lanes are apart is at the head of its chunk: one character, and then the marks its cut was kept off. There, continuing that character's run is never more expensive than opening another, so that step needs no rule for the mark. That is true only while the wait works.

The walk counts the steps it takes apart, and `StructuredAppendLaneWalkTest` bounds that count below and above, by the text's length, since a failed lane keeps closing chunks to the end of the text. A wait that half works changes plans, which the parity tests catch. A wait that stops changes the count.

`StructuredAppendStreamTest` walks the cut across the mark one character at a time at three versions, with and without a pair ahead of it. It checks:

- the round trip under both segmentations;
- the refusal of a run of marks that no symbol can keep off its head;
- that a marked text has the same symbol count as its unmarked twin (after a line feed, after an alphanumeric, and after a run of digits at the sixteen-symbol limit, where the version must match too);
- the plan that does open a Byte run at the mark under a declared ISO-8859-1.

`StructuredAppendLaneWalkTest` checks the lanes against the scalar walk on texts with a mark on every line, after digits, after a pair and at the head, and on a lane's last characters.

**The parity is of the bytes written, also when a forced charset does not hold the text.** `EciMode.Iso8859_1` forced on text outside Latin-1 is lossy by the caller's choice. The Byte writer writes the transcoder's replacement byte on the targets that go through the transcoder, and the low byte on the targets that narrow. The parity takes the same byte on each target, so it is still the XOR of what the symbols carry.

**Each symbol is the stream `Create` would write for its chunk, behind the header.** The header comes first, then the ECI header, then the mode segments. The per-symbol pipeline from data codewords on is unchanged, and that is what keeps every existing invariant (padding, error correction, interleaving, masking) in force for a set.

**A refusal advises only what would work.** Advice to "widen the version range" for a caller already at version 40, or to "lower the ECC level" where L is refused too, only sends the caller to a second refusal. So each of the three changes that keep the text's bytes as asked (a wider range, a lower level, `QRSegmentation.Optimal`) is tried by planning the text that way, including the one-symbol question. Every single change that works is offered as an alternative. Only when none works is the smallest working combination given. A lower level is named as the highest level that works, since the next one down may not work.

The refusal also states two reasons that the size of the set does not show. One is a run of U+FEFF that no symbol holds together with the character ahead of it. The other is marks whose kept-off cuts cost the text its sixteen symbols. The same text, with an ordinary character of the mark's width in place of the marks, shows this by fitting. The first sentence of the refusal names the text's mode, charset and length, as `Create`'s refusal does. The trials run on the throw path only. Advising the caller to drop a byte order mark or a declared charset was rejected, because both change the bytes the caller asked for.

---

## Pipeline

### 1. Validate the matrix request

All generation overloads reject:

- requested versions outside `1..40` (except `-1`, meaning automatic);
- negative quiet-zone sizes.

`TryGetRequiredBufferSize` and the span-output overload additionally reject quiet zones whose resulting side or squared byte count exceeds `int.MaxValue`. The span overload also rejects caller-provided buffers smaller than the calculated matrix. These paths compute dimensions with `long` arithmetic before narrowing to `int`, so `coreSize + 2 * quietZoneSize` and `totalSize * totalSize` cannot overflow.

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

Numeric and Alphanumeric payloads need no character-set conversion. An explicitly requested non-default ECI is still emitted before their data-mode indicator.

Explicit ECI is a caller constraint. In particular, forcing `Iso8859_1` is semantically correct only for text in U+0000..U+00FF. `Default` avoids an incompatible choice by upgrading text outside that range to UTF-8.

On supported x86/x64 runtimes, analysis uses AVX2 or SSE2 for character-class checks. On ARM64 it uses a NEON tier (16 chars per step, with 8-wide vector remainder blocks). A scalar path covers short inputs and other targets.

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

Byte-mode capacity is calculated from the encoded byte count, not the UTF-16 `char` count. A UTF-8 BOM adds three bytes to both capacity selection and the Byte-mode character-count indicator.

Only UTF-8 is actually counted. Latin-1 is one byte per `char`, out-of-range ones included: the writer narrows each `char`, and the encoder replaces each `char` it cannot represent with one byte of its own. A surrogate pair counts as the two code units it is, not as the one scalar value it spells. So a charset the caller forced over content it cannot represent still produces a well-formed symbol. That is what makes the mojibake the caller asked for readable as mojibake, rather than a stream a reader takes apart wrongly. The classification pass has just decided which of the two charsets applies, so it hands that answer over instead of letting the count re-derive it with a second scan of the text.

The version calculation does not reserve four mandatory terminator bits. The terminator may shrink to the remaining capacity, down to zero bits for an exact fit. If no version can hold the required header and payload bits, generation fails instead of truncating.

When `QRCodeGeneratorOptions.Version` pins a version, automatic selection is bypassed. It is intended for callers that need a fixed symbol size and already know the payload fits.

#### Version ranges

`QRCodeGeneratorOptions.Version` is a `QRVersionRange` rather than a single version, and the scan runs over `[Min, Max]` instead of 1 to 40. A pinned version is the degenerate `Exactly(n)` case, so there is one concept rather than a requested version and a range that could contradict each other. The range's bounds are validated when it is constructed, before any generator is called, and both are **inclusive**. That is why this is a domain type and not C#'s `..`, whose end is exclusive and would make `1..40` mean 1 through 39.

Two behaviours were introduced with the range and are now unconditional. Both differed from the 1.1.1 `requestedVersion` parameter, which was removed in 2.0.0:

- **A range narrower than 1-40 is checked for fit.** `Exactly(n)` reports content that does not fit version *n* as `false` from `TryGetRequiredBufferSize` (or an `ArgumentException` from `Create`). The removed parameter handed the version straight to the encoder and failed deep inside with `ArgumentOutOfRangeException (Parameter 'length')` from a span slice.
- **Sizing honours the version.** The released `GetRequiredBufferSize` had no `requestedVersion` parameter, so an ignored `Version` would have been a silent trap. `TryGetRequiredBufferSize` reports the version the range resolves to, matching what Micro QR and rMQR already do.

`QRVersionRange.Any` goes straight to the automatic path before any range resolution runs, so the default costs nothing extra. A constrained range or an ECC boost pays for the extra text analysis its resolution needs, since a boost has to know the version before it can raise the level.

**The scan does not assume the fit predicate is monotone in the version**, even though it is. It could plausibly not be: the character-count indicator widens at versions 10 and 27, so a larger version costs more header bits. Scanning `[Min, Max]` is correct either way. `VersionRangeTest.StandardQr_FitsIsMonotoneInVersion` sweeps 3 modes × 4 ECC levels × 3 ECI modes × 58 lengths × 40 versions, so the monotonicity is a checked fact rather than an assumption the code rests on.

#### ECC boost

`QRCodeGeneratorOptions.BoostEccLevel` treats the requested ECC level as a minimum. The version is chosen for that level exactly as above, and then the level is raised while the next level still fits the chosen version. The version is fixed before the boost starts, so boosting **never grows the symbol**: it turns padding the symbol would carry anyway into error-correction capacity. The main audience is symbols with an icon overlay, where the spare capacity absorbs the covered modules.

- **Off by default.** A raised level rewrites the format information and can change the winning mask, so turning the boost on by default would silently change every existing symbol. Existing tests, golden pixels and playground permalinks all assume a requested level is the emitted level.
- **Sizing ignores the flag.** The buffer size depends only on the version and the quiet zone, and the boost cannot change the version, so `TryGetRequiredBufferSize` reports the same answer either way. This is documented on the API rather than left implicit.
- **The error contract is unchanged.** Content that fits no version in the range fails with the same exception, message included, as the boost-free path. The unconstrained overflow stays `InvalidOperationException` (the released contract), and a constrained range stays `ArgumentException`. Turning boost on must not reclassify an error.
- **Standard QR only.** Micro QR ties its legal levels to the version (M1 has none, only M4 offers Q), so a boost there would interact with version selection instead of following it. rMQR has a single M→H step. Either can adopt the same contract later.

`EccBoostTest` pins the headroom classes (boost to H, stop at an intermediate level, no headroom, already at H), the version invariance, the sizing indifference and the error parity.

#### Mixed-mode segmentation

**What.** `QRSegmentation.Optimal` splits the content into the Numeric / Alphanumeric / Byte runs whose total bit cost is minimal for a candidate version, and fits the version against that cost instead of the single-mode cost. `QRSegmentation.Single` (the default) keeps one run in one mode.

**Why.** Under a single segment, a mixed payload pays the whole content's mode for every character. A URL prefix followed by a long numeric identifier is all Byte, so the digits cost 8 bits each instead of 3⅓. Splitting the digits off routinely makes the symbol a version or more smaller (`https://example.com/item?id=` + 30 digits: version 4-M as one Byte run, version 3-M split).

**Why opt-in, and why the ceiling.** Changing the default would change the emitted bit stream, and therefore the rendered symbol, for existing callers. When the content fits in a single mode, that fit caps the scan from above: only strictly smaller versions are tried. So a plan is emitted only when it lowers the version, and in every other case the single-mode stream is emitted byte for byte. The end-to-end tests assert both properties for every corpus entry.

**Why the scan needs almost no bounding machinery.** The character-count indicator widths are constant within each of the three version bands (1–9 / 10–26 / 27–40), so the optimal cost is also constant within a band. The scan computes it at most once per band (three O(n) cost runs in the worst case, with no reconstruction table) and compares it against each candidate capacity.

rMQR needed a trivial bound, a floor and a re-priced ceiling, because its 32 versions carry 13 distinct width triples across a strategy-ordered ranking. A totally ordered version set with banded widths makes the floor and the ceiling unnecessary. That lesson is worth keeping next to the rMQR one, rather than porting the bounds by reflex.

Only the trivial bound carried over: one O(n) pass that prices each character at the cheapest rate any mode could give it. Without it, content that no split can shrink still paid for a band cost run. Measured on 120 single-mode characters, the Optimal arm went from 1.8x the Single encode to roughly parity, while the shapes that win were untouched. Its blind spot is the same as rMQR's: finely alternating content clears the bound and pays for planning that then gains nothing, because seeing that switching modes on every character never pays *is* the dynamic program.

**When no single mode fits.** The ceiling does not exist, so the scan runs to the end of the window. This is the one place where `Optimal` accepts input that `Single` rejects. For example, 1,000 lowercase letters followed by 4,500 digits is 5,500 Byte-mode characters, far over the 2,953 that version 40-L holds, but well inside its 23,648 bits once the digits are split off. The path throws only when a mixed plan fails as well, and it then throws the single-mode path's exact exception type for each constraint shape, so turning segmentation on cannot reclassify an error.

**Content that cannot benefit.** All-Numeric content skips planning: no mode prices a digit below Numeric, and every extra run adds a header, so one run is provably the optimum. The rule is one predicate rather than a repeated condition, because every caller that gets a no from it skips a whole cost run, and would emit a different stream if the predicate were ever wrong. The version scan, the Structured Append chunk cost (which the ECC boost asks again per level) and the per-symbol writer all ask it. `QRSegmentPlannerUnitTest` pins it against the program itself at every version band, for both charsets, and at the lengths that land mid packing group.

**How the optimum is exact.** A run does not cost a constant amount per character (Numeric packs 3 digits into 10 bits, Alphanumeric 2 characters into 11). So the dynamic program carries the packing-group remainder in its state rather than rounding to a per-character average. The state layout and transitions are in `ModeSegmenter`, shared with the rMQR and Micro QR planners. The symbologies differ only in header widths, which are passed as parameters, so one implementation keeps the cost models (the UTF-8 surrogate rules included) from drifting apart. Micro QR also passes per-version mode availability (M1 is Numeric-only, M2 has no Byte mode), which disables the missing transitions. `QRSegmentPlannerUnitTest` checks the program against the optimum of an independent exhaustive search over mode assignments, on short content across the bands and charsets. The rMQR suite checks the same code against its own independent oracle across all 32 versions.

**Bounds.** Content longer than the largest character count any version holds in any mode (7,089, Numeric at 40-L, an exact fit) is rejected before any cost run. The margin is 4 bits. The derivation sits next to the constant in `QRSegmentPlanner`, so a change to the capacity table re-derives the constant rather than nudging it. The plan buffer is stack-allocated for content up to 64 characters, and above that it is pooled at the text's length. A plan cannot hold more runs than the content has characters, so the pooled path can never run out of space. The reconstructed plan is re-costed from the byte counts the encoder will actually emit, and rejected if the two disagree, because the bit-stream writers store without per-flush bounds checks.

**Composition with the other options.** The BOM is a stream-level prefix, and a split would move it into the middle of the decoded text. So `Utf8Bom` falls back to the single-mode stream exactly when a BOM would actually be written: in a UTF-8 Byte-mode stream. Content whose single mode is Numeric or Alphanumeric never carries a BOM (even under an explicitly requested UTF-8 charset), and still splits. The first version of the gate suppressed those splits too, and code review caught it costing a full version for nothing. The other options compose as follows:

- A version range narrows the scan window. A pinned version that only a mixed plan fits succeeds where `Single` throws.
- ECC boost runs after the plan is fixed and compares the exact planned stream bits against the higher level's capacity, which keeps the version-invariance contract.
- A pinned mask applies at the matrix stage, independently of the plan.
- Argument validation keeps the quiet-zone-first precedence of the other surfaces, and an undefined segmentation value reports the same `segmentation` parameter name on the generators and the builder.

**Plans the byte-segment decoder would misread are never built.** The shared byte-segment decoder consumes a leading EF BB BF in every segment that has no explicit ISO-8859-1 declaration, even behind an explicit UTF-8 ECI. So a plan that opened a Byte run at a U+FEFF in mid-content would decode with that character silently dropped.

Under UTF-8 the segmentation program has no such transition (`ModeSegmenter.ByteOrderMark`). At a U+FEFF past the first character, a Byte run can only continue. So the run that holds the mark opens a character early and keeps the mark inside it, and the optimum is the cheapest safe plan. Every text still has a plan, its single Byte run, so the rule refuses nothing. A text whose unconstrained optimum was safe keeps that plan bit for bit. The first character is exempt because the single-mode stream starts with the same bytes and loses the mark in the same way.

All three planners share the rule through the shared program: in the cost-only loop, the loop with parents, the prefix walk and both vector walks. These tests check it:

- `ModeSegmenterPlanParityTest` checks the three scalar loops against an all-states reference that carries the same rule.
- `StructuredAppendWriterPlanTest` checks the writer's lane per chunk against the scalar plan.
- `StructuredAppendLaneWalkTest` checks the budget search's lanes against the scalar walk.
- Each symbology's segmentation test checks the round trip and the smaller symbol.

`ModeSegmenter.HasBomRelocatedToARunStart` and the Standard QR plan pricing keep the old refusal as the answer to a model that disagreed.

Micro QR has no ECI to pin a charset. It also rejects plans that contain a non-ASCII Latin-1 Byte run whose narrowed bytes the decoder's unspecified-charset resolution would read as UTF-8 (`SegmentDecoders.ResolvesToUtf8WhenUnspecified`, kept beside the resolution it mirrors). Isolating such a run from the invalid neighbours that disambiguate it would decode it as different text. There only the minimal-bit plan is checked, so such content reports "does not fit" when no single mode holds it.

Lesson: a mixed-mode plan is only as good as the decode it produces. Cost optimality had to be constrained by the decoder's per-segment charset heuristics, and adversarial review caught this by reproduction, not by inspection.

The U+FEFF constraint was first a rejection of the minimal plan, with a fallback to the single-mode stream. Its known gap was that a slightly costlier safe split was never searched: 3,000 digits + U+FEFF + 10 letters reported "does not fit". Moving the rule into the program was deferred until the input class mattered. Structured Append is where it did: a set sizes sixteen chunks by plans, and a refused plan there is a symbol written as a stream its chunk was never sized for.

**ECI.** A plan has one ECI prefix, ahead of the first run. A decoder carries the declared charset across the runs that follow, so a plan needs no repetition. Its 12 bits are part of the cost the version scan compares.

**Kanji.** A text whose every character has a Kanji cell is one Kanji run under both segmentations, and the planner returns before any cost run. No character of it fits another mode more cheaply, and Byte would need an ECI header ([When Kanji mode is written](qrcode-symbologies.md#when-kanji-mode-is-written)).

A Kanji-eligible text with ASCII in it has two plans under `Optimal`. The analysis marks it `KanjiPlannable` on the `Optimal` paths only, so `Single` does not pay for reading past its first ASCII character. The analysis asks only whether a character has a cell: a membership test on the reverse table, without the rank and the value. The writer looks up each value, once per character.

- **The Kanji plan** comes from an eighth state, Kanji, in a program of its own (`ModeSegmenter.ComputeCostsKanji`). An ASCII character reaches Numeric, Alphanumeric and Byte at one byte. A character with a cell reaches only Kanji. The stream carries no ECI header, since its Byte runs hold only ASCII. After any character, either Kanji or the other states are reachable, never both. So Kanji's predecessor is stored in the byte that holds Byte's predecessor at an ASCII position: the table stays two bytes per character, and the walk back steps through a Kanji run as it steps through a Byte run. It is a separate program rather than a flag on the seven-state loops, so every text that is not eligible runs the program it ran before.
- **The UTF-8 plan** is the one `Optimal` wrote before.
- **The scan** goes up from the smallest version, below the single-mode (UTF-8) fit, and takes the first version that a plan holds. Where both plans hold it, the scan takes the Kanji plan. Each program is priced at most once per band, behind its own screen. The Kanji run keeps its predecessors, and the plan is walked back from the run that accepted it. That run is the band's, at the widths of whichever version of the band is taken, so running the program again for the build would compute the same table. The scan pays for writing the table, which costs less than the run the build is spared.
- **The Kanji screen** prices a character with a cell at 13 bits (78 sixths) and adds no ECI header. It adds a header for every stretch of characters with a cell and every stretch of ASCII, since no run of the plan crosses between them. If it priced the text at its UTF-8 bytes behind the header, as the seven-state screen does, it would skip the version where a Kanji plan of kana-heavy text fits (「こんにちは世界、QRコードの分割テストです。」×3 at M). The header per stretch is what makes the screen worth having on interleaved text. With one header in all, 「日本7777」×10 cleared version 4-M's screen and paid for a cost run whose plan could not fit. The narrowest count indicator stays 8 bits, since Kanji's is 8 at versions 1–9, as Byte's is. The same pass gives the UTF-8 screen.
- **Why the UTF-8 plan still competes.** Interleaved kanji and digits (「1日」×20 then 60 digits) pay a header per run as Kanji runs. There, one UTF-8 Byte run with the digits split off is a version smaller. Without that plan in the scan, `Optimal` would have grown a symbol it wrote before.

`ModeSegmenterKanjiParityTest` checks both loops, the walk back and the run counts against an independent eight-state reference. `KanjiOptimalTest` checks the output against a reference scan: a Kanji plan is written out from the standard with the mask pinned, and a UTF-8 plan is compared with the same text with UTF-8 asked for. `KanjiPlanBoundsTest` checks the screen against the optimum at every band. A Kanji Structured Append set runs the eighth state in scalar walks and plans. The lanes, which only sets use, keep the seven states and serve UTF-8 sets (see "The lanes" under [Structured Append](#structured-append)).

**Speed.** Where its UTF-8 twin plans nothing, a Kanji plan does more work: the analysis reads past ASCII, the scan prices the eighth state, and a plan is built and written run by run. The bar set for it was "no slower than the same text with UTF-8 asked for", and it is not met. On 「日本7777」×10 at M, the Kanji plan (version 5) takes about 5-10 % longer than the UTF-8 stream (version 6).

The gap is wider where the symbol is small. Micro QR's 「日本語12345」 at L takes 10-35 % longer as a Kanji plan at M3 than as its UTF-8 single stream at M4. rMQR's 「日本7777」×10 takes 6-25 % longer at R17x77 than at R13x139. The spread is between measurements; see the rMQR record for why its build runs the program itself.

Three changes narrowed the gap:

- The analysis asks the reverse table only whether a character has a cell, so the writer's lookup is the one lookup per character.
- The plan is walked back from the scan's own cost run where that run accepted it (on Standard QR the band's run, whose widths are those of every version in the band), so the build does not run the program again.
- The counts and the measure are filled in one pass.

Decided with the user on 2026-10-01: accepted, because Kanji mode is written only on request, and a caller who asks gets the smaller symbol.

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

Byte mode uses ISO-8859-1 narrowing or UTF-8 encoding. Temporary charset buffers use `stackalloc` up to 256 bytes and `ArrayPool<byte>` above that threshold. `BitWriter` stages bits in a 64-bit accumulator and bulk-writes big-endian words. Byte-mode data is copied eight bytes at a time where possible.

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
- the AdvSimd kernel on WebAssembly, with its swizzle standing in for the table lookup, for blocks of 90 data bytes × ECC codewords or more (2026-10-01). It runs at 0.11 to 0.13 of the scalar kernel on a version 40-L block, and 0.54 to 0.64 on Micro QR M4-L. Under that product, the interpreter's setup (0.35 µs before the first step) costs more than the scalar division;
- cached log-domain scalar implementation elsewhere.

Every optimized kernel is parity-tested against a deliberately naive polynomial-division reference.

### 6. Interleave the final message

`BinaryInterleaver` emits:

1. data codeword 0 from every block, then data codeword 1 from every block, and so on;
2. the extra final data row from the longer Group 2 blocks, when present;
3. ECC codeword 0 from every block, then ECC codeword 1, and so on;
4. zero remainder bits for the selected version.

The implementation writes the output sequentially and accepts strided source reads. A one-block symbol uses an identity fast path. Remainder-bit storage is cleared explicitly so uninitialized stack or pooled memory cannot affect the matrix.

### 7. Place function patterns and data

The encoder places or reserves the following (the placer writes the whole byte-per-module core, so no zeroing is required):

- three 7×7 finder patterns;
- one-module separators;
- alignment patterns for version 2+;
- horizontal and vertical timing patterns;
- the fixed dark module;
- both format-information areas;
- both version-information areas for version 7+.

Reserved modules are represented by a compact bit mask. The reference `PlaceFunctionModulesReference` painters build both the painted function modules and the bit mask once per version, and both are cached (`ModulePlacer.PlacementLayout`). The encoder copies them for each symbol. The decoder reads the same cached mask when it needs to tell function modules from data modules, which keeps both directions structurally identical.

The interleaved bits are then consumed MSB-first in the standard two-column zigzag from bottom-right to top-left, skipping column 6 and every reserved module. The reference walk keeps up to 64 pending stream bits in a register and handles both modules of a strip row together. The production placement uses the cached walk, which holds the core index per stream bit and records the rows where both strip modules are free as runs. The stream is expanded to one byte per bit. Each run row is then a single 16-bit store, and everything else is an index-table scatter. This placement is parity-tested against the reference walk for every version.

Bit expansion uses AVX2 or SSSE3 on x86, and ARM64 NEON on .NET 8+, when supported. Every backend preserves MSB-first order and writes exactly eight 0/1 bytes per input byte. Short streams and final vector blocks stay within the logical spans rather than requiring scratch-buffer slack. The ARM64 path also handles short vectors, and may align a large output stream with a scalar prefix. That alignment is only a performance hint, so an unaligned buffer or a moving GC does not change the result. The portable fallback uses arithmetic for short streams and a lazily initialized 2 KiB lookup table for longer streams, with no per-call allocation. Direct expansion parity tests cover every byte value, the vector and alignment thresholds, offset buffers, and dirty output guards, in addition to the placement tests for all 40 versions.

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

The eight formulas are precomputed as 12-row periodic templates. XOR, shifts, and popcount implement both masking and all four penalty rules without changing the reference result. On AVX2 the tier for versions 1-11 scores four candidates per vector (one pattern per lane), with per-version tables of pre-masked templates and format-bit overlays. The tiers for larger versions score four rows per vector. Parity tests compare every representation against straightforward textbook formulas.

**Pinned mask.** `QRCodeGeneratorOptions.MaskPattern` (0-7, `null` = automatic) skips the evaluation entirely and applies that one pattern with a scalar per-module loop. Any pattern gives a legal symbol, since the specification only recommends the best scorer. So pinning exists for byte-exact reproduction of symbols produced elsewhere (the decoder reports the pattern in `QRCodeDecodeInfo.MaskPattern`) and for exercising decoders against all eight patterns. Invalid values are rejected when the option is set, as with `Version`. Micro QR offers the same option over its four patterns (`MicroQRCodeGeneratorOptions.MaskPattern`, with an unrelated numbering; see the Micro QR spec map). rMQR has a single fixed mask, so it has no such option.


### 9. Write format / version information and expose output

After the winning mask is applied:

- BCH(15,5) format information encodes ECC level and mask index, applies the standard format mask, and is written twice;
- BCH(18,6) version information is written twice for versions 7–40.

For `QRCodeData`, the temporary core matrix is packed into the object's one-bit-per-module payload. The quiet zone remains virtual.

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

- **Single segment per input, by default.** It keeps the default path auditable and makes mode selection a single pass. The trade-off is non-minimal symbols for mixed-mode payloads. The opt-in `QRSegmentation.Optimal` answers it, and never changes the emitted stream unless it lowers the version.
- **Kanji mode for text JIS X 0208 holds, on request, from 2.0.0.** Through 1.x, all Unicode input went out as UTF-8 Byte mode with ECI 26, at a cost in capacity for Japanese text. The reasons were output stability and, before Kanji decoding shipped, not carrying a Shift_JIS table. The table is now generated rather than taken from `System.Text.Encoding.CodePages`, so no dependency is added. With `AllowKanji`, a text whose every character has an encoder cell is one Kanji segment with no ECI header, when the charset is the library's choice and no byte order mark is asked for. The option is off by default because an Android phone's own scanners read no Kanji mode, so the default output is 1.x's. The rule, and why it is not wider, are in [When Kanji mode is written](qrcode-symbologies.md#when-kanji-mode-is-written).
- **ASCII omits ECI by default.** This minimizes overhead and maximizes compatibility. Latin-1 and wider Unicode receive explicit ECI declarations under automatic selection.
- **BOM is explicit and UTF-8-only.** `QRCodeGeneratorOptions.Utf8Bom` affects the stream only when the selected data mode is Byte and the effective ECI is UTF-8.
- **Version can be forced.** Fixed-size applications need control over symbol dimensions, so a pinned `QRCodeGeneratorOptions.Version` bypasses minimum-fit selection rather than acting as a lower bound.
- **Quiet zone is output policy, not core symbol data.** Core encoding is always performed on the `21 + 4 * (version - 1)` matrix. Quiet-zone storage differs by output model without changing encoded modules.
- **Mask scoring includes final metadata.** Scoring a data-only candidate can choose a different winner from scoring the actual final matrix, so format and version information are part of candidate evaluation.
- **Structured Append is one method, and its count comes from the version cap.** There is no explicit-count overload, no sizing API for a set (`TryGetRequiredBufferSize` stays single-symbol) and no combine helper. The first two can be added in a minor release if a request arrives. A combine helper would decide a reassembly policy (a missing symbol, a duplicate, a parity mismatch) that cannot be taken back, and the four rules on `QRStructuredAppend` are all a caller needs.
- **A set of one is never written.** Its header costs 20 bits and tells a reader nothing. The decoder still accepts one.
- **The parity follows the bytes, so Kanji encoding moves the parity of Japanese text.** When Japanese is written in Kanji mode (with `AllowKanji`), its parity is the XOR of Shift_JIS bytes rather than UTF-8 ones. A set stays consistent, so no reader breaks. But a pinned value of this encoder's parity for Japanese input depends on the option: 「こんにちは世界、QRコードの分割テストです。」×3 carries 176 as a Kanji set and 6 as the UTF-8 set. `StructuredAppendKanjiTest` pins the Kanji sets' parity, and the older tests pin ASCII, Latin-1 and UTF-8. The decode corpus holds Japanese sets both ways, because the decoder reports what is on the wire.

---

## Lessons Learned

### Capacity and text encoding

- **Byte-mode length means encoded bytes, not UTF-16 characters.** This is the central boundary condition for UTF-8 input. Using `text.Length` would under-size every non-ASCII payload and make version transitions wrong.
- **The UTF-8 BOM belongs in the Byte character count.** Treating it as out-of-band metadata creates symbols that some readers reject because the declared count is three bytes short.
- **ECI overhead can change the version and the mask.** Twelve header bits can cross a version boundary. Even when they do not, they shift every following bit, which changes ECC, interleaving, placed data, and often the selected mask.
- **Exact-capacity inputs need no full terminator.** Padding must add `min(remaining, 4)` terminator bits rather than assuming four bits are always available.
- **A branch test whose fixture cannot reach the branch asserts nothing.** The first test of ECC boost under segmentation used content whose planned stream (350 bits) could never fit the next level's capacity (272). So its `>= requested level` assertion held with the boost never firing. Adversarial review caught it by computing the headroom. Boost and threshold tests must pin the exact resulting level, on a fixture whose arithmetic is worked out in the test comment.
- **Sub-microsecond benchmark tables need a noise disclaimer.** Rows where an arm doing strictly more work measures "faster" (Optimal beating Single on all-numeric content that never plans) are code-layout and run-to-run jitter. Single-iteration BenchmarkDotNet jobs on this class of benchmark swing ±30%, so ratios are trusted only from MediumRun jobs, and deltas within a few percent are not signal.

### Matrix construction

- **Function areas need one shared source of truth.** Placement, data walking, masking, and decoding all depend on exactly the same blocked-module geometry. Reconstructing it independently is an invitation for one-module drift around format, alignment, or version areas.
- **Remainder bits must be deterministic even though they carry no payload.** Stack and pooled buffers are not guaranteed to be zeroed. Leaving the tail untouched makes output depend on prior memory contents.
- **Mask candidates must contain their own format bits.** The 30 format modules affect runs, 2×2 blocks, finder-like windows, and dark balance. Scoring without them is observably not the same algorithm.
- **The quiet zone should not inflate object storage.** Keeping it virtual reduced `QRCodeData` to core bits only while preserving the public matrix coordinate space.

### Performance

- **Bit-packing was the decisive mask optimization.** Parallelizing eight expensive byte-domain candidates still pays the byte-domain cost and adds scheduling/allocation overhead. Packed scalar rows measured roughly 8× at version 1, 44× at version 10, and 30–40× at version 40 over the former per-module implementation.
- **For the small versions the eight candidates are the vector lanes, not the rows.** With one `ulong` per row (versions 1-11), scoring four candidates per vector removes every scalar tail and every per-candidate horizontal reduction. It also lets the pre-masked templates and the format-bit overlays be per-version tables (one XOR / OR per row). Together with fusing the popcounts of provably disjoint bit sets (dark vs light 5-runs, the two finder-like orientations), this halved the mask kernel again (1.6-1.9x) after the lane-per-row round. Fusing all scoring passes into one loop lost (register pressure), and vectorizing the balance score bought nothing measurable.

  x64 without AVX2 and WebAssembly run the same scorer on two candidates per 128-bit vector (2026-09-30). The popcount is SSSE3's nibble table or WebAssembly's byte popcount, into 16-bit accumulators. Versions 1-11 run at 0.59 to 0.63 of the scalar paths on default NativeAOT, 0.46 to 0.49 on WebAssembly AOT, and 0.69 to 0.71 interpreted. There, WebAssembly's own 64-bit lane shifts are required: the portable ones are calls into a software fallback in AOT-compiled code, 8x slower than scalar. The SoA scorers for versions 12-40, on two rows per vector, lost to the scalar paths on both targets (0.94 to 1.68 on default NativeAOT, 1.40 to 5.80 on WebAssembly), so those versions stay scalar there.

- **Sequential output wins during interleaving.** Round-robin source reads with a contiguous destination measured better than sequential source reads with scattered writes, despite the strided access.
- **The data placement stream should stay in a register.** Refilling a 64-bit MSB-aligned accumulator removes a byte load and variable shift from each module and enables a two-module fast path for the common unblocked case (the reference walk).
- **Everything the placer derives from the version alone belongs in a per-version table.** Painting the function patterns, building the blocked bit mask and deciding the zigzag order per symbol was ~25-35 % of the encode. A cached template + mask + walk order (built by the reference painters, so correct by construction) turned the placer into a memcpy plus one vector bit expansion and a run/scatter store pass. That gave 9x at version 1 and 4.5x at version 40 in the kernel, and -26 % (v1) to -44 % (v40) on the encode E2E. The decoder shares the cached mask. Strided byte scatter is bound by store issue, and wider stores per row do not help (the same finding as the rMQR placer).
- **Reed-Solomon setup is reusable.** Generator polynomials depend only on ECC count, so caching their log-domain form removes repeated polynomial construction and reduces the scalar inner loop to table lookup and XOR.
- **Steady-state allocation guarantees require warm-up-aware tests.** Lazy tables, JIT compilation, and `ArrayPool` initialization are one-time effects. The Release-only allocation test warms them up before measuring the span API.

### Structured Append

- **A fast path that must agree with a definition keeps the definition beside it.** The closed-form chunk end had two slips: a budget with the mode indicator subtracted twice, and a byte order mark that suppressed the plan for a whole chunk instead of only its Byte part. Both cost only optimality, never correctness, so every round-trip test passed them. What caught them is a test against the reference walk at every budget where the answer can change.
- **A rule stated inline in one caller is a rule the next caller does not know.** The version scan skipped the segmentation program on all-digit content. The chunk cost and the writer did not, and ran it once per chunk and once per boost level. Naming the rule (`QRSegmentPlanner.CanPlanBeatSingleMode`) is what let them ask it.
- **An option no benchmark row turns on has an unknown cost, and a row that turns it on may still not exercise it.** The boost was in no benchmark shape. The shapes added for it never climbed a level, because a balanced split at L leaves no room for M. Only a test that climbs showed that.
- **When timing stops resolving anything, count.** On a machine too loaded to time, a counter of the characters the program steps over per plan found both the size of the waste and which end of the search bracket it was at. On a quiet machine, the counted reductions later matched wall-clock time almost one to one. That counter cannot see a per-character gain, so such a gain needs its own number: the share of steps it takes.
- **Tighten a bound only where it is loose, and measure that on content that differs in kind.** The first tightening of the budget bracket moved the end that was already tight. The ceiling that looked tight for several rounds was tight only on the benchmark's repeated line. One table of where the answer sits against each end, across content classes, showed the floor tight everywhere and the ceiling almost nowhere.
- **An estimate carries the design it was made under.** A reuse promised by an analysis made against an earlier bracket was worth a twelfth of the promise. A brief to "hand over the last walk's plan" named a walk that had already been removed. Read the code as it stands and measure its stages before designing.
- **Vector lanes pay when a lane is a whole independent instance.** A vector form of one program's step lost, because its broadcast and horizontal minimum lengthen the serial chain. Eight budgets walked as eight lanes won by about 2.5 times, and the writer's chunks planned as lanes won too. The budget lanes are at the same character nearly always, so the control stays scalar and only the costs are vectors. Classifying eight characters in vector code on every step cost 1.85 scalar steps, while the scalar lookup cost one.
- **A check after a search that did not know the rule is a second search.** Three fixes to the pricing of text with U+FEFF each moved the failure to another shape. Putting the rule in the recurrence and in the chunk close ended it. A fix of a review finding is new code and is reviewed as such: most findings after the first review round were in the fixes.
- **A change whose point is that the output does not move needs a mutant showing it would.** Several of the planner's savings change no symbol, so each is pinned by counting the work it saves. A guard added later can empty a test written earlier without failing it, so a rule's mutant is re-run whenever code in front of the rule changes.
- **Measure before designing a fix for a measured cost.** A reported end-to-end gap on runs of U+FEFF was mostly the order in which the two texts were timed (the first pays for the JIT's tiering). Only the planner's part was real. A number measured at one configuration is a claim about that configuration and names it.

### Kanji

- **A rule read literally can grow a symbol.** The rule "Kanji runs taken where they lower the version, as any plan is" said nothing about the UTF-8 plan `Optimal` wrote before. A Kanji plan pays a header wherever a cell meets ASCII, so interleaved text can lose to one UTF-8 Byte run (「1日」×20 then 60 digits: 7-M as UTF-8, 8-M as Kanji). Pricing 「1日2回3錠」 by hand before writing the scan found this. A rule of "never larger than `Single`" would not have.
- **A comparison has to be benchmarked where it loses too.** The first Structured Append arms were all texts whose Kanji set wins, and they read as a clean gain. Interleaved text, whose set stays UTF-8, was 46 % slower than forced UTF-8, because the Kanji set was planned in full before it lost. The floors that decide which set is planned first came from that arm.
- **A bound's first test can catch a fault for another reason than the bound.** A fault in the rMQR scan's memo key was first caught where a Kanji key collided with a UTF-8 key of the same widths. A key that kept the two programs apart but dropped Kanji's width got through, until a search under that fault found 「1日」×9 then 28 digits.
- **A reference that checks a table-driven computation has to reach every range of the table.** No cell of the corpus written for costs sat in Shift_JIS's second range, so the parity's second offset was untested until the mutation check. Telling a Kanji set from a UTF-8 set by parity failed first for another reason: a text repeated an even number of times XORs to 0 in both charsets.
- **What a reuse can save depends on which filter accepts.** Walking the plan back from the scan's run saves the build's run only where a cost run accepted at the widths the build uses. rMQR accepts on an upper bound and has nothing to reuse. Step timings with the accepting filter traced showed that before any code moved.
- **A harness has to warm past tiered compilation, and a shared machine has to be checked before it is timed.** A warm-up of 15 ms ended before the call-counting delay, so the first items timed ran unoptimised code and read 3 to 10 times slow. Two 40-minute ShortRun A/Bs were lost to other sessions' builds and a busy loop, which the CPU load would have shown first. Alternating base and head in short processes, with a noise arm, survived the drift. An arm moves by about 6 % between sets of processes on this machine, so a 3-6 % gain is real only where several arms agree.

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

When the optimized implementation and a reference disagree, the simple reference and external decoder are treated as the specification oracle. Performance code is not allowed to define behavior.
