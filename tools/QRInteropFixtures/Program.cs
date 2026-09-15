using QRInteropFixtures;

// ZXing.Net resolves Shift_JIS through static initializers that run on first use, so
// the CodePages provider has to be registered before any encoding happens; otherwise
// Kanji-mode cases silently fall back to Byte mode.
_ = KanjiPayload.ShiftJis;

// Regenerates the committed fixture corpus under tests/FeatherQR.Tests/Fixtures/.
// Fixtures are produced by external (non-FeatherQR) encoders so the corpus can
// serve as an independent conformance oracle. See
// .github/docs/specs/qrcode-test-fixtures.md for the format and the oracle matrix.
//
// Usage: dotnet run --project tools/QRInteropFixtures -- regenerate

var command = args.Length == 0 ? "regenerate" : args[0];
if (command == "spot-check-microqr")
{
    // Decodes FeatherQR-generated Micro QR symbols with zxing-cpp.
    return MicroQRSpotCheck.Run();
}
if (command == "probe-creator")
{
    // Checks whether the pinned ZXingCpp native build can create Micro QR / rMQR.
    return CreatorProbe.Run();
}
if (command == "spot-check-rmqr")
{
    // Decodes FeatherQR-generated rMQR symbols (all versions × ECC × modes) with zxing-cpp.
    return RmQRSpotCheck.Run();
}
if (command == "probe-rmqr")
{
    // rMQR oracle facts, recorded in specs/qrcode-test-fixtures.md: libzint version
    // mapping, zxing-cpp Extra() naming, qrtool interop.
    return RmQRProbe.Run(FindRepoRoot());
}
if (command == "probe-rmqr-capacity")
{
    // Measures whether rMQR reserves misdecode-protection codewords p, by damaging
    // symbols to our correction capacity and asking zxing-cpp in both directions.
    // Backs the Correction cap decision in specs/rmqr-decoder.md.
    return RmQRCapacityProbe.Run();
}
if (command == "generate-kanji-table")
{
    // Emits src/FeatherQR/Internals/ShiftJisKanjiTable.cs from the sweep data.
    return KanjiTableGenerator.Run(FindRepoRoot());
}
if (command == "probe-kanji-sweep")
{
    // Sweeps every Kanji-mode cell through qrtool + zxing-cpp and diffs against CP932.
    return KanjiSweepProbe.Run(FindRepoRoot());
}
if (command == "probe-kanji-mapping")
{
    // Which Shift_JIS mapping (JIS X 0208 vs CP932) each oracle reader applies.
    return KanjiMappingProbe.Run(FindRepoRoot());
}
if (command == "probe-kanji")
{
    // Which external encoder lineages can emit Kanji mode, per symbology.
    // Backs the fixture plan for Kanji decode support.
    return KanjiProbe.Run();
}
if (command == "spot-check-structured-append")
{
    // Reads every symbol of the sets this library writes with the three pinned readers and
    // compares the parity with the pinned balancing encoder's. Findings go to the fixture spec.
    return StructuredAppendSpotCheck.Run();
}
if (command == "import-structured-append-samples")
{
    // Copies a third-party Structured Append capture set in as image-only fixtures,
    // manifests reader-sourced, with provenance. Args: <zxing-cpp checkout> <commit>.
    if (args.Length != 3)
    {
        Console.Error.WriteLine("Usage: dotnet run --project tools/QRInteropFixtures -- import-structured-append-samples <zxing-cpp-root> <commit>");
        return 1;
    }
    return StructuredAppendSampleImporter.Run(FindRepoRoot(), args[1], args[2]);
}
if (command != "regenerate" && command != "regenerate-structured-append")
{
    Console.Error.WriteLine($"Unknown command '{command}'. Usage: dotnet run --project tools/QRInteropFixtures -- [regenerate|regenerate-structured-append|import-structured-append-samples|spot-check-structured-append|spot-check-microqr|spot-check-rmqr|probe-creator|probe-rmqr|probe-rmqr-capacity|probe-kanji|probe-kanji-mapping|probe-kanji-sweep|generate-kanji-table]");
    return 1;
}

var repoRoot = FindRepoRoot();
var fixturesBase = Path.Combine(repoRoot, "tests", "FeatherQR.Tests", "Fixtures");

// Structured Append corpus: two in-process encoder lineages (a balancing encoder and an
// explicit-parts encoder). Every symbol passes the zxing-cpp gate, which supplies the
// per-symbol text and parity the encoders do not expose, before it is written.
// Third-party captures (import-structured-append-samples) live beside them and are
// never regenerated here.
var structuredAppendRoot = Path.Combine(fixturesBase, "StandardQrStructuredAppend");
var structuredAppendGenerators = new IStructuredAppendFixtureGenerator[]
{
    new QrCodeGeneratorStructuredAppendFixtureGenerator(),
    new CodeGlyphXStructuredAppendFixtureGenerator(),
};
var structuredAppendTotal = 0;

foreach (var generator in structuredAppendGenerators)
{
    if (!generator.IsAvailable)
    {
        Console.WriteLine($"skip: {generator.Name} (not available on this machine)");
        continue;
    }

    var generatorDir = Path.Combine(structuredAppendRoot, generator.Name);
    if (Directory.Exists(generatorDir))
        Directory.Delete(generatorDir, recursive: true);
    Directory.CreateDirectory(generatorDir);

    foreach (var caseDefinition in StructuredAppendCorpus.Cases)
    {
        var set = StructuredAppendSanityGate.Verify(generator.Generate(caseDefinition), caseDefinition);
        foreach (var fixture in set)
        {
            FixtureWriter.Write(generatorDir, fixture);
            structuredAppendTotal++;
        }
        Console.WriteLine($"wrote: {generator.Name}/{caseDefinition.Id} ({set.Length} symbols, version {string.Join("/", set.Select(f => f.Manifest.Version).Distinct())}, {string.Join("/", set.Select(f => f.Manifest.ErrorCorrectionLevel).Distinct())}, parity {set[0].Manifest.StructuredAppend!.Parity})");
    }
}

if (command == "regenerate-structured-append")
{
    Console.WriteLine($"done: {structuredAppendTotal} Structured Append fixtures under {structuredAppendRoot}");
    return 0;
}

var standardQrRoot = Path.Combine(fixturesBase, "StandardQr");

var generators = new IFixtureGenerator[]
{
    new ZXingNetFixtureGenerator(),
    // Future generators plug in here. They require external toolchains; see the
    // oracle matrix in .github/docs/specs/qrcode-test-fixtures.md before adding one.
};

var corpus = StandardQrCorpus.Cases;
var total = 0;

foreach (var generator in generators)
{
    if (!generator.IsAvailable)
    {
        Console.WriteLine($"skip: {generator.Name} (not available on this machine)");
        continue;
    }

    var generatorDir = Path.Combine(standardQrRoot, generator.Name);
    if (Directory.Exists(generatorDir))
        Directory.Delete(generatorDir, recursive: true);
    Directory.CreateDirectory(generatorDir);

    foreach (var caseDefinition in corpus)
    {
        var fixture = generator.Generate(caseDefinition);
        FixtureWriter.Write(generatorDir, fixture);
        total++;
        Console.WriteLine($"wrote: {generator.Name}/{fixture.Manifest.Id} (version {fixture.Manifest.Version}, {fixture.Manifest.ErrorCorrectionLevel}, {fixture.Manifest.Mode})");
    }
}

// Micro QR corpus: two independent external encoder lineages (libzint via the
// pinned ZXingCpp package, and the pinned qrtool prebuilt binary). Every fixture
// passes the zxing-cpp sanity gate (decode + metadata cross-check) before it is
// written, and the gate's reader supplies the manifest mask pattern.
var microQrRoot = Path.Combine(fixturesBase, "MicroQR");
var microGenerators = new IMicroQRFixtureGenerator[]
{
    new ZintMicroQRFixtureGenerator(),
    new QrtoolMicroQRFixtureGenerator(repoRoot),
};

foreach (var generator in microGenerators)
{
    if (!generator.IsAvailable)
    {
        Console.WriteLine($"skip: {generator.Name} (not available on this machine; see tools/QRInteropFixtures/get-qrtool.ps1 for the qrtool binary)");
        continue;
    }

    var generatorDir = Path.Combine(microQrRoot, generator.Name);
    if (Directory.Exists(generatorDir))
        Directory.Delete(generatorDir, recursive: true);
    Directory.CreateDirectory(generatorDir);

    foreach (var caseDefinition in MicroQRCorpus.Cases)
    {
        if (!generator.SupportsCase(caseDefinition))
        {
            Console.WriteLine($"skip: {generator.Name}/{caseDefinition.Id} (unsupported by this generator)");
            continue;
        }

        var fixture = generator.Generate(caseDefinition);
        var mask = MicroQRSanityGate.VerifyAndGetMask(fixture);
        fixture = fixture with { Manifest = fixture.Manifest with { MaskPattern = mask } };

        FixtureWriter.Write(generatorDir, fixture);
        total++;
        Console.WriteLine($"wrote: {generator.Name}/{fixture.Manifest.Id} (M{fixture.Manifest.Version}, {fixture.Manifest.ErrorCorrectionLevel}, {fixture.Manifest.Mode}, mask {mask})");
    }
}

// rMQR corpus: the same two lineages, each with its own case list (libzint carries
// the systematic per-version sweep, qrtool the boundary + UTF-8 cases, see
// RmQRCorpus). Every fixture passes the zxing-cpp sanity gate (raw-byte payload,
// version, ECC cross-check) before it is written.
var rmqrRoot = Path.Combine(fixturesBase, "RmQr");
var rmqrGenerators = new (IRmQRFixtureGenerator Generator, RmQRFixtureCaseDefinition[] Cases)[]
{
    (new ZintRmQRFixtureGenerator(), RmQRCorpus.ZintCases),
    (new QrtoolRmQRFixtureGenerator(repoRoot), RmQRCorpus.QrtoolCases),
};

foreach (var (generator, cases) in rmqrGenerators)
{
    if (!generator.IsAvailable)
    {
        Console.WriteLine($"skip: {generator.Name} (not available on this machine; see tools/QRInteropFixtures/get-qrtool.ps1 for the qrtool binary)");
        continue;
    }

    var generatorDir = Path.Combine(rmqrRoot, generator.Name);
    if (Directory.Exists(generatorDir))
        Directory.Delete(generatorDir, recursive: true);
    Directory.CreateDirectory(generatorDir);

    foreach (var caseDefinition in cases)
    {
        if (!generator.SupportsCase(caseDefinition))
        {
            Console.WriteLine($"skip: {generator.Name}/{caseDefinition.Id} (unsupported by this generator)");
            continue;
        }

        var fixture = generator.Generate(caseDefinition);
        var mask = RmQRSanityGate.VerifyAndGetMask(fixture);
        fixture = fixture with { Manifest = fixture.Manifest with { MaskPattern = mask } };

        FixtureWriter.Write(generatorDir, fixture);
        total++;
        Console.WriteLine($"wrote: {generator.Name}/{fixture.Manifest.Id} ({fixture.Manifest.VersionName}, {fixture.Manifest.ErrorCorrectionLevel}, {fixture.Manifest.Mode})");
    }
}

Console.WriteLine($"done: {total + structuredAppendTotal} fixtures under {fixturesBase}");
return 0;

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "FeatherQR.slnx")))
            return dir.FullName;
        dir = dir.Parent;
    }

    throw new InvalidOperationException("Repository root (FeatherQR.slnx) not found above the tool output directory.");
}
