// Which SIMD tier each kernel runs in this WebAssembly build, held to the table for --simd-class
// (Wasm); with --parity, the vector tiers held to their scalar forms here, where the PackedSimd
// operations they take run. The exit code is the report's, which Node returns as its own.
// With --time, it times the benchmark shapes on this build instead (TierTiming).
if (TierTiming.TryRun(args, out var timingExit))
    return timingExit;
if (!SimdReport.TryParseClass(args, out var simdClass))
    return 2;
var tierResult = SimdReport.PrintAndCheck(simdClass);
var parityResult = SimdParity.Requested(args) ? SimdParity.Run() : 0;
return tierResult != 0 ? tierResult : parityResult;
