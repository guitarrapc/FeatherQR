using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;

namespace FeatherQR.Tests;

/// <summary>
/// The netstandard builds' span API against this host's build, on the paths a netstandard build has code of its own for. No test host
/// runs a netstandard build, so this loads the build beside the core project into a load context of its own and calls its public span API.
/// </summary>
/// <remarks>
/// The builds are found and checked for staleness as <see cref="CoreAssemblyDependencyTest"/> finds them: <c>dotnet build</c> of the
/// solution produces them before <c>dotnet test</c> runs, and a build older than a source of the core is skipped (failed on CI).
/// The span API takes spans, which reflection cannot pass, so each entry is called through a dynamic method that makes them from arrays.
/// The same dynamic methods over this host's build give the expected answers. A loaded build runs on this host's runtime, so the
/// allocation checks hold the build's own code, not .NET Framework's.
/// </remarks>
public class NetStandardBuildParityTest
{
    private delegate int SpanCreate(string text, int eccLevel, byte[] destination, object options);
    private delegate bool SpanSizing(string text, int eccLevel, object options, out int bufferSize, out int width, out int height);
    private delegate bool SpanDecode(byte[] input, int width, int height, char[] destination, out int charsWritten, object?[]? info);

    // Lazy, so that cases starting together load a build once: a load context is not unloaded.
    private static readonly ConcurrentDictionary<string, Lazy<Assembly>> Builds = new();

    public static IEnumerable<Func<string>> Frameworks() =>
        CoreAssemblyDependencyTest.CoreTargetFrameworks().Select(f => f()).Where(f => f.StartsWith("netstandard", StringComparison.Ordinal)).Select(f => (Func<string>)(() => f));

    public static IEnumerable<Func<(string TargetFramework, string Symbology)>> Cases()
    {
        foreach (var framework in Frameworks().Select(f => f()))
        {
            foreach (var symbology in new[] { "QR", "MicroQR", "RmQR" })
                yield return () => (framework, symbology);
        }
    }

    [Test]
    [MethodDataSource(nameof(Frameworks))]
    public async Task MixedImageSymbols_MatchesThisHostsBuild(string targetFramework)
    {
        var (build, _) = await LoadBuild(targetFramework);
        var (luminance, width, height) = MixedSymbolsDecodeTest.RenderGrid(MixedSymbolsDecodeTest.Mix.AllThree, 3, 2, 20f);
        var expected = SymbolDecoder.DecodeImageSymbols(luminance, width, height);
        await Assert.That(expected.Length).IsEqualTo(6);

        // Only the span entry needs a bridge. The result array belongs to the loaded build and is read through reflection.
        var target = build.GetType("FeatherQR.SymbolDecoder", throwOnError: true)!
            .GetMethod("DecodeImageSymbols", [typeof(ReadOnlySpan<byte>), typeof(int), typeof(int)])!;
        var method = new DynamicMethod("SymbolDecoder.DecodeImageSymbols", typeof(Array),
            [typeof(byte[]), typeof(int), typeof(int)], typeof(NetStandardBuildParityTest).Module);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Newobj, typeof(ReadOnlySpan<byte>).GetConstructor([typeof(byte[])])!);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, target);
        il.Emit(OpCodes.Ret);
        var decode = method.CreateDelegate<Func<byte[], int, int, Array>>();
        var actual = decode(luminance, width, height);

        await Assert.That(actual.Length).IsEqualTo(expected.Length);
        for (var i = 0; i < actual.Length; i++)
        {
            var symbol = actual.GetValue(i)!;
            var type = symbol.GetType();
            await Assert.That(type.GetProperty("Text")!.GetValue(symbol)).IsEqualTo(expected[i].Text);
            await Assert.That(type.GetProperty("Symbology")!.GetValue(symbol)!.ToString()).IsEqualTo(expected[i].Symbology.ToString());
            await Assert.That(type.GetProperty("Corners")!.GetValue(symbol)!.ToString()).IsEqualTo(expected[i].Corners.ToString());
        }
    }

    /// <summary>
    /// Which netstandard build keeps Standard QR's pooled quiet-zone path. netstandard2.0, which .NET Framework runs, keeps the pooled core
    /// copied into the cleared destination, since the move in place is slower on .NET Framework 4.8. netstandard2.1, which .NET 6
    /// and 7 run, moves the core in place as .NET 8 and later do. The pooled path's row copy, <c>CopyIntoWindow</c>, is compiled only into a
    /// build that keeps the path, so its presence tells the two apart. <see cref="SpanCreate_DirtyDestination_MatchesThisHostsBuild"/>
    /// holds either path's output to this host's.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Frameworks))]
    public async Task StandardQRQuietZone_KeepsTheOldPathOnNetStandard20Only(string targetFramework)
    {
        var (build, _) = await LoadBuild(targetFramework);
        var copyIntoWindow = build.GetType("FeatherQR.QRCodeGenerator", throwOnError: true)!.GetMethod("CopyIntoWindow", BindingFlags.NonPublic | BindingFlags.Static);

        await Assert.That(copyIntoWindow is not null).IsEqualTo(targetFramework == "netstandard2.0")
            .Because($"{targetFramework}: only the netstandard2.0 build keeps the pooled core copied into the cleared destination; the others move the core in place");
    }

    /// <summary>
    /// The netstandard builds read a Byte segment's UTF-8 with their own decoder, since their runtime's count of chars allocates in each call
    /// that meets a bad byte, and so does .NET Framework's decode, which replaces an encoded surrogate with two U+FFFD where the host
    /// writes three. The text checks here load the builds on .NET 8 and 10, where a runtime decode straight into the destination, with no
    /// count first, reads as the host's build and allocates nothing, so those checks would pass on it. What tells the two apart is the
    /// segment reader's call to the build's own decoder, no runtime encoding or decoder reached from the reader, and no runtime encoding,
    /// encoder or decoder kept beside them.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Frameworks))]
    public async Task Utf8Decode_IsTheBuildsOwn(string targetFramework)
    {
        var (build, _) = await LoadBuild(targetFramework);
        var decoders = build.GetType("FeatherQR.Internals.BinaryDecoders.SegmentDecoders", throwOnError: true)!;
        var reader = decoders.GetMethod("DecodeBytePayload", BindingFlags.Public | BindingFlags.Static)!;
        var own = decoders.GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Where(m => m.Name == "DecodeUtf8").ToArray();
        var runtimeCalls = Reached(own.Append(reader), build)
            .Where(m => typeof(System.Text.Encoding).IsAssignableFrom(m.DeclaringType) || typeof(System.Text.Decoder).IsAssignableFrom(m.DeclaringType))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}");
        var keptDecoders = decoders.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Where(f => typeof(System.Text.Encoding).IsAssignableFrom(f.FieldType) || typeof(System.Text.Encoder).IsAssignableFrom(f.FieldType) || typeof(System.Text.Decoder).IsAssignableFrom(f.FieldType))
            .Select(f => f.Name);

        await Assert.That(Callees(reader).Select(m => m.Name)).Contains("DecodeUtf8").Because($"{targetFramework}: the segment reader decodes UTF-8 with the build's own loop");
        await Assert.That(string.Join(",", runtimeCalls)).IsEmpty().Because($"{targetFramework}: nothing the reader reaches in the build calls a runtime encoding or decoder");
        await Assert.That(string.Join(",", keptDecoders)).IsEmpty().Because($"{targetFramework}: no runtime encoding, encoder or decoder is kept for the reader");
    }

    // Walks only the build's own methods. A runtime method is what the walk looks for, so its body is never read.
    private static IEnumerable<MethodBase> Reached(IEnumerable<MethodBase> roots, Assembly build)
    {
        var seen = new HashSet<MethodBase>(roots);
        var pending = new Queue<MethodBase>(seen);
        while (pending.TryDequeue(out var method))
        {
            foreach (var callee in Callees(method))
            {
                yield return callee;
                if (callee.DeclaringType?.Assembly == build && seen.Add(callee))
                    pending.Enqueue(callee);
            }
        }
    }

    // This scans bytes and does not decode IL, so a byte that only looks like a call can add a method. It cannot drop a real one: each
    // token resolves in its method's generic context, and a delegate's target is read through ldftn and ldvirtftn.
    private static IEnumerable<MethodBase> Callees(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
        var typeArguments = method.DeclaringType is { IsGenericType: true } type ? type.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        for (var i = 0; i + 5 <= il.Length; i++)
        {
            var at = il[i] == OpCodes.Call.Value || il[i] == OpCodes.Callvirt.Value || il[i] == OpCodes.Newobj.Value ? i + 1
                : il[i] == 0xFE && i + 6 <= il.Length && (il[i + 1] == (byte)OpCodes.Ldftn.Value || il[i + 1] == (byte)OpCodes.Ldvirtftn.Value) ? i + 2
                : -1;
            if (at < 0)
                continue;
            MethodBase? callee = null;
            try
            {
                callee = method.Module.ResolveMethod(BitConverter.ToInt32(il, at), typeArguments, methodArguments);
            }
            catch (Exception e) when (e is ArgumentException or BadImageFormatException)
            {
            }
            if (callee is not null)
                yield return callee;
        }
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task SpanCreate_DirtyDestination_MatchesThisHostsBuild(string targetFramework, string symbology)
    {
        var (build, path) = await LoadBuild(targetFramework);

        // Texts of one to many versions, and of mixed modes that Optimal segments into a smaller symbol, so that the generator's planned
        // path runs: the URL for Standard QR (its planned span site), "a1234567890" for Micro QR, and the mixed text for rMQR. Standard QR's
        // longest texts take the scalar mask selection's two-word rows (500 bytes, version 17) and three-word rows (1,200 bytes, version 29).
        // Standard QR's and Micro QR's quiet zones bracket a margin of one module and one wider than a version 1 or M1 core, and each
        // symbology's reach each form of the gap between two rows (Standard QR's on the builds that move its core in place). From q = 9 the
        // netstandard builds clear a gap in one call where the host's writes two 16-byte stores up to q = 16 and four up to q = 32, and from
        // q = 33 both clear it.
        var (texts, quietZones, eccLevel, host) = symbology switch
        {
            "QR" => (new[] { "HELLO WORLD", new string('A', 40), "https://example.com/item?id=123456789012345678901234567890", "HELLO WORLD 1234567890123456789012345678901234567890", new string('7', 300), new string('z', 500), new string('z', 1200), Utf8Text },
                new[] { 0, 1, 2, 4, 7, 16, 17, 25, 32, 33 }, (int)QREccLevel.M, (Func<string, int, bool, byte[]>)HostQR),
            "MicroQR" => (new[] { "12345", "HELLO", "a1234567890", "0123456789012345678901234567890", MicroQRUtf8Text },
                new[] { 0, 1, 2, 4, 5, 8, 9, 16, 17, 25, 32, 33 }, (int)MicroQREccLevel.L, HostMicroQR),
            _ => (new[] { "HELLO WORLD", "1234567890123", "HELLO WORLD 1234567890123456789012345678901234567890", new string('k', 120), Utf8Text },
                new[] { 0, 1, 2, 4, 5, 8, 9, 16, 17, 32, 33 }, (int)RmQREccLevel.M, (Func<string, int, bool, byte[]>)HostRmQR),
        };
        await Assert.That(texts.Count(text => host(text, 0, true).Length < host(text, 0, false).Length)).IsGreaterThan(0)
            .Because($"a {symbology} text must segment under Optimal into a smaller symbol than Single, so that the planned path runs");

        var create = BindSpanCreate(build, symbology, path);
        var optionsType = build.GetType($"FeatherQR.{symbology}CodeGeneratorOptions", throwOnError: true)!;
        var segmentationType = build.GetType($"FeatherQR.{symbology}Segmentation", throwOnError: true)!;

        foreach (var text in texts)
        {
            foreach (var optimal in new[] { false, true })
            {
                foreach (var quietZone in quietZones)
                {
                    var expected = host(text, quietZone, optimal);
                    var options = Activator.CreateInstance(optionsType)!;
                    optionsType.GetProperty("QuietZoneSize")!.SetValue(options, quietZone);
                    optionsType.GetProperty("Segmentation")!.SetValue(options, Enum.Parse(segmentationType, optimal ? "Optimal" : "Single"));

                    var destination = new byte[expected.Length + 7];
                    destination.AsSpan().Fill(0xA5);
                    var written = create(text, eccLevel, destination, options);

                    var context = $"{targetFramework} {symbology} \"{(text.Length > 20 ? text[..20] + "..." : text)}\" {(optimal ? "Optimal" : "Single")} quiet zone {quietZone}";
                    if (written != expected.Length)
                        Assert.Fail($"{context}: wrote {written} bytes, this host's build {expected.Length}");
                    var matrix = destination.AsSpan(0, written);
                    if (!matrix.SequenceEqual(expected))
                    {
                        var first = matrix.CommonPrefixLength(expected);
                        Assert.Fail($"{context}: byte {first} is {matrix[first]}, this host's build {expected[first]}");
                    }
                    if (destination.AsSpan(written).IndexOfAnyExcept((byte)0xA5) >= 0)
                        Assert.Fail($"{context}: wrote past the {written} bytes of the matrix");
                }
            }
        }
    }

    // Outside ISO-8859-1, so every symbology writes it as UTF-8: two-, three- and four-byte sequences, the last a surrogate pair.
    private const string Utf8Text = "Zürich こんにちは 🎉";
    private const string MicroQRUtf8Text = "日本";

    /// <summary>
    /// A text and the options that steer it onto one path. A netstandard build has code of its own for the UTF-8 transcode both ways, since
    /// its runtime's <see cref="System.Text.Encoding.UTF8"/> allocates in each call that meets a lone surrogate and in each count of chars
    /// that meets a bad byte, .NET Framework's decoder allocates and replaces bad bytes otherwise, and netstandard2.0's
    /// <see cref="System.Text.Encoding"/> has no span overloads. netstandard2.0 counts UTF-8 with code of its own too. A netstandard build
    /// also has code of its own for Standard QR's pooled quiet-zone and FNC1 cores on netstandard2.0, and for the scalar form of every
    /// vectorized step. Kanji, ECI, a pinned mask and version 40 are paths of their own on every build.
    /// </summary>
    private sealed record SpanCase(string Name, string Symbology, string Text, string EccLevel, params (string Property, object Value)[] Options)
    {
        /// <summary>The text read back where it differs from <see cref="Text"/>: the encoder writes a lone surrogate as U+FFFD.</summary>
        public string? Decoded { get; init; }
    }

    // Past Standard QR's stack budget for one UTF-8 run (256 bytes at four a character), so that under Single, or with a byte order mark,
    // the whole text transcodes into a pooled buffer. rMQR's budget of 160 bytes is past its largest symbol's 149.
    private static readonly string LongUtf8Text = string.Concat(Enumerable.Repeat(Utf8Text, 10));

    private static readonly SpanCase[] SpanCaseTable =
    [
        new("QR ASCII URL", "QR", "https://example.com/item?id=42", "M"),
        new("QR Latin-1", "QR", "Zürich Hauptbahnhof, Gleis 7", "M"),
        new("QR UTF-8", "QR", Utf8Text, "M"),
        new("QR UTF-8 with a byte order mark", "QR", Utf8Text, "M", ("Utf8Bom", true)),
        new("QR UTF-8 long", "QR", LongUtf8Text, "M", ("Segmentation", "Optimal")),
        new("QR UTF-8 long Single", "QR", LongUtf8Text, "M", ("Segmentation", "Single")),
        new("QR UTF-8 long with a byte order mark", "QR", LongUtf8Text, "M", ("Utf8Bom", true)),
        // netstandard2.0 counts UTF-8 with a loop of its own, and both netstandard builds write it with one. Each must read a lone surrogate,
        // a reversed pair and a lone high surrogate at the end as the .NET builds' encoders do.
        new("QR lone surrogates", "QR", "a\uD800b\uDC00c\uD83D", "M") { Decoded = "a\uFFFDb\uFFFDc\uFFFD" },
        new("QR reversed pair", "QR", "\uDE00\uD83Dx", "M") { Decoded = "\uFFFD\uFFFDx" },
        new("QR lone surrogate Single", "QR", "a\uD800b", "M", ("Segmentation", "Single")) { Decoded = "a\uFFFDb" },
        new("QR lone surrogate with a byte order mark", "QR", "a\uD800b", "M", ("Utf8Bom", true)) { Decoded = "a\uFFFDb" },
        new("QR lone surrogate ECI UTF-8", "QR", "a\uD800b", "M", ("EciMode", "Utf8")) { Decoded = "a\uFFFDb" },
        new("QR lone surrogate beside Kanji", "QR", "\u65E5\u672C\uD800\u8A9E", "M", ("AllowKanji", true)) { Decoded = "\u65E5\u672C\uFFFD\u8A9E" },
        new("QR lone surrogate FNC1", "QR", "0109521234543213\u001d10AB\uD800C", "M", ("Fnc1", Fnc1First())) { Decoded = "0109521234543213\u001d10AB\uFFFDC" },
        new("QR lone surrogates long Single", "QR", string.Concat(Enumerable.Repeat("\u3053\u3093\u306B\u3061\u306F\uD800", 20)), "M", ("Segmentation", "Single")) { Decoded = string.Concat(Enumerable.Repeat("\u3053\u3093\u306B\u3061\u306F\uFFFD", 20)) },
        new("QR ECI UTF-8", "QR", "plain ascii", "M", ("EciMode", "Utf8")),
        new("QR ECI ISO-8859-1", "QR", "Zürich", "M", ("EciMode", "Iso8859_1")),
        new("QR Kanji", "QR", "日本語の漢字モード", "M", ("AllowKanji", true)),
        new("QR Kanji Single", "QR", "日本語の漢字モード", "M", ("AllowKanji", true), ("Segmentation", "Single")),
        new("QR Kanji among ASCII", "QR", "商品番号 1234567890 東京都", "M", ("AllowKanji", true)),
        new("QR Single", "QR", "HELLO WORLD 1234567890 hello", "M", ("Segmentation", "Single")),
        new("QR Optimal long", "QR", string.Concat(Enumerable.Repeat("ABC123456789xyz-", 60)), "M", ("Segmentation", "Optimal")),
        new("QR FNC1 first position", "QR", "0109521234543213\u001d10ABC123", "M", ("Fnc1", Fnc1First())),
        new("QR FNC1 first position quiet zone 0", "QR", "0109521234543213\u001d10ABC123", "M", ("Fnc1", Fnc1First()), ("QuietZoneSize", 0)),
        new("QR FNC1 second position", "QR", "AB12345", "M", ("Fnc1", Fnc1Second(37))),
        new("QR quiet zone 0", "QR", "https://example.com", "M", ("QuietZoneSize", 0)),
        new("QR quiet zone 20", "QR", "https://example.com", "M", ("QuietZoneSize", 20)),
        new("QR mask 3", "QR", "https://example.com", "M", ("MaskPattern", 3)),
        new("QR boosted ECC", "QR", "https://example.com", "L", ("BoostEccLevel", true)),
        new("QR version 40", "QR", new string('9', 7000), "L"),
        new("MicroQR numeric", "MicroQR", "0123456789", "L"),
        new("MicroQR alphanumeric", "MicroQR", "HELLO WORLD 14", "L"),
        new("MicroQR Latin-1", "MicroQR", "Zürich", "L"),
        new("MicroQR UTF-8", "MicroQR", MicroQRUtf8Text, "L"),
        new("MicroQR lone surrogate", "MicroQR", "a\uD800", "L") { Decoded = "a\uFFFD" },
        new("MicroQR Kanji", "MicroQR", "日本語", "L", ("AllowKanji", true)),
        new("MicroQR Optimal", "MicroQR", "a1234567890", "L", ("Segmentation", "Optimal")),
        new("MicroQR quiet zone 0", "MicroQR", "HELLO", "L", ("QuietZoneSize", 0)),
        new("MicroQR mask 2", "MicroQR", "12345", "L", ("MaskPattern", 2)),
        new("RmQR ASCII", "RmQR", "HELLO WORLD", "M"),
        new("RmQR UTF-8", "RmQR", Utf8Text, "M"),
        // These runs hold more chars than a third of rMQR's 160-byte budget, and the netstandard builds transcode them through refs into a
        // buffer of three bytes a char. The indexer loop would write the same bytes, so these hold the refs' output on long runs at each
        // site. The buffer's size changes only the speed.
        new("RmQR UTF-8 long", "RmQR", "12345678901234567890" + new string('k', 80) + "あ", "M"),
        new("RmQR UTF-8 long Single", "RmQR", new string('k', 100) + "あ", "M", ("Segmentation", "Single")),
        new("RmQR UTF-8 long FNC1", "RmQR", "0109521234543213\u001d10" + new string('k', 80) + "あ", "M", ("Fnc1", Fnc1First())),
        new("RmQR UTF-8 long FNC1 Single", "RmQR", "0109521234543213\u001d10" + new string('k', 80) + "あ", "M", ("Fnc1", Fnc1First()), ("Segmentation", "Single")),
        new("RmQR lone surrogates", "RmQR", "a\uD800b\uDC00c", "M") { Decoded = "a\uFFFDb\uFFFDc" },
        new("RmQR lone surrogate Single", "RmQR", "a\uD800b", "M", ("Segmentation", "Single")) { Decoded = "a\uFFFDb" },
        new("RmQR lone surrogate FNC1", "RmQR", "0109521234543213\u001d10AB\uD800C", "M", ("Fnc1", Fnc1First())) { Decoded = "0109521234543213\u001d10AB\uFFFDC" },
        new("RmQR ECI UTF-8", "RmQR", "plain ascii", "M", ("EciMode", "Utf8")),
        new("RmQR Kanji", "RmQR", "日本語の漢字", "M", ("AllowKanji", true)),
        new("RmQR FNC1 first position", "RmQR", "0109521234543213\u001d10ABC123", "M", ("Fnc1", Fnc1First())),
        new("RmQR FNC1 second position", "RmQR", "AB12345", "M", ("Fnc1", Fnc1Second(37))),
        new("RmQR Optimal long", "RmQR", string.Concat(Enumerable.Repeat("AB12345678xy-", 8)), "M", ("Segmentation", "Optimal")),
        new("RmQR quiet zone 0", "RmQR", "HELLO WORLD", "M", ("QuietZoneSize", 0)),
    ];

    /// <summary>A symbol of this host's build rendered as a luminance image, black on white or inverted.</summary>
    private sealed record ImageCase(string Name, string Symbology, string Text, string EccLevel, int PixelsPerModule, bool Inverted = false);

    private static readonly ImageCase[] ImageCaseTable =
    [
        new("QR URL", "QR", "https://example.com/item?id=42", "M", 8),
        // Version 10, so alignment patterns and the version information are read.
        new("QR version 10", "QR", new string('z', 200), "M", 4),
        new("QR inverted", "QR", "https://example.com/item?id=42", "M", 8, Inverted: true),
        new("MicroQR", "MicroQR", "HELLO WORLD 14", "L", 8),
        new("MicroQR inverted", "MicroQR", "HELLO WORLD 14", "L", 8, Inverted: true),
        new("RmQR", "RmQR", "HELLO WORLD", "M", 8),
        new("RmQR inverted", "RmQR", "HELLO WORLD", "M", 8, Inverted: true),
    ];

    // Holds any decoded text of the cases: version 40's 7,000 digits.
    private const int DecodedChars = 8192;

    public static IEnumerable<Func<(string TargetFramework, string Case)>> SpanCases() =>
        from framework in Frameworks().Select(f => f())
        from c in SpanCaseTable
        select (Func<(string, string)>)(() => (framework, c.Name));

    public static IEnumerable<Func<(string TargetFramework, string Case)>> ImageCases() =>
        from framework in Frameworks().Select(f => f())
        from c in ImageCaseTable
        select (Func<(string, string)>)(() => (framework, c.Name));

    /// <summary>
    /// Sizing, encode and matrix decode through each netstandard build's span API give this host's build's answers: the same size, the
    /// same matrix, and the text back with the same decode info. Each call allocates nothing once warm.
    /// </summary>
    /// <remarks>Runs alone, as <see cref="DecodeAllocationTest"/> does: a test running beside it can empty the array pool's shared stacks.</remarks>
    [Test]
    [NotInParallel]
    [MethodDataSource(nameof(SpanCases))]
    public async Task SpanApis_MatchThisHostsBuildAndAllocateNothing(string targetFramework, string caseName)
    {
        var c = SpanCaseTable.Single(x => x.Name == caseName);
        var (build, path) = await LoadBuild(targetFramework);
        var context = $"{targetFramework} {caseName}";

        var decoded = c.Decoded ?? c.Text;
        var expected = Encode(typeof(QRCodeGenerator).Assembly, typeof(QRCodeGenerator).Assembly.Location, c);
        await Assert.That(expected.Fits).IsTrue().Because($"{caseName}: fits a symbol on this host's build");
        await Assert.That(expected.Text).IsEqualTo(decoded).Because($"{caseName}: this host's build reads its own matrix back");

        var actual = Encode(build, path, c);
        await Assert.That(actual.Fits).IsTrue().Because($"{context}: fits a symbol");
        await Assert.That((actual.BufferSize, actual.Width, actual.Height)).IsEqualTo((expected.BufferSize, expected.Width, expected.Height)).Because($"{context}: the size this host's build gives");
        await Assert.That(actual.Matrix.AsSpan().SequenceEqual(expected.Matrix)).IsTrue().Because($"{context}: the matrix this host's build writes");
        await Assert.That(actual.Text).IsEqualTo(decoded).Because($"{context}: decodes its own matrix to the text");
        await Assert.That(actual.Info).IsEqualTo(expected.Info).Because($"{context}: the decode info this host's build gives");

#if !DEBUG
        foreach (var (call, bytes) in SteadyStateBytes(build, path, c, actual))
            await Assert.That(bytes).IsEqualTo(0).Because($"{context}: bytes the {call} allocates once warm");
#endif
    }

    // Both builds count UTF-8 through an encoding of their own.
    public static IEnumerable<Func<string>> ProviderFrameworks() => [() => $"net{Environment.Version.Major}.0", () => "netstandard2.1"];

    /// <summary>
    /// A build's encoding does not follow an encoding provider the process registers. One that answers UTF-8 with ASCII answers on this
    /// thread alone, and a fresh copy of the build is loaded, so its encoding is made under it here. Through ASCII the UTF-8 count would
    /// throw, since ASCII cannot hold the U+FFFD it replaces with.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ProviderFrameworks))]
    public async Task Encodings_IgnoreTheProcessEncodingProviders(string targetFramework)
    {
        var path = CoreAssemblyDependencyTest.FindCoreBuild(targetFramework);
        await Assert.That(path).IsNotNull().Because($"no FeatherQR.dll for {targetFramework} under src/FeatherQR/bin; build the solution first");
        CoreAssemblyDependencyTest.SkipIfStale(path!);
        AsciiProvider.RegisterOnce();

        EncodeOutcome utf8;
        AsciiProvider.OnThisThread = true;
        try
        {
            var build = new AssemblyLoadContext($"FeatherQR {targetFramework} under a provider").LoadFromStream(new MemoryStream(File.ReadAllBytes(path!)));
            utf8 = Encode(build, path!, new SpanCase("UTF-8", "QR", "Zürich こんにちは", "M"));
        }
        finally
        {
            AsciiProvider.OnThisThread = false;
        }

        await Assert.That(utf8.Text).IsEqualTo("Zürich こんにちは").Because($"{targetFramework}: the UTF-8 count's encoding is the runtime's UTF-8");
    }

    /// <summary>Answers UTF-8 with ASCII on a thread that asks it to, and leaves every other thread to the runtime.</summary>
    private sealed class AsciiProvider : System.Text.EncodingProvider
    {
        [ThreadStatic]
        internal static bool OnThisThread;

        private static int registered;

        internal static void RegisterOnce()
        {
            if (Interlocked.Exchange(ref registered, 1) == 0)
                System.Text.Encoding.RegisterProvider(new AsciiProvider());
        }

        public override System.Text.Encoding? GetEncoding(int codepage) => OnThisThread && codepage == 65001 ? System.Text.Encoding.ASCII : null;

        public override System.Text.Encoding? GetEncoding(string name) => null;
    }

    /// <summary>
    /// The netstandard builds refuse a forced ISO-8859-1 over text it cannot hold, as the host's build does
    /// (<see cref="ForcedIso8859_1Test"/>). Their Latin-1 writer narrows each char without a check, so a build that let such text through
    /// would write each char's low byte.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Frameworks))]
    public async Task ForcedIso8859_1_OverTextItCannotHold_IsRefused(string targetFramework)
    {
        var (build, path) = await LoadBuild(targetFramework);
        var options = Options(build, "QR", [("EciMode", "Iso8859_1")]);
        var eccLevel = EccLevel(build, "QR", "M");
        var text = "a" + (char)0x3042 + "b";

        await Assert.That(RefusedFor(() => BindSizing(build, "QR", path)(text, eccLevel, options, out _, out _, out _))).IsEqualTo("eciMode")
            .Because($"{targetFramework}: sizing refuses the charset");
        await Assert.That(RefusedFor(() => BindSpanCreate(build, "QR", path)(text, eccLevel, new byte[10_000], options))).IsEqualTo("eciMode")
            .Because($"{targetFramework}: the encode refuses the charset");
    }

    // The parameter an argument error names, or null when the call returns.
    private static string? RefusedFor(Action call)
    {
        try
        {
            call();
            return null;
        }
        catch (ArgumentException e)
        {
            return e.ParamName;
        }
    }

#if !DEBUG
    /// <summary>The bytes sizing, encode and matrix decode of <paramref name="c"/> through <paramref name="assembly"/>'s span API each allocate once warm, a round of calls at a time.</summary>
    private static (string Call, long Bytes)[] SteadyStateBytes(Assembly assembly, string path, SpanCase c, EncodeOutcome outcome)
    {
        var sizing = BindSizing(assembly, c.Symbology, path);
        var create = BindSpanCreate(assembly, c.Symbology, path);
        var decode = BindDecode(assembly, c.Symbology, image: false, path);
        var options = Options(assembly, c.Symbology, c.Options);
        var eccLevel = EccLevel(assembly, c.Symbology, c.EccLevel);
        var destination = new byte[outcome.BufferSize];
        var chars = new char[DecodedChars];
        return
        [
            ("sizing", DecodeAllocationTest.SteadyStateBytes(() => sizing(c.Text, eccLevel, options, out _, out _, out _))),
            ("encode", DecodeAllocationTest.SteadyStateBytes(() => create(c.Text, eccLevel, destination, options))),
            ("matrix decode", DecodeAllocationTest.SteadyStateBytes(() => decode(outcome.Matrix, outcome.Width, outcome.Height, chars, out _, null))),
        ];
    }
#endif

    /// <summary>
    /// Image decode through each netstandard build's span API reads the text with this host's build's decode info, corners included, and
    /// allocates nothing once warm. Every image step runs its scalar form there.
    /// </summary>
    /// <remarks>Runs alone, as <see cref="DecodeAllocationTest"/> does: a test running beside it can empty the array pool's shared stacks.</remarks>
    [Test]
    [NotInParallel]
    [MethodDataSource(nameof(ImageCases))]
    public async Task SpanDecodeImage_MatchesThisHostsBuildAndAllocatesNothing(string targetFramework, string caseName)
    {
        var c = ImageCaseTable.Single(x => x.Name == caseName);
        var (build, path) = await LoadBuild(targetFramework);
        var context = $"{targetFramework} {caseName}";

        var host = typeof(QRCodeGenerator).Assembly;
        var symbol = Encode(host, host.Location, new SpanCase(c.Name, c.Symbology, c.Text, c.EccLevel));
        var (luminance, width, height) = Render(symbol.Matrix, symbol.Width, symbol.Height, c.PixelsPerModule, c.Inverted);

        var expected = DecodeImage(host, host.Location, c.Symbology, luminance, width, height);
        await Assert.That(expected.Text).IsEqualTo(c.Text).Because($"{caseName}: this host's build reads the image");

        var actual = DecodeImage(build, path, c.Symbology, luminance, width, height);
        await Assert.That(actual.Text).IsEqualTo(c.Text).Because($"{context}: reads the image");
        await Assert.That(actual.Info).IsEqualTo(expected.Info).Because($"{context}: the decode info this host's build gives");

#if !DEBUG
        var decode = BindDecode(build, c.Symbology, image: true, path);
        var chars = new char[DecodedChars];
        await Assert.That(DecodeAllocationTest.SteadyStateBytes(() => decode(luminance, width, height, chars, out _, null))).IsEqualTo(0)
            .Because($"{context}: bytes the image decode allocates once warm");
#endif
    }

    /// <summary>
    /// A Byte segment the decoder reads as UTF-8, under ECI 26 or behind a byte order mark: bytes UTF-8 does not hold, and two well-formed
    /// controls. No encoder here writes the ill-formed ones, so the stream is written by hand. The netstandard builds read UTF-8 through code of their own.
    /// </summary>
    private sealed record Utf8StreamCase(string Name, bool Eci26, byte[] Payload);

    private static readonly Utf8StreamCase[] Utf8StreamCaseTable =
    [
        new("ECI UTF-8, valid", true, [0x61, 0xC3, 0xA9]),
        new("ECI UTF-8, a bad continuation", true, [0x61, 0xC3, 0x28, 0x62]),
        new("ECI UTF-8, bytes UTF-8 never holds", true, [0xFF, 0xFE, 0x61]),
        new("ECI UTF-8, a sequence cut at the end", true, [0x61, 0xE3, 0x81]),
        new("ECI UTF-8, an encoded surrogate", true, [0xED, 0xA0, 0x80, 0x61]),
        new("ECI UTF-8, an overlong form", true, [0xC0, 0xAF, 0x61]),
        new("ECI UTF-8, a four-byte sequence", true, [0x61, 0xF0, 0x9F, 0x98, 0x80]),
        new("ECI UTF-8, past U+10FFFF", true, [0xF4, 0x90, 0x80, 0x80, 0x61]),
        new("a byte order mark before bad bytes", false, [0xEF, 0xBB, 0xBF, 0x61, 0xC3, 0x28]),
    ];

    // Each lead byte's bounds and the bytes either side of each continuation range, so that random draws cut, overrun and complete
    // sequences of every length.
    private static readonly byte[] Utf8EdgeBytes =
        [0x00, 0x61, 0x7F, 0x80, 0x8F, 0x90, 0x9F, 0xA0, 0xBF, 0xC0, 0xC1, 0xC2, 0xDF, 0xE0, 0xE1, 0xEC, 0xED, 0xEE, 0xEF, 0xF0, 0xF1, 0xF3, 0xF4, 0xF5, 0xFF];

    // The other half of the segments is cut from valid UTF-8 made of ASCII runs of any length beside chars of two, three and four bytes.
    private static readonly string[] Utf8Pieces = ["a", "HELLO WORLD 12345", "https://example.com/", "é", "こんにちは", "😀"];

    /// <summary>
    /// Random Byte segments read through each netstandard build's own UTF-8 decoder give this host's build's text and decode info, which
    /// are <c>Utf8.ToUtf16</c>'s: under ECI 26 and behind a byte order mark with replacement, and with no ECI as UTF-8 only where the
    /// bytes are well formed, else as Shift_JIS or ISO-8859-1.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Frameworks))]
    public async Task SpanDecode_RandomUtf8_ReadsAsThisHostsBuild(string targetFramework)
    {
        var (build, path) = await LoadBuild(targetFramework);
        var host = typeof(QRCodeGenerator).Assembly;
        var hostDecode = BindDecode(host, "QR", image: false, host.Location);
        var buildDecode = BindDecode(build, "QR", image: false, path);
        var random = new Random(26);
        string? mismatch = null;
        for (var round = 0; round < 6000 && mismatch is null; round++)
        {
            byte[] payload;
            if (round % 2 == 0)
            {
                payload = new byte[random.Next(0, 46)];
                for (var i = 0; i < payload.Length; i++)
                    payload[i] = Utf8EdgeBytes[random.Next(Utf8EdgeBytes.Length)];
            }
            else
            {
                var text = new System.Text.StringBuilder();
                while (text.Length < 40 && random.Next(8) != 0)
                    text.Append(Utf8Pieces[random.Next(Utf8Pieces.Length)]);
                var bytes = System.Text.Encoding.UTF8.GetBytes(text.ToString());
                payload = bytes.AsSpan(0, Math.Min(bytes.Length, 45)).ToArray();
            }

            // ECI 26, no ECI, or no ECI behind a byte order mark
            var mode = round / 2 % 3;
            byte[] stream = mode == 2 ? [0xEF, 0xBB, 0xBF, .. payload] : payload;
            var matrix = KanjiSymbolBuilder.StandardQr(DataStream(mode == 0, stream, Version3LDataCodewords), version: 3, QREccLevel.L, mask: 0);

            var expected = Decode(hostDecode, matrix, 29, 29);
            if (mode != 1 && expected.Text != System.Text.Encoding.UTF8.GetString(payload))
            {
                mismatch = $"round {round}, {Convert.ToHexString(stream)}: this host's build does not read Encoding.UTF8's text";
                break;
            }
            // A segment with no ECI can read as Shift_JIS and fail on a pair without a cell: it has no text to size a destination by
            var rooms = expected.Text is null ? [DecodedChars] : new[] { DecodedChars, expected.Text.Length, expected.Text.Length - 1 };
            foreach (var room in rooms)
            {
                if (room < 0)
                    continue;
                var hostRead = Decode(hostDecode, matrix, 29, 29, room);
                var buildRead = Decode(buildDecode, matrix, 29, 29, room);
                if (buildRead != hostRead)
                {
                    mismatch = $"round {round}, {Convert.ToHexString(stream)}, room {room}: reads {buildRead}, this host's build {hostRead}";
                    break;
                }
            }
        }

        await Assert.That(mismatch).IsNull().Because($"{targetFramework}: random Byte segments, with ECI 26, without and behind a byte order mark");
    }

    // Text for the UTF-8 count and transcode: ASCII runs of any length beside chars of two, three and four bytes, and lone surrogates
    private static readonly string[] TextPieces = ["a", "HELLO WORLD 12345", "https://example.com/", "é", "こんにちは", "😀", "\uD800", "\uDC00"];

    /// <summary>
    /// Random text through each netstandard build's UTF-8 count and transcode sizes and writes this host's build's symbol: Standard QR
    /// under ECI UTF-8 in both segmentations, and Micro QR, whose payloads the transcode writes on every build. Both builds transcode with
    /// a loop of their own, and netstandard2.0 counts with one.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Frameworks))]
    public async Task SpanCreate_RandomUtf8_WritesThisHostsMatrix(string targetFramework)
    {
        var (build, path) = await LoadBuild(targetFramework);
        var host = typeof(QRCodeGenerator).Assembly;
        var random = new Random(8);
        string? mismatch = null;
        var shapes = new (string Symbology, string EccLevel, int MaxLength, (string Property, object Value)[] Options)[]
        {
            ("QR", "M", 60, [("EciMode", "Utf8")]),
            ("QR", "M", 60, [("EciMode", "Utf8"), ("Segmentation", "Single")]),
            ("MicroQR", "L", 4, []),
        };
        foreach (var (symbology, eccLevel, maxLength, options) in shapes)
        {
            // Reuse the span bridges so each random input exercises the API without generating and JIT-compiling new wrappers.
            var hostEncoder = BindEncoder(host, symbology, host.Location);
            var buildEncoder = BindEncoder(build, symbology, path);
            for (var round = 0; round < 500 && mismatch is null; round++)
            {
                var text = new System.Text.StringBuilder();
                while (text.Length < maxLength && random.Next(6) != 0)
                    text.Append(TextPieces[random.Next(TextPieces.Length)]);
                var c = new SpanCase($"{symbology} random {round}", symbology, text.ToString(0, Math.Min(text.Length, maxLength)), eccLevel, options);

                var expected = Encode(host, c, hostEncoder);
                var actual = Encode(build, c, buildEncoder);
                if (actual.Fits != expected.Fits || !actual.Matrix.AsSpan().SequenceEqual(expected.Matrix))
                    mismatch = $"{c.Name}, \"{c.Text}\": a symbol other than this host's build's";
            }
        }

        await Assert.That(mismatch).IsNull().Because($"{targetFramework}: random text through the UTF-8 count and transcode");
    }

    public static IEnumerable<Func<(string TargetFramework, string Case)>> Utf8StreamCases() =>
        from framework in Frameworks().Select(f => f())
        from c in Utf8StreamCaseTable
        select (Func<(string, string)>)(() => (framework, c.Name));

    /// <summary>
    /// Matrix decode of a Byte segment read as UTF-8, ill-formed or one of the two controls, gives this host's build's text and decode info through each
    /// netstandard build's span API, and allocates nothing once warm.
    /// </summary>
    /// <remarks>Runs alone, as <see cref="DecodeAllocationTest"/> does: a test running beside it can empty the array pool's shared stacks.</remarks>
    [Test]
    [NotInParallel]
    [MethodDataSource(nameof(Utf8StreamCases))]
    public async Task SpanDecode_BadUtf8_MatchesThisHostsBuildAndAllocatesNothing(string targetFramework, string caseName)
    {
        var c = Utf8StreamCaseTable.Single(x => x.Name == caseName);
        var (build, path) = await LoadBuild(targetFramework);
        var context = $"{targetFramework} {caseName}";
        var matrix = KanjiSymbolBuilder.StandardQr(DataStream(c.Eci26, c.Payload, Version1LDataCodewords), version: 1, QREccLevel.L, mask: 0);

        var host = typeof(QRCodeGenerator).Assembly;
        var expected = Decode(BindDecode(host, "QR", image: false, host.Location), matrix, 21, 21);
        var bytes = c.Eci26 ? c.Payload : c.Payload[3..];
        await Assert.That(expected.Text).IsEqualTo(System.Text.Encoding.UTF8.GetString(bytes)).Because($"{caseName}: this host's build reads the bytes as Encoding.UTF8 does");

        var actual = Decode(BindDecode(build, "QR", image: false, path), matrix, 21, 21);
        await Assert.That(actual.Text).IsEqualTo(expected.Text).Because($"{context}: the text this host's build reads");
        await Assert.That(actual.Info).IsEqualTo(expected.Info).Because($"{context}: the decode info this host's build gives");

#if !DEBUG
        var decode = BindDecode(build, "QR", image: false, path);
        var chars = new char[DecodedChars];
        await Assert.That(DecodeAllocationTest.SteadyStateBytes(() => decode(matrix, 21, 21, chars, out _, null))).IsEqualTo(0)
            .Because($"{context}: bytes the matrix decode allocates once warm");
#endif
    }

    private const int Version1LDataCodewords = 19;
    private const int Version3LDataCodewords = 55;

    /// <summary>The data codewords of a symbol holding one Byte segment of <paramref name="payload"/>, under ECI 26 or none, with a count indicator of 8 bits (versions 1 to 9).</summary>
    internal static byte[] DataStream(bool eci26, byte[] payload, int dataCodewords)
    {
        var bits = new System.Text.StringBuilder();
        if (eci26)
            bits.Append("0111").Append("00011010");
        bits.Append("0100").Append(Convert.ToString(payload.Length, 2).PadLeft(8, '0'));
        foreach (var b in payload)
            bits.Append(Convert.ToString(b, 2).PadLeft(8, '0'));
        bits.Append("0000");
        while (bits.Length % 8 != 0)
            bits.Append('0');

        var data = new byte[dataCodewords];
        var i = 0;
        for (; i < bits.Length / 8; i++)
            data[i] = Convert.ToByte(bits.ToString(i * 8, 8), 2);
        for (var pad = 0; i < data.Length; i++, pad++)
            data[i] = pad % 2 == 0 ? (byte)0xEC : (byte)0x11;
        return data;
    }

    private sealed record EncodeOutcome(bool Fits, int BufferSize, int Width, int Height, byte[] Matrix, string? Text, string? Info);

    private sealed record SpanEncoder(SpanSizing Size, SpanCreate Create, SpanDecode Decode);

    private static SpanEncoder BindEncoder(Assembly assembly, string symbology, string path)
        => new(BindSizing(assembly, symbology, path), BindSpanCreate(assembly, symbology, path), BindDecode(assembly, symbology, image: false, path));

    /// <summary>Sizes, writes and decodes <paramref name="c"/> through <paramref name="assembly"/>'s span API.</summary>
    private static EncodeOutcome Encode(Assembly assembly, string path, SpanCase c)
        => Encode(assembly, c, BindEncoder(assembly, c.Symbology, path));

    private static EncodeOutcome Encode(Assembly assembly, SpanCase c, SpanEncoder encoder)
    {
        var options = Options(assembly, c.Symbology, c.Options);
        var eccLevel = EccLevel(assembly, c.Symbology, c.EccLevel);
        if (!encoder.Size(c.Text, eccLevel, options, out var bufferSize, out var width, out var height))
            return new(false, 0, 0, 0, [], null, null);

        // A dirty destination, so that a module the encoder leaves unwritten shows.
        var matrix = new byte[bufferSize];
        matrix.AsSpan().Fill(0xA5);
        var written = encoder.Create(c.Text, eccLevel, matrix, options);
        if (written != bufferSize)
            throw new InvalidOperationException($"{c.Name}: wrote {written} bytes where sizing gave {bufferSize}");

        var (text, info) = Decode(encoder.Decode, matrix, width, height);
        return new(true, bufferSize, width, height, matrix, text, info);
    }

    private static (string? Text, string? Info) DecodeImage(Assembly assembly, string path, string symbology, byte[] luminance, int width, int height)
        => Decode(BindDecode(assembly, symbology, image: true, path), luminance, width, height);

    private static (string? Text, string? Info) Decode(SpanDecode decode, byte[] input, int width, int height, int destinationLength = DecodedChars)
    {
        var chars = new char[destinationLength];
        var info = new object?[1];
        var decoded = decode(input, width, height, chars, out var written, info);
        return (decoded ? new string(chars, 0, written) : null, info[0]?.ToString());
    }

    private static (byte[] Luminance, int Width, int Height) Render(byte[] modules, int width, int height, int pixelsPerModule, bool inverted)
    {
        byte dark = inverted ? (byte)255 : (byte)0;
        var (w, h) = (width * pixelsPerModule, height * pixelsPerModule);
        var luminance = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
                luminance[y * w + x] = modules[y / pixelsPerModule * width + x / pixelsPerModule] != 0 ? dark : (byte)(255 - dark);
        }
        return (luminance, w, h);
    }

    /// <summary>A <c>{symbology}CodeGeneratorOptions</c> of <paramref name="assembly"/> with each property set: an enum by member name, a function of the property type for anything else that is not a plain value.</summary>
    private static object Options(Assembly assembly, string symbology, (string Property, object Value)[] settings)
    {
        var type = assembly.GetType($"FeatherQR.{symbology}CodeGeneratorOptions", throwOnError: true)!;
        var options = Activator.CreateInstance(type)!;
        foreach (var (name, value) in settings)
        {
            var property = type.GetProperty(name)!;
            property.SetValue(options, value switch
            {
                string member when property.PropertyType.IsEnum => Enum.Parse(property.PropertyType, member),
                Func<Type, object> make => make(property.PropertyType),
                _ => value,
            });
        }
        return options;
    }

    private static int EccLevel(Assembly assembly, string symbology, string name)
        => Convert.ToInt32(Enum.Parse(assembly.GetType($"FeatherQR.{symbology}EccLevel", throwOnError: true)!, name), System.Globalization.CultureInfo.InvariantCulture);

    private static Func<Type, object> Fnc1First() => fnc1 => fnc1.GetProperty("FirstPosition")!.GetValue(null)!;

    private static Func<Type, object> Fnc1Second(int applicationIndicator) => fnc1 => fnc1.GetMethod("SecondPosition", [typeof(int)])!.Invoke(null, [applicationIndicator])!;

    /// <summary>The core's build for <paramref name="targetFramework"/>, loaded once into a load context of its own. A stale build skips the case.</summary>
    private static async Task<(Assembly Build, string Path)> LoadBuild(string targetFramework)
    {
        var path = CoreAssemblyDependencyTest.FindCoreBuild(targetFramework);
        await Assert.That(path).IsNotNull()
            .Because($"no FeatherQR.dll for {targetFramework} under src/FeatherQR/bin; build the solution (dotnet build) before running the tests");
        CoreAssemblyDependencyTest.SkipIfStale(path!);
        return (Builds.GetOrAdd(path!, p => new Lazy<Assembly>(() => new AssemblyLoadContext("FeatherQR " + targetFramework).LoadFromStream(new MemoryStream(File.ReadAllBytes(p))))).Value, path!);
    }

    private static byte[] HostQR(string text, int quietZone, bool optimal)
    {
        var options = new QRCodeGeneratorOptions { QuietZoneSize = quietZone, Segmentation = optimal ? QRSegmentation.Optimal : QRSegmentation.Single };
        var matrix = new byte[Sizing.Required(text.AsSpan(), QREccLevel.M, options).BufferSize];
        QRCodeGenerator.Create(text.AsSpan(), QREccLevel.M, matrix, options);
        return matrix;
    }

    private static byte[] HostMicroQR(string text, int quietZone, bool optimal)
    {
        var options = new MicroQRCodeGeneratorOptions { QuietZoneSize = quietZone, Segmentation = optimal ? MicroQRSegmentation.Optimal : MicroQRSegmentation.Single };
        var matrix = new byte[Sizing.Required(text.AsSpan(), MicroQREccLevel.L, options).BufferSize];
        MicroQRCodeGenerator.Create(text.AsSpan(), MicroQREccLevel.L, matrix, options);
        return matrix;
    }

    private static byte[] HostRmQR(string text, int quietZone, bool optimal)
    {
        var options = new RmQRCodeGeneratorOptions { QuietZoneSize = quietZone, Segmentation = optimal ? RmQRSegmentation.Optimal : RmQRSegmentation.Single };
        var matrix = new byte[Sizing.Required(text.AsSpan(), RmQREccLevel.M, options).BufferSize];
        RmQRCodeGenerator.Create(text.AsSpan(), RmQREccLevel.M, matrix, options);
        return matrix;
    }

    /// <summary>The loaded build's <c>{symbology}CodeGenerator.Create(ReadOnlySpan&lt;char&gt;, ecc, Span&lt;byte&gt;, in options)</c>, taking arrays.</summary>
    private static SpanCreate BindSpanCreate(Assembly build, string symbology, string path)
    {
        var generator = build.GetType($"FeatherQR.{symbology}CodeGenerator", throwOnError: true)!;
        var eccLevel = build.GetType($"FeatherQR.{symbology}EccLevel", throwOnError: true)!;
        var options = build.GetType($"FeatherQR.{symbology}CodeGeneratorOptions", throwOnError: true)!;
        var target = generator.GetMethod("Create", [typeof(ReadOnlySpan<char>), eccLevel, typeof(Span<byte>), options.MakeByRefType()])
            ?? throw new InvalidOperationException($"{generator.FullName}.Create(ReadOnlySpan<char>, {eccLevel.Name}, Span<byte>, in {options.Name}) not found in {path}");

        var method = new DynamicMethod($"{symbology}.Create", typeof(int), [typeof(string), typeof(int), typeof(byte[]), typeof(object)], typeof(NetStandardBuildParityTest).Module, skipVisibility: true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(MemoryExtensions).GetMethod(nameof(MemoryExtensions.AsSpan), [typeof(string)])!);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Newobj, typeof(Span<byte>).GetConstructor([typeof(byte[])])!);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Unbox, options);
        il.Emit(OpCodes.Call, target);
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<SpanCreate>();
    }

    /// <summary>
    /// The loaded build's <c>{symbology}CodeGenerator.TryGetRequiredBufferSize(ReadOnlySpan&lt;char&gt;, ecc, out size, in options)</c>, giving the
    /// size's buffer length and sides. Standard QR's and Micro QR's square gives its side as both.
    /// </summary>
    private static SpanSizing BindSizing(Assembly build, string symbology, string path)
    {
        var generator = build.GetType($"FeatherQR.{symbology}CodeGenerator", throwOnError: true)!;
        var eccLevel = build.GetType($"FeatherQR.{symbology}EccLevel", throwOnError: true)!;
        var options = build.GetType($"FeatherQR.{symbology}CodeGeneratorOptions", throwOnError: true)!;
        var size = build.GetType($"FeatherQR.{symbology}CodeCalculatedSize", throwOnError: true)!;
        var target = generator.GetMethod("TryGetRequiredBufferSize", [typeof(ReadOnlySpan<char>), eccLevel, size.MakeByRefType(), options.MakeByRefType()])
            ?? throw new InvalidOperationException($"{generator.FullName}.TryGetRequiredBufferSize(ReadOnlySpan<char>, {eccLevel.Name}, out {size.Name}, in {options.Name}) not found in {path}");
        var sides = symbology == "RmQR" ? new[] { "Width", "Height" } : ["Size", "Size"];

        var method = new DynamicMethod($"{symbology}.TryGetRequiredBufferSize", typeof(bool),
            [typeof(string), typeof(int), typeof(object), typeof(int).MakeByRefType(), typeof(int).MakeByRefType(), typeof(int).MakeByRefType()], typeof(NetStandardBuildParityTest).Module, skipVisibility: true);
        var il = method.GetILGenerator();
        var calculated = il.DeclareLocal(size);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(MemoryExtensions).GetMethod(nameof(MemoryExtensions.AsSpan), [typeof(string)])!);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloca, calculated);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Unbox, options);
        il.Emit(OpCodes.Call, target);
        foreach (var (argument, property) in new[] { (3, "BufferSize"), (4, sides[0]), (5, sides[1]) })
        {
            il.Emit(OpCodes.Ldarg_S, (byte)argument);
            il.Emit(OpCodes.Ldloca, calculated);
            il.Emit(OpCodes.Call, size.GetProperty(property)!.GetMethod!);
            il.Emit(OpCodes.Stind_I4);
        }
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<SpanSizing>();
    }

    /// <summary>
    /// The loaded build's <c>{symbology}CodeDecoder.TryDecode</c> over a matrix, or <c>TryDecodeImage</c> over a luminance image, writing to a
    /// <c>Span&lt;char&gt;</c>. A square matrix takes one side, so the height is dropped there. The decode info is boxed into <c>info[0]</c>
    /// only when <c>info</c> is given, so that a measured call boxes nothing.
    /// </summary>
    private static SpanDecode BindDecode(Assembly build, string symbology, bool image, string path)
    {
        var decoder = build.GetType($"FeatherQR.{symbology}CodeDecoder", throwOnError: true)!;
        var info = build.GetType($"FeatherQR.{symbology}CodeDecodeInfo", throwOnError: true)!;
        var twoSides = image || symbology == "RmQR";
        Type[] parameters = twoSides
            ? [typeof(ReadOnlySpan<byte>), typeof(int), typeof(int), typeof(Span<char>), typeof(int).MakeByRefType(), info.MakeByRefType()]
            : [typeof(ReadOnlySpan<byte>), typeof(int), typeof(Span<char>), typeof(int).MakeByRefType(), info.MakeByRefType()];
        var name = image ? "TryDecodeImage" : "TryDecode";
        var target = decoder.GetMethod(name, parameters)
            ?? throw new InvalidOperationException($"{decoder.FullName}.{name}({string.Join(", ", parameters.Select(p => p.Name))}) not found in {path}");

        var method = new DynamicMethod($"{symbology}.{name}", typeof(bool),
            [typeof(byte[]), typeof(int), typeof(int), typeof(char[]), typeof(int).MakeByRefType(), typeof(object[])], typeof(NetStandardBuildParityTest).Module, skipVisibility: true);
        var il = method.GetILGenerator();
        var decoded = il.DeclareLocal(info);
        var succeeded = il.DeclareLocal(typeof(bool));
        var done = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Newobj, typeof(ReadOnlySpan<byte>).GetConstructor([typeof(byte[])])!);
        il.Emit(OpCodes.Ldarg_1);
        if (twoSides)
            il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Newobj, typeof(Span<char>).GetConstructor([typeof(char[])])!);
        il.Emit(OpCodes.Ldarg_S, (byte)4);
        il.Emit(OpCodes.Ldloca, decoded);
        il.Emit(OpCodes.Call, target);
        il.Emit(OpCodes.Stloc, succeeded);
        il.Emit(OpCodes.Ldarg_S, (byte)5);
        il.Emit(OpCodes.Brfalse_S, done);
        il.Emit(OpCodes.Ldarg_S, (byte)5);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, decoded);
        il.Emit(OpCodes.Box, info);
        il.Emit(OpCodes.Stelem_Ref);
        il.MarkLabel(done);
        il.Emit(OpCodes.Ldloc, succeeded);
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<SpanDecode>();
    }
}
