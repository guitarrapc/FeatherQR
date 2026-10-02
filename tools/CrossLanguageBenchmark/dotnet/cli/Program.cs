using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;
using FeatherQR;

// FeatherQR's CLI for the cross-language benchmark. Every library's CLI speaks the same protocol and runs the same timing loop,
// both described in .github/docs/plans/cross-language-benchmark-plan.md ("Protocol"), so this file is the one to port.
//
//   featherqr-cli <mode> <op> <symbology> <input> [--ecc E] [--version V] [options]
//     mode       run | fixed | cold | noop
//     op         encode | decode-matrix | decode-image
//     symbology  qr | microqr | rmqr
//   run:   --warmup-ms 3000 --batch-ms 20 --batches 30
//   fixed: --iterations N

if (args.Length < 4)
    return Usage();

var (mode, op, symbology, input) = (args[0], args[1], args[2], args[3]);
var json = new StringBuilder("{\"protocol\":1,\"library\":\"FeatherQR\"");
json.Append(CultureInfo.InvariantCulture, $",\"libraryVersion\":\"{typeof(QRCodeGenerator).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}\"");
json.Append(CultureInfo.InvariantCulture, $",\"runtime\":\"{RuntimeInformation.FrameworkDescription} {RuntimeInformation.RuntimeIdentifier}\"");
json.Append(CultureInfo.InvariantCulture, $",\"build\":\"{(RuntimeFeature.IsDynamicCodeSupported ? "jit" : "nativeaot")}\",\"vector256\":{(Vector256.IsHardwareAccelerated ? "true" : "false")}");
json.Append(CultureInfo.InvariantCulture, $",\"mode\":\"{mode}\"");

Operation operation;
try
{
    operation = Operations.Load(op, symbology, input, Option("--ecc"), Option("--version"));
}
catch (NotSupportedException)
{
    Console.Out.Write(json.Append(",\"status\":\"unsupported\"}\n"));
    return 0;
}

ulong sink = 0;
switch (mode)
{
    case "run":
        {
            json.Append(',').Append(operation.Describe());
            var warmupMs = double.Parse(Option("--warmup-ms") ?? "3000", CultureInfo.InvariantCulture);
            var batchMs = double.Parse(Option("--batch-ms") ?? "20", CultureInfo.InvariantCulture);
            var batches = int.Parse(Option("--batches") ?? "30", CultureInfo.InvariantCulture);
            var call = operation.Call;

            // Warm up for the stated time and at least 3 calls. The batch size comes from the warmup's second half, after the first tiers.
            var warmupTicks = (long)(warmupMs * Stopwatch.Frequency / 1000);
            long calls = 0, halfCalls = 0, halfTicks = 0, elapsed;
            var start = Stopwatch.GetTimestamp();
            do
            {
                sink += call();
                calls++;
                elapsed = Stopwatch.GetTimestamp() - start;
                if (halfCalls == 0 && elapsed >= warmupTicks / 2)
                    (halfCalls, halfTicks) = (calls, elapsed);
            } while (calls < 3 || elapsed < warmupTicks);
            var perCall = calls > halfCalls ? (double)(elapsed - halfTicks) / (calls - halfCalls) : (double)elapsed / calls;
            var batchCalls = Math.Max(1L, (long)(batchMs * Stopwatch.Frequency / 1000 / perCall));

            var samples = new long[batches];
            for (var b = 0; b < batches; b++)
            {
                var t0 = Stopwatch.GetTimestamp();
                for (var k = 0L; k < batchCalls; k++)
                    sink += call();
                samples[b] = Stopwatch.GetTimestamp() - t0;
            }

            json.Append(CultureInfo.InvariantCulture, $",\"warmupCalls\":{calls},\"warmupNs\":{Nanoseconds(elapsed)},\"batchCalls\":{batchCalls},\"batchNs\":[");
            for (var b = 0; b < batches; b++)
                json.Append(CultureInfo.InvariantCulture, $"{(b == 0 ? "" : ",")}{Nanoseconds(samples[b])}");
            json.Append(']');
            break;
        }
    case "fixed":
        {
            var iterations = long.Parse(Option("--iterations") ?? throw new ArgumentException("fixed needs --iterations."), CultureInfo.InvariantCulture);
            var call = operation.Call;
            for (var k = 0L; k < iterations; k++)
                sink += call();
            json.Append(CultureInfo.InvariantCulture, $",\"status\":\"ok\",\"iterations\":{iterations}");
            break;
        }
    case "cold":
        json.Append(',').Append(operation.Describe());
        break;
    case "noop":
        json.Append(",\"status\":\"ok\"");
        break;
    default:
        return Usage();
}

Console.Out.Write(json.Append(CultureInfo.InvariantCulture, $",\"checksum\":\"{sink}\"}}\n"));
return 0;

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static long Nanoseconds(long ticks) => (long)(ticks * (1e9 / Stopwatch.Frequency));

static int Usage()
{
    Console.Error.WriteLine("usage: featherqr-cli <run|fixed|cold|noop> <encode|decode-matrix|decode-image> <qr|microqr|rmqr> <input> [--ecc E] [--version V] [--warmup-ms 3000 --batch-ms 20 --batches 30 | --iterations N]");
    return 2;
}
