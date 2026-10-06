using System.Diagnostics;
using System.Globalization;
using System.Text;

/// <summary>
/// One case of the protocol made into a call. The input is read and converted here, before any timing, so the timed call does only QR work.
/// </summary>
/// <param name="call">The timed unit of work. Its value is folded into the checksum, so the work cannot be dropped. It is never 0 for a call that succeeds and always 0 for one that fails, so the loop counts the calls that failed.</param>
/// <param name="describe">Makes one call and returns its result as protocol JSON members: the status, then the decoded text or the encoded matrix.</param>
internal sealed class Operation(Func<ulong> call, Func<string> describe)
{
    public Func<ulong> Call { get; } = call;
    public Func<string> Describe { get; } = describe;
}

/// <summary>
/// The protocol's modes, timing loop and JSON for the .NET CLIs: every library's CLI speaks the same protocol and runs the same loop,
/// described in .github/docs/plans/cross-language-benchmark-plan.md ("Protocol"), so this file is the one to port.
/// </summary>
/// <remarks>
///   &lt;cli&gt; &lt;mode&gt; &lt;op&gt; &lt;symbology&gt; &lt;input&gt; [--ecc E] [--version V] [options]
///     mode       run | fixed | cold | noop
///     op         encode | decode-matrix | decode-image
///     symbology  qr | microqr | rmqr
///   run:   --warmup-ms 3000 --batch-ms 20 --batches 30
///   fixed: --iterations N
/// </remarks>
internal static class Protocol
{
    /// <param name="identity">The JSON members naming the library, its version, the runtime and the build, without the braces.</param>
    /// <param name="load">Makes the operation from op, symbology, input, ecc and version, and throws <see cref="NotSupportedException"/> for one the library does not offer.</param>
    public static int Run(string[] args, string identity, Func<string, string, string, string?, string?, Operation> load)
    {
        if (args.Length < 4)
            return Usage();

        var (mode, op, symbology, input) = (args[0], args[1], args[2], args[3]);
        var json = new StringBuilder("{\"protocol\":1,").Append(identity);
        json.Append(CultureInfo.InvariantCulture, $",\"mode\":\"{mode}\"");

        Operation operation;
        try
        {
            operation = load(op, symbology, input, Option("--ecc"), Option("--version"));
        }
        catch (NotSupportedException)
        {
            Console.Out.Write(json.Append(",\"status\":\"unsupported\"}\n"));
            return 0;
        }

        // Verification checks one call. A library can still fail the calls after it, for example by changing its input, so every timed call
        // that fails is counted, and the collector rejects a process with any.
        ulong sink = 0;
        long failed = 0;
        switch (mode)
        {
            case "run":
                {
                    var described = operation.Describe();
                    json.Append(',').Append(described);
                    // An input the library cannot handle is reported, not timed: a failing call costs what failing costs.
                    if (described.StartsWith("\"status\":\"failed\"", StringComparison.Ordinal))
                        break;
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
                        var value = call();
                        sink += value;
                        if (value == 0)
                            failed++;
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
                        {
                            var value = call();
                            sink += value;
                            if (value == 0)
                                failed++;
                        }
                        samples[b] = Stopwatch.GetTimestamp() - t0;
                    }

                    json.Append(CultureInfo.InvariantCulture, $",\"warmupCalls\":{calls},\"warmupNs\":{Nanoseconds(elapsed)},\"batchCalls\":{batchCalls},\"batchNs\":[");
                    for (var b = 0; b < batches; b++)
                        json.Append(CultureInfo.InvariantCulture, $"{(b == 0 ? "" : ",")}{Nanoseconds(samples[b])}");
                    json.Append(CultureInfo.InvariantCulture, $"],\"failedCalls\":{failed}");
                    break;
                }
            case "fixed":
                {
                    var iterations = long.Parse(Option("--iterations") ?? throw new ArgumentException("fixed needs --iterations."), CultureInfo.InvariantCulture);
                    var call = operation.Call;
                    for (var k = 0L; k < iterations; k++)
                    {
                        var value = call();
                        sink += value;
                        if (value == 0)
                            failed++;
                    }
                    json.Append(CultureInfo.InvariantCulture, $",\"status\":\"ok\",\"iterations\":{iterations},\"failedCalls\":{failed}");
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
    }

    private static long Nanoseconds(long ticks) => (long)(ticks * (1e9 / Stopwatch.Frequency));

    private static int Usage()
    {
        Console.Error.WriteLine("usage: <cli> <run|fixed|cold|noop> <encode|decode-matrix|decode-image> <qr|microqr|rmqr> <input> [--ecc E] [--version V] [--warmup-ms 3000 --batch-ms 20 --batches 30 | --iterations N]");
        return 2;
    }
}
