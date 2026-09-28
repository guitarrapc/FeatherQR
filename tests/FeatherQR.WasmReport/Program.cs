// Which SIMD tier each kernel runs in this WebAssembly build, held to the table for --simd-class
// (Wasm); the exit code is the report's, which Node returns as its own.
if (!SimdReport.TryParseClass(args, out var simdClass))
    return 2;
return SimdReport.PrintAndCheck(simdClass);
