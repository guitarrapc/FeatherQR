using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

// zxing-cpp's .NET package (ZXingCpp) as a CLI of the cross-language benchmark: the protocol in ../cli/Protocol.cs over ZXingCppOperations.
//
//   zxingcpp-net-cli <mode> <op> <symbology> <input> [--ecc E] [--version V] [options]

var package = typeof(ZXingCpp.BarcodeReader).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
var identity = string.Create(CultureInfo.InvariantCulture,
    $"\"library\":\"ZXingCpp\",\"libraryVersion\":\"{package}\",\"runtime\":\"{RuntimeInformation.FrameworkDescription} {RuntimeInformation.RuntimeIdentifier}\",\"build\":\"jit\"");
return Protocol.Run(args, identity, ZXingCppOperations.Load);
