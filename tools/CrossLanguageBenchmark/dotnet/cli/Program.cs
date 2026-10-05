using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Text;
using FeatherQR;

// FeatherQR's CLI for the cross-language benchmark. The protocol and its timing loop are in Protocol.cs, which every library's CLI ports.
//
//   featherqr-cli <mode> <op> <symbology> <input> [--ecc E] [--version V] [options]

var identity = new StringBuilder("\"library\":\"FeatherQR\"");
identity.Append(CultureInfo.InvariantCulture, $",\"libraryVersion\":\"{typeof(QRCodeGenerator).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}\"");
identity.Append(CultureInfo.InvariantCulture, $",\"runtime\":\"{RuntimeInformation.FrameworkDescription} {RuntimeInformation.RuntimeIdentifier}\"");
identity.Append(CultureInfo.InvariantCulture, $",\"build\":\"{Build()}\",\"vector256\":{Flag(Vector256.IsHardwareAccelerated)}");
// What this build's code sees, for the instruction-set probe: under NativeAOT, a set outside the target that is not checked at run time reads false on a CPU that has it.
identity.Append(CultureInfo.InvariantCulture, $",\"isa\":{{\"vector128\":{Flag(Vector128.IsHardwareAccelerated)},\"vectorByteCount\":{Vector<byte>.Count}");
identity.Append(CultureInfo.InvariantCulture, $",\"avx2\":{Flag(Avx2.IsSupported)},\"bmi2X64\":{Flag(Bmi2.X64.IsSupported)},\"gfni\":{Flag(Gfni.IsSupported)},\"avx512f\":{Flag(Avx512F.IsSupported)}");
identity.Append(CultureInfo.InvariantCulture, $",\"advSimd\":{Flag(AdvSimd.IsSupported)},\"dp\":{Flag(Dp.IsSupported)}}}");

return Protocol.Run(args, identity.ToString(), Operations.Load);

static string Flag(bool value) => value ? "true" : "false";

// "jit", or "nativeaot" and the instruction-set target the publish stamped (FeatherQRCli.csproj).
static string Build() => RuntimeFeature.IsDynamicCodeSupported
    ? "jit"
    : $"nativeaot {typeof(Operation).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "IlcInstructionSet")?.Value ?? "default"}";
