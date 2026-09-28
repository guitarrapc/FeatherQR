// Which SIMD tier each kernel runs in this WebAssembly build, held to the table for --simd-class
// (Wasm); the exit code is the report's, which Node returns as its own.
// With --time, it times the benchmark shapes on this build instead (TierTiming).
if (TierTiming.TryRun(args, out var timingExit))
    return timingExit;
if (!SimdReport.TryParseClass(args, out var simdClass))
    return 2;
return SimdReport.PrintAndCheck(simdClass);
